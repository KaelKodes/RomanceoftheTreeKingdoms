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
                else task = (new Random().NextDouble() > 0.5) ? "DevelopEconomy" : "Research";
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

            // FORCE EVALUATION: Only send enough officers for 1.5x strength relative to target
            if (weeklyTask.task == "CaptureCity")
            {
                int cityDef = GetCityDefenseStrength(conn, targetId);
                int needed = (int)(cityDef * 1.5f);

                // Get current force assigned to this target
                int currentStr = 0;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT SUM(strength) FROM officers WHERE (current_assignment = 'CaptureCity' OR current_assignment = 'SupportAttack') AND assignment_target_id = $tid";
                    countCmd.Parameters.AddWithValue("$tid", targetId);
                    var currentForce = countCmd.ExecuteScalar();
                    currentStr = (currentForce != null && currentForce != DBNull.Value) ? Convert.ToInt32(currentForce) : 0;
                }

                if (currentStr >= needed)
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

        if (leader.ActionPoints > 0)
        {
            int bestStat = Math.Max(leader.Intelligence, Math.Max(leader.Politics, Math.Max(leader.Strength, leader.Leadership)));
            ActionManager.DomesticType? workType = null;
            if (bestStat == leader.Intelligence) workType = ActionManager.DomesticType.Technology;
            else if (bestStat == leader.Politics) workType = ActionManager.DomesticType.Commerce;
            else if (bestStat == leader.Strength) workType = ActionManager.DomesticType.PublicOrder;
            else if (bestStat == leader.Leadership) workType = ActionManager.DomesticType.Defense;

            if (workType.HasValue)
            {
                var am = GetNode<ActionManager>("/root/ActionManager");
                int locId = 0;
                var locCmd = conn.CreateCommand();
                locCmd.CommandText = "SELECT location_id FROM officers WHERE officer_id = $oid";
                locCmd.Parameters.AddWithValue("$oid", leader.OfficerId);
                locId = Convert.ToInt32(locCmd.ExecuteScalar());

                while (leader.ActionPoints > 0)
                {
                    am.PerformDomesticAction(leader.OfficerId, locId, workType.Value);
                    leader.ActionPoints--;
                }
            }
        }
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
            case "RecruitOfficer":
                int roninId = FindRoninInCity(conn, officer.LocationId);
                if (roninId > 0) am.PerformRecruit(officer.OfficerId, roninId);
                else am.PerformRest(officer.OfficerId);
                break;
            case "Recruit":
                // AI Army Building: Fill up to max troops with a random unit type
                var roles = new UnitRole[] { UnitRole.FRONTLINE, UnitRole.CAVALRY, UnitRole.RANGED };
                UnitRole selected = roles[new Random().Next(roles.Length)];
                am.PerformRecruitTroops(officer.OfficerId, selected);
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
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.city_id FROM routes r
            JOIN cities c ON (CASE WHEN r.start_city_id IN (SELECT city_id FROM cities WHERE faction_id = $fid) THEN r.end_city_id ELSE r.start_city_id END) = c.city_id
            WHERE (r.start_city_id IN (SELECT city_id FROM cities WHERE faction_id = $fid) OR r.end_city_id IN (SELECT city_id FROM cities WHERE faction_id = $fid))
              AND c.faction_id != $fid
            ORDER BY c.faction_id IS NULL DESC, (SELECT COUNT(*) FROM officers WHERE location_id = c.city_id) ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("$fid", factionId);
        var res = cmd.ExecuteScalar();
        return res != null ? Convert.ToInt32(res) : 0;
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
        cmd.CommandText = "SELECT officer_id, location_id, name, current_action_points, strength, is_commander FROM officers WHERE faction_id = $fid AND current_action_points > 0";
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
        // Simple: Max strength of any officer in the city + 20 wall bonus
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(strength) FROM officers WHERE location_id = $cid";
        cmd.Parameters.AddWithValue("$cid", cityId);
        var res = cmd.ExecuteScalar();
        int maxStr = (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;
        return maxStr + 20;
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
}
