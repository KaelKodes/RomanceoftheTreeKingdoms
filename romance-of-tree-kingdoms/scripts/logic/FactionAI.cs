using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class FactionAI : Node
{
    public static FactionAI Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
    }

    public void RepositionOfficers(SqliteConnection conn, int factionId)
    {
        GD.Print($"[Repositioning] Faction {factionId} is reorganizing for the week...");

        // 1. Fetch Cities
        var cities = new List<AI_City>();
        var cityCmd = conn.CreateCommand();
        cityCmd.CommandText = "SELECT city_id, name, is_hq, governor_id FROM cities WHERE faction_id = $fid";
        cityCmd.Parameters.AddWithValue("$fid", factionId);
        using (var reader = cityCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                cities.Add(new AI_City
                {
                    CityId = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    IsHQ = reader.GetBoolean(2),
                    GovernorId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
                });
            }
        }

        if (cities.Count == 0) return;

        // 2. Identify Frontline Cities (Adjacent to foreign or neutral cities)
        foreach (var city in cities)
        {
            var adjCmd = conn.CreateCommand();
            adjCmd.CommandText = @"
                SELECT COUNT(*) FROM routes r
                JOIN cities c ON (r.start_city_id = c.city_id OR r.end_city_id = c.city_id)
                WHERE (r.start_city_id = $cid OR r.end_city_id = $cid)
                AND c.city_id != $cid
                AND (c.faction_id IS NULL OR c.faction_id != $fid)";
            adjCmd.Parameters.AddWithValue("$cid", city.CityId);
            adjCmd.Parameters.AddWithValue("$fid", factionId);
            city.IsFrontline = (long)adjCmd.ExecuteScalar() > 0;
        }

        // 3. Fetch All Officers of this faction
        var officers = new List<AI_Officer_Stats>();
        var offCmd = conn.CreateCommand();
        offCmd.CommandText = "SELECT officer_id, name, strength, politics, intelligence, leadership, is_commander FROM officers WHERE faction_id = $fid AND is_player = 0";
        offCmd.Parameters.AddWithValue("$fid", factionId);
        using (var reader = offCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var o = new AI_Officer_Stats
                {
                    OfficerId = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Strength = reader.GetInt32(2),
                    Politics = reader.IsDBNull(3) ? 50 : reader.GetInt32(3),
                    Intelligence = reader.IsDBNull(4) ? 50 : reader.GetInt32(4),
                    Leadership = reader.IsDBNull(5) ? 50 : reader.GetInt32(5),
                    IsCommander = reader.IsDBNull(6) ? false : (reader.GetInt32(6) == 1)
                };
                // Categorize: Warriors prefer Strength/Leadership, Bureaucrats prefer Politics/Intelligence
                o.IsWarrior = (o.Strength + o.Leadership) >= (o.Politics + o.Intelligence);
                officers.Add(o);
            }
        }

        // 4. Redistribution Algorithm
        var assignments = new Dictionary<int, int>(); // OfficerID -> CityID
        var remainingOfficers = new List<AI_Officer_Stats>(officers);

        // Step A: Governors must be at their post (Top Priority)
        foreach (var city in cities)
        {
            if (city.GovernorId > 0)
            {
                var gov = remainingOfficers.FirstOrDefault(o => o.OfficerId == city.GovernorId);
                if (gov != null)
                {
                    assignments[gov.OfficerId] = city.CityId;
                    remainingOfficers.Remove(gov);
                }
            }
        }

        // Step A2: Faction Leader should stay at HQ (unless already assigned as Governor)
        var fLeader = remainingOfficers.FirstOrDefault(o => o.IsCommander);
        if (fLeader != null)
        {
            var hqCity = cities.FirstOrDefault(c => c.IsHQ) ?? cities.FirstOrDefault();
            if (hqCity != null)
            {
                assignments[fLeader.OfficerId] = hqCity.CityId;
                remainingOfficers.Remove(fLeader);
            }
        }


        var frontlineCities = cities.Where(c => c.IsFrontline).ToList();
        var safeCities = cities.Where(c => !c.IsFrontline).ToList();

        // If no specific frontline found (isolated kingdom?), treat all as safe
        if (frontlineCities.Count == 0) frontlineCities = cities;

        // Step B: Warriors to the Frontline
        var warriors = remainingOfficers.Where(o => o.IsWarrior).OrderByDescending(o => o.Strength).ToList();
        if (frontlineCities.Count > 0)
        {
            int fIdx = 0;
            foreach (var w in warriors)
            {
                assignments[w.OfficerId] = frontlineCities[fIdx].CityId;
                remainingOfficers.Remove(w);
                fIdx = (fIdx + 1) % frontlineCities.Count;
            }
        }

        // Step C: Bureaucrats and Leftovers to Safe Cities (or HQ)
        var safeTargets = safeCities.Count > 0 ? safeCities : cities;
        int sIdx = 0;
        foreach (var off in remainingOfficers)
        {
            assignments[off.OfficerId] = safeTargets[sIdx].CityId;
            sIdx = (sIdx + 1) % safeTargets.Count;
        }

        // 5. Execute Moves
        using (var trans = conn.BeginTransaction())
        {
            foreach (var kvp in assignments)
            {
                var moveCmd = conn.CreateCommand();
                moveCmd.CommandText = "UPDATE officers SET location_id = $loc WHERE officer_id = $oid";
                moveCmd.Parameters.AddWithValue("$loc", kvp.Value);
                moveCmd.Parameters.AddWithValue("$oid", kvp.Key);
                moveCmd.ExecuteNonQuery();
            }
            trans.Commit();
        }

        GD.Print($"[Repositioning] Faction {factionId} reorganized {assignments.Count} officers across {cities.Count} cities.");
    }

    private class AI_City { public int CityId; public string Name; public bool IsHQ; public bool IsFrontline; public int GovernorId; }
    private class AI_Officer_Stats { public int OfficerId; public string Name; public int Strength; public int Politics; public int Intelligence; public int Leadership; public bool IsCommander; public bool IsWarrior; }

    public async void ProcessTurn(int factionId)
    {
        try
        {
            GD.Print($"--- Faction {factionId} thinking for the day ---");

            using (var conn = DatabaseHelper.GetConnection())
            {
                conn.Open();
                EnsureGoalsExist(conn, factionId);
                AssignOfficerTasks(conn, factionId);

                var officers = GetOfficersWithAssignments(conn, factionId);
                var leader = GetFactionLeader(conn, factionId);

                // 1. Leader takes their turn first (Execute assignment if they have one)
                var leaderOff = officers.FirstOrDefault(o => o.OfficerId == leader?.OfficerId);
                if (leaderOff != null)
                {
                    ExecuteAssignment(conn, leaderOff);
                    officers.Remove(leaderOff);
                }

                // 2. The rest of the AI in that Faction take their turns
                foreach (var off in officers)
                {
                    ExecuteAssignment(conn, off);
                }
            }

            await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
            GetNodeOrNull<TurnManager>("/root/TurnManager")?.AIEndTurn();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[FactionAI] Error in ProcessTurn: {ex.Message}");
            GetNodeOrNull<TurnManager>("/root/TurnManager")?.AIEndTurn();
        }
    }

    private void EnsureGoalsExist(SqliteConnection conn, int factionId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT monthly_goal, weekly_task FROM factions WHERE faction_id = $fid";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.IsDBNull(0)) UpdateMonthlyGoal(conn, factionId);
                if (reader.IsDBNull(1)) UpdateWeeklyTask(conn, factionId);
            }
        }
    }

    public void UpdateMonthlyGoal(SqliteConnection conn, int factionId)
    {
        var leader = GetFactionLeader(conn, factionId);
        if (leader == null) return;

        string goal = "Prosper";
        if (leader.Strength > 65 || leader.Leadership > 65) goal = "Conquest";
        else if (leader.Politics > 70 || leader.Intelligence > 70) goal = "Prosper";
        else goal = "Stability";

        ExecuteSql(conn, $"UPDATE factions SET monthly_goal = '{goal}' WHERE faction_id = {factionId}");
        GD.Print($"[AI Strategy] Faction {factionId} Leader {leader.Name} sets MONTHLY GOAL: {goal}");
    }

    public void UpdateWeeklyTask(SqliteConnection conn, int factionId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT monthly_goal FROM factions WHERE faction_id = $fid";
        cmd.Parameters.AddWithValue("$fid", factionId);
        string monthlyGoal = cmd.ExecuteScalar()?.ToString() ?? "Prosper";

        string task = "DevelopEconomy";
        int targetId = 0;

        switch (monthlyGoal)
        {
            case "Conquest":
                targetId = FindExpansionTarget(conn, factionId);
                task = targetId > 0 ? "CaptureCity" : "Recruit";
                break;
            case "Prosper":
                if (GetIdleOfficers(conn, factionId).Count < 5) task = "RecruitOfficer";
                else task = GetNextDomesticTask(conn, factionId);
                break;
            case "Stability":
            default:
                task = (new Random().NextDouble() > 0.6) ? "RecruitOfficer" : "Fortify";
                break;
        }

        var update = conn.CreateCommand();
        update.CommandText = "UPDATE factions SET weekly_task = $task, goal_target_id = $tid WHERE faction_id = $fid";
        update.Parameters.AddWithValue("$task", task);
        update.Parameters.AddWithValue("$tid", targetId);
        update.Parameters.AddWithValue("$fid", factionId);
        update.ExecuteNonQuery();

        GD.Print($"[AI Strategy] Faction {factionId} sets WEEKLY TASK: {task}");
    }

    private void AssignOfficerTasks(SqliteConnection conn, int factionId)
    {
        var leader = GetFactionLeader(conn, factionId);
        if (leader == null || leader.ActionPoints <= 0) return;

        var weeklyTask = GetWeeklyTask(conn, factionId);
        var idle = GetIdleOfficers(conn, factionId);

        // Prioritize: Subordinates first, then Commander (Delegate if possible)
        var candidates = idle.OrderBy(o => o.IsCommander).ThenByDescending(o => o.Strength).ToList();

        foreach (var off in candidates)
        {
            if (leader.ActionPoints <= 0) break;

            string task = weeklyTask.task;
            int targetId = weeklyTask.targetId;

            if (weeklyTask.task == "CaptureCity")
            {
                int cityTroops = GetCityDefenseStrength(conn, targetId);
                int neededTroops = (int)(cityTroops * 1.3f); // Aim for 30% superiority

                // Get current force assigned to this target
                int currentAssignedTroops = 0;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT SUM(troops) FROM officers WHERE (current_assignment = 'CaptureCity' OR current_assignment = 'SupportAttack') AND assignment_target_id = $tid";
                    countCmd.Parameters.AddWithValue("$tid", targetId);
                    var currentForce = countCmd.ExecuteScalar();
                    currentAssignedTroops = (currentForce != null && currentForce != DBNull.Value) ? Convert.ToInt32(currentForce) : 0;
                }

                if (currentAssignedTroops >= neededTroops && currentAssignedTroops > 0)
                {
                    task = "DevelopEconomy"; // Diversify if we have enough force
                }
                else
                {
                    // Logic: If I am the Commander OR I am very strong, AND no one else is leading yet, I lead.
                    // Simplified: First one assigned becomes 'CaptureCity' (Leader), others 'SupportAttack'.
                    // Since we sorted candidates, the Commander (if available) naturally goes first.

                    using (var leaderCheck = conn.CreateCommand())
                    {
                        leaderCheck.CommandText = "SELECT COUNT(*) FROM officers WHERE current_assignment = 'CaptureCity' AND assignment_target_id = $tid";
                        leaderCheck.Parameters.AddWithValue("$tid", targetId);
                        if ((long)leaderCheck.ExecuteScalar() == 0) task = "CaptureCity";
                        else task = "SupportAttack";
                    }
                }
            }

            // Execute Assignment Update
            // Note: Use the Officer's own AP for the task execution later, but Leader AP is used for *Giving Orders*.
            // BUT: If the Leader is assigning themselves, do they pay double?
            // Convention: Leader pays 1 AP to "Order" a subordinate.
            // Self-Ordering: Usually free or just costs the execution AP.
            // Let's say Command Cost is 1 AP per order issued.

            ExecuteSql(conn, $"UPDATE officers SET current_assignment = '{task}', assignment_target_id = {targetId} WHERE officer_id = {off.OfficerId}");

            // Deduct Leader AP for issuing the order to OTHERS
            // Self-Assignment is "free" to order (but costs execution AP later)
            if (off.OfficerId != leader.OfficerId)
            {
                ExecuteSql(conn, $"UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = {leader.OfficerId}");
                leader.ActionPoints--;
            }

            // Safety: If Leader is assigned a task, they need 1 AP to execute it.
            // Don't let them spend their last point on ordering a subordinate.
            // We check candidates list to see if leader is in the pool (they are prioritized first so likely already assigned)
            bool leaderIsAssigned = candidates.Any(c => c.OfficerId == leader.OfficerId);
            if (leaderIsAssigned && leader.ActionPoints <= 1) break;
        }

        // Leader AP conservation: We do NOT burn remaining AP on domestic tasks here.
        // Doing so would prevent the Leader from executing their assigned task (e.g. CaptureCity) in the main loop.
        // Future improvement: Move 'Burn unused AP' logic to the end of the turn or into ExecuteAssignment.
    }

    private void ExecuteAssignment(SqliteConnection conn, AI_Officer officer)
    {
        if (officer.ActionPoints <= 0) return;
        var am = GetNode<ActionManager>("/root/ActionManager");

        switch (officer.Assignment)
        {
            case "CaptureCity":
            case "SupportAttack":
                if (IsAdjacent(conn, officer.LocationId, officer.AssignmentTarget))
                {
                    if (officer.Assignment == "CaptureCity")
                        am.DeclareAttack(officer.OfficerId, officer.AssignmentTarget);
                    else
                        am.PerformRest(officer.OfficerId); // Wait for the leader to declare
                }
                else
                {
                    // TRAVEL TO FRONT: Move one hop closer to target
                    int nextHop = FindNextHopToward(conn, officer.LocationId, officer.AssignmentTarget, officer.OfficerId);
                    if (nextHop > 0)
                        am.PerformMove(officer.OfficerId, nextHop);
                    else
                        am.PerformRest(officer.OfficerId);
                }
                break;
            case "DevelopEconomy":
                am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.Commerce);
                break;
            case "Research":
                am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.Technology);
                break;
            case "Fortify":
                am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.Defense);
                break;
            case "Cultivate":
                am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.Agriculture);
                break;
            case "RecruitOfficer":
                int roninId = FindRoninInCity(conn, officer.LocationId);
                if (roninId > 0)
                {
                    int gold = GetFactionGold(conn, officer.OfficerId);
                    if (gold < 100)
                    {
                        GD.Print($"[AI Budget] {officer.Name} cannot afford to 'Wine & Dine' {roninId}. Talking instead.");
                        am.PerformSocialTalk(officer.OfficerId, roninId);
                    }
                    else
                        am.PerformRecruit(officer.OfficerId, roninId);
                }
                else am.PerformRest(officer.OfficerId);
                break;
            case "Recruit":
                int currentGold = GetFactionGold(conn, officer.OfficerId);
                if (currentGold < 200)
                {
                    GD.Print($"[AI Budget] {officer.Name} cannot afford troops. Developing Economy instead.");
                    am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.Commerce);
                }
                else
                {
                    // AI Army Building: Fill up to max troops with a random unit type
                    var roles = new UnitRole[] { UnitRole.FRONTLINE, UnitRole.CAVALRY, UnitRole.RANGED };
                    UnitRole selected = roles[new Random().Next(roles.Length)];
                    am.PerformRecruitTroops(officer.OfficerId, selected);
                }
                break;
            default:
                if (new Random().NextDouble() > 0.7)
                {
                    int targetId = FindSocialTarget(conn, officer.OfficerId, officer.LocationId);
                    if (targetId > 0) am.PerformSocialTalk(officer.OfficerId, targetId);
                    else am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.PublicOrder);
                }
                else
                    am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.PublicOrder);
                break;
        }
    }

    private int FindSocialTarget(SqliteConnection conn, int officerId, int locationId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT officer_id FROM officers WHERE location_id = $loc AND officer_id != $oid LIMIT 5";
        cmd.Parameters.AddWithValue("$loc", locationId);
        cmd.Parameters.AddWithValue("$oid", officerId);
        var options = new List<int>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) options.Add(Convert.ToInt32(r.GetValue(0)));
        }
        return options.Count == 0 ? 0 : options[new Random().Next(options.Count)];
    }

    private int FindRoninInCity(SqliteConnection conn, int locationId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT officer_id FROM officers WHERE location_id = $loc AND faction_id IS NULL LIMIT 1";
        cmd.Parameters.AddWithValue("$loc", locationId);
        var res = cmd.ExecuteScalar();
        return res != null ? Convert.ToInt32(res) : 0;
    }

    public void ProcessRoninTurns()
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT o.officer_id, o.location_id, o.name, c.faction_id, f.name
                FROM officers o
                JOIN cities c ON o.location_id = c.city_id
                JOIN factions f ON c.faction_id = f.faction_id
                WHERE o.faction_id IS NULL AND c.faction_id IS NOT NULL";

            var candidates = new List<(int oid, int locId, string name, int fid, string fName)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    candidates.Add((Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1)), r.GetString(2), Convert.ToInt32(r.GetValue(3)), r.GetString(4)));
            }

            foreach (var c in candidates)
            {
                int relation = RelationshipManager.Instance.GetFactionRelation(c.oid, c.fid);
                if (relation >= 50 && new Random().NextDouble() > 0.90)
                {
                    GD.Print($"[Ronin Agency] {c.name} is impressed by Faction {c.fName} and offers service!");
                    int oldMax = GameConstants.TROOPS_VOLUNTEER;
                    int newMax = GameConstants.TROOPS_REGULAR;
                    int troopGain = newMax - oldMax;
                    ExecuteSql(conn, $"UPDATE officers SET faction_id = {c.fid}, rank = '{GameConstants.RANK_REGULAR}', max_troops = {newMax}, troops = troops + {troopGain} WHERE officer_id = {c.oid}");
                }
            }
        }
    }

    private int FindExpansionTarget(SqliteConnection conn, int factionId)
    {
        // 1. Get Faction Cities
        var myCities = new HashSet<int>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) myCities.Add(r.GetInt32(0));
        }

        if (myCities.Count == 0) return 0;

        // 2. Find Neighbors via Routes (C# Logic avoids complex JOIN/CASE issues)
        var neighbors = new HashSet<int>();
        var routeCmd = conn.CreateCommand();
        routeCmd.CommandText = "SELECT start_city_id, end_city_id FROM routes";

        // Optimally we would filter in SQL, but reading all routes is fast enough for <100 cities.
        // If map is huge, we should filter by start/end.
        // Let's filter by start OR end in SQL for efficiency.
        var cityList = string.Join(",", myCities);
        routeCmd.CommandText = $"SELECT start_city_id, end_city_id FROM routes WHERE start_city_id IN ({cityList}) OR end_city_id IN ({cityList})";

        using (var r = routeCmd.ExecuteReader())
        {
            while (r.Read())
            {
                int s = r.GetInt32(0);
                int e = r.GetInt32(1);

                if (myCities.Contains(s) && !myCities.Contains(e)) neighbors.Add(e);
                else if (myCities.Contains(e) && !myCities.Contains(s)) neighbors.Add(s);
            }
        }

        if (neighbors.Count == 0) return 0;

        // 3. Score Neighbors (Neutral first, then weak garrison)
        var neighborList = string.Join(",", neighbors);
        var targetCmd = conn.CreateCommand();
        targetCmd.CommandText = $@"
            SELECT city_id FROM cities 
            WHERE city_id IN ({neighborList}) 
            AND (faction_id != $fid OR faction_id IS NULL)
            ORDER BY (faction_id IS NULL) DESC, 
                     (SELECT COUNT(*) FROM officers WHERE location_id = cities.city_id) ASC
            LIMIT 1";
        targetCmd.Parameters.AddWithValue("$fid", factionId);

        var res = targetCmd.ExecuteScalar();
        int targetId = res != null && res != DBNull.Value ? Convert.ToInt32(res) : 0;
        if (targetId > 0)
        {
            // Double check ownership in case of race condition
            var ownCmd = conn.CreateCommand();
            ownCmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
            ownCmd.Parameters.AddWithValue("$cid", targetId);
            var fIdObj = ownCmd.ExecuteScalar();
            int fId = fIdObj != null && fIdObj != DBNull.Value ? Convert.ToInt32(fIdObj) : 0;
            if (fId == factionId)
            {
                GD.PrintErr($"[FactionAI] FindExpansionTarget selected OWN city {targetId}! Faction query error?");
                return 0;
            }
        }
        return targetId;
    }

    private (string task, int targetId) GetWeeklyTask(SqliteConnection conn, int factionId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT weekly_task, goal_target_id FROM factions WHERE faction_id = $fid";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read()) return (r.IsDBNull(0) ? "DevelopEconomy" : r.GetString(0), r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1)));
        }
        return ("DevelopEconomy", 0);
    }

    private List<AI_Officer> GetOfficersWithAssignments(SqliteConnection conn, int factionId)
    {
        var list = new List<AI_Officer>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT officer_id, name, location_id, current_action_points, current_assignment, assignment_target_id FROM officers WHERE faction_id = $fid AND current_assignment IS NOT NULL AND current_action_points > 0";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                list.Add(new AI_Officer
                {
                    OfficerId = Convert.ToInt32(r.GetValue(0)),
                    Name = r.IsDBNull(1) ? "Unknown" : r.GetString(1),
                    LocationId = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2)),
                    ActionPoints = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3)),
                    Assignment = r.IsDBNull(4) ? "" : r.GetString(4),
                    AssignmentTarget = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5))
                });
        }
        return list;
    }

    private bool IsAdjacent(SqliteConnection conn, int loc1, int loc2)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM routes WHERE (start_city_id = $l1 AND end_city_id = $l2) OR (start_city_id = $l2 AND end_city_id = $l1)";
        cmd.Parameters.AddWithValue("$l1", loc1);
        cmd.Parameters.AddWithValue("$l2", loc2);
        return (long)cmd.ExecuteScalar() > 0;
    }

    private void ExecuteSql(SqliteConnection conn, string sql)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private class AI_Leader { public string Name; public int Intelligence; public int Strength; public int Politics; public int Leadership; public int ActionPoints; public int OfficerId; }
    private class AI_Officer { public int OfficerId; public int LocationId; public string Name; public int ActionPoints; public string Assignment; public int AssignmentTarget; public int Strength; public bool IsCommander; }

    private AI_Leader GetFactionLeader(SqliteConnection conn, int factionId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, intelligence, strength, politics, leadership, current_action_points, officer_id FROM officers WHERE faction_id = $fid AND is_commander = 1 LIMIT 1";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
                return new AI_Leader
                {
                    Name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                    Intelligence = reader.IsDBNull(1) ? 50 : Convert.ToInt32(reader.GetValue(1)),
                    Strength = reader.IsDBNull(2) ? 50 : Convert.ToInt32(reader.GetValue(2)),
                    Politics = reader.IsDBNull(3) ? 50 : Convert.ToInt32(reader.GetValue(3)),
                    Leadership = reader.IsDBNull(4) ? 50 : Convert.ToInt32(reader.GetValue(4)),
                    ActionPoints = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5)),
                    OfficerId = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6))
                };
        }
        return null;
    }

    private List<AI_Officer> GetIdleOfficers(SqliteConnection conn, int factionId)
    {
        var list = new List<AI_Officer>();
        var cmd = conn.CreateCommand();
        // Exclude Governors (except leaders) from being 'Idle' for reassignment/missions
        cmd.CommandText = @"
            SELECT o.officer_id, o.location_id, o.name, o.current_action_points, o.strength, o.is_commander 
            FROM officers o
            LEFT JOIN cities c ON o.officer_id = c.governor_id
            WHERE o.faction_id = $fid 
            AND o.current_action_points > 0
            AND (c.governor_id IS NULL OR o.is_commander = 1)";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                list.Add(new AI_Officer
                {
                    OfficerId = Convert.ToInt32(reader.GetValue(0)),
                    LocationId = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                    Name = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                    ActionPoints = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3)),
                    Strength = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                    IsCommander = reader.IsDBNull(5) ? false : (Convert.ToInt32(reader.GetValue(5)) == 1)
                });
        }
        return list;
    }

    private int GetOfficerStrength(SqliteConnection conn, int officerId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT strength FROM officers WHERE officer_id = $oid";
        cmd.Parameters.AddWithValue("$oid", officerId);
        var res = cmd.ExecuteScalar();
        return res != null ? Convert.ToInt32(res) : 0;
    }

    private int GetCityDefenseStrength(SqliteConnection conn, int cityId)
    {
        // City Defense = Sum of troops of all officers in the city + Militia Base (Bonus)
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SUM(troops) FROM officers WHERE location_id = $cid";
        cmd.Parameters.AddWithValue("$cid", cityId);
        var res = cmd.ExecuteScalar();
        int totalTroops = (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;

        // If it's a neutral town with no officers, it has default militia
        if (totalTroops == 0)
        {
            var facCmd = conn.CreateCommand();
            facCmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
            facCmd.Parameters.AddWithValue("$cid", cityId);
            var fRes = facCmd.ExecuteScalar();
            if (fRes == null || fRes == DBNull.Value)
            {
                return 1500; // Default Militia Strength for neutral towns
            }
        }

        return totalTroops + 500; // +500 for general garrison/walls
    }

    private int FindNextHopToward(SqliteConnection conn, int startId, int targetId, int officerId)
    {
        if (startId == targetId) return 0;

        // BFS for shortest path through ANY routes
        // In the future, we might prefer owned cities.
        var queue = new Queue<int>();
        var parent = new Dictionary<int, int>();
        var visited = new HashSet<int>();

        queue.Enqueue(startId);
        visited.Add(startId);

        bool found = false;
        while (queue.Count > 0)
        {
            int curr = queue.Dequeue();
            if (curr == targetId)
            {
                found = true;
                break;
            }

            // Get neighbors
            var neighbors = new List<int>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (CASE WHEN start_city_id = $curr THEN end_city_id ELSE start_city_id END) FROM routes WHERE start_city_id = $curr OR end_city_id = $curr";
            cmd.Parameters.AddWithValue("$curr", curr);
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) neighbors.Add(r.GetInt32(0));
            }

            foreach (var n in neighbors)
            {
                if (!visited.Contains(n))
                {
                    visited.Add(n);
                    parent[n] = curr;
                    queue.Enqueue(n);
                }
            }
        }

        if (found)
        {
            int curr = targetId;
            while (parent[curr] != startId)
            {
                curr = parent[curr];
            }
            return curr; // First hop from start
        }

        return 0;
    }

    public bool EvaluateRetreat(int factionId, int friendlyTroops, int enemyTroops)
    {
        if (friendlyTroops <= 0) return true;
        if (enemyTroops <= 0) return false;

        float ratio = (float)friendlyTroops / (float)enemyTroops;

        // If outnumbered 4 to 1, or very weak, consider retreat
        if (ratio < 0.25f) return true;

        return false;
    }

    private string GetNextDomesticTask(SqliteConnection conn, int factionId)
    {
        // 1. Find HQ or primary city
        int cityId = 0;
        var hqCmd = conn.CreateCommand();
        hqCmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid AND is_hq = 1 LIMIT 1";
        hqCmd.Parameters.AddWithValue("$fid", factionId);
        var res = hqCmd.ExecuteScalar();
        if (res == null || res == DBNull.Value)
        {
            hqCmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid LIMIT 1";
            res = hqCmd.ExecuteScalar();
        }

        if (res == null || res == DBNull.Value) return "DevelopEconomy";
        cityId = Convert.ToInt32(res);

        // 2. Get Stats
        int commerce = 0, agriculture = 0, technology = 0, maxStats = 1000;
        var cityCmd = conn.CreateCommand();
        cityCmd.CommandText = "SELECT commerce, agriculture, technology, max_stats FROM cities WHERE city_id = $cid";
        cityCmd.Parameters.AddWithValue("$cid", cityId);
        using (var reader = cityCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                commerce = reader.GetInt32(0);
                agriculture = reader.GetInt32(1);
                technology = reader.GetInt32(2);
                maxStats = reader.IsDBNull(3) ? 1000 : reader.GetInt32(3);
            }
        }

        // 3. Increment Logic (250, 500, 750, 1000)
        int[] tiers = { 250, 500, 750, maxStats };
        foreach (int tier in tiers)
        {
            if (commerce < tier) return "DevelopEconomy";
            if (agriculture < tier) return "Cultivate";
            if (technology < tier) return "Research";
        }

        return "Recruit"; // Everything maxed? Build army.
    }

    private int GetFactionGold(SqliteConnection conn, int officerId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT f.gold FROM factions f JOIN officers o ON f.faction_id = o.faction_id WHERE o.officer_id = $oid";
        cmd.Parameters.AddWithValue("$oid", officerId);
        var res = cmd.ExecuteScalar();
        return (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;
    }
}
