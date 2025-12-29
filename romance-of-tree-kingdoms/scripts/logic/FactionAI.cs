using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class FactionAI : Node
{
    public static FactionAI Instance { get; private set; }
    // private string _dbPath; // Removed

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

                // 1. Refresh Hierarchy if needed (Should be handled by TurnManager at week starts, but safety check here)
                EnsureGoalsExist(conn, factionId);

                // 2. Leader Phase: Assign Tasks (Costs Leader AP)
                AssignOfficerTasks(conn, factionId);

                // 3. Officer Phase: Execute Assignments
                var officers = GetOfficersWithAssignments(conn, factionId);
                foreach (var off in officers)
                {
                    ExecuteAssignment(conn, off);
                }
            }

            // Simulate "Thinking Time"
            await ToSignal(GetTree().CreateTimer(0.5f), SceneTreeTimer.SignalName.Timeout);

            var tm = GetNodeOrNull<TurnManager>("/root/TurnManager");
            tm?.AIEndTurn();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[FactionAI] Error in ProcessTurn: {ex.Message}\n{ex.StackTrace}");
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

        string goal = "Prosper"; // Default

        // High Strength/Leadership -> Conquest
        if (leader.Strength > 70 || leader.Leadership > 75) goal = "Conquest";
        // High Politics/Intelligence -> Prosper (Tech/Economy)
        else if (leader.Politics > 70 || leader.Intelligence > 70) goal = "Prosper";
        // Balanced/High Charisma -> Defense/Stability
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
                // Find weakest neighbor
                targetId = FindExpansionTarget(conn, factionId);
                task = targetId > 0 ? "CaptureCity" : "Recruit";
                break;
            case "Prosper":
                task = (new Random().NextDouble() > 0.5) ? "DevelopEconomy" : "Research";
                break;
            case "Stability":
            default:
                task = "Fortify";
                break;
        }

        var update = conn.CreateCommand();
        update.CommandText = "UPDATE factions SET weekly_task = $task, goal_target_id = $tid WHERE faction_id = $fid";
        update.Parameters.AddWithValue("$task", task);
        update.Parameters.AddWithValue("$tid", targetId);
        update.Parameters.AddWithValue("$fid", factionId);
        update.ExecuteNonQuery();

        GD.Print($"[AI Strategy] Faction {factionId} sets WEEKLY TASK: {task} (Target: {targetId})");
    }

    private void AssignOfficerTasks(SqliteConnection conn, int factionId)
    {
        // Leaders spend AP to assign. 1 AP = Assign up to 3 officers? Or 1 for 1?
        // Let's go simple: Leader spends ALL AP to assign as many as possible.
        var leader = GetFactionLeader(conn, factionId);
        if (leader == null || leader.ActionPoints <= 0) return;

        var weeklyTask = GetWeeklyTask(conn, factionId);
        var idle = GetIdleOfficers(conn, factionId);

        foreach (var off in idle)
        {
            if (leader.ActionPoints <= 0) break;

            // Simple assignment: Everyone does the weekly task
            // Future: Specialization (Smarter officers do specific sub-tasks)
            ExecuteSql(conn, $"UPDATE officers SET current_assignment = '{weeklyTask.task}', assignment_target_id = {weeklyTask.targetId} WHERE officer_id = {off.OfficerId}");

            // leader.ActionPoints--; // Don't forget to update DB
            ExecuteSql(conn, $"UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = {leader.OfficerId}");
            leader.ActionPoints--;
        }
    }

    private void ExecuteAssignment(SqliteConnection conn, AI_Officer officer)
    {
        if (officer.ActionPoints <= 0) return;

        var am = GetNode<ActionManager>("/root/ActionManager");

        switch (officer.Assignment)
        {
            case "CaptureCity":
                // If not at target but neighbor, Declare!
                // If not adjacent, travel?
                // For now, AI is simple: only attacks neighbors
                if (IsAdjacent(conn, officer.LocationId, officer.AssignmentTarget))
                {
                    am.DeclareAttack(officer.OfficerId, officer.AssignmentTarget);
                }
                else
                {
                    // Move towards it or just Patrol
                    am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.PublicOrder);
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
            case "Recruit":
                am.PerformRest(officer.OfficerId); // Heal/Recruit troops
                break;
            default:
                // Idle? Patrol.
                am.PerformDomesticAction(officer.OfficerId, officer.LocationId, ActionManager.DomesticType.PublicOrder);
                break;
        }

        // Clear assignment after execution if it was a one-shot or keep for daily?
        // User said "Weekly goals would be known to their officers and they would be a part of achieving them."
        // We keep it until start of next day logic? No, ActionManager consumes AP.
        // Once AP is 0, they stop.
    }

    // --- Helpers ---

    private int FindExpansionTarget(SqliteConnection conn, int factionId)
    {
        // Find adjacent neutral or weak enemy city
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.city_id 
            FROM routes r
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
            if (r.Read()) return (r.IsDBNull(0) ? "DevelopEconomy" : r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt32(1));
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
            {
                list.Add(new AI_Officer
                {
                    OfficerId = r.GetInt32(0),
                    Name = r.GetString(1),
                    LocationId = r.GetInt32(2),
                    ActionPoints = r.GetInt32(3),
                    Assignment = r.GetString(4),
                    AssignmentTarget = r.IsDBNull(5) ? 0 : r.GetInt32(5)
                });
            }
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
    private class AI_Officer { public int OfficerId; public int LocationId; public string Name; public int ActionPoints; public string Assignment; public int AssignmentTarget; }

    private AI_Leader GetFactionLeader(SqliteConnection conn, int factionId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name, intelligence, strength, politics, leadership, current_action_points, officer_id
            FROM officers 
            WHERE faction_id = $fid 
              AND is_commander = 1
            LIMIT 1";
        cmd.Parameters.AddWithValue("$fid", factionId);

        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                return new AI_Leader
                {
                    Name = reader.GetString(0),
                    Intelligence = reader.GetInt32(1),
                    Strength = reader.GetInt32(2),
                    Politics = reader.GetInt32(3),
                    Leadership = reader.GetInt32(4),
                    ActionPoints = reader.GetInt32(5),
                    OfficerId = reader.GetInt32(6)
                };
            }
        }
        return null;
    }

    private List<AI_Officer> GetIdleOfficers(SqliteConnection conn, int factionId)
    {
        var list = new List<AI_Officer>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT officer_id, location_id, name, current_action_points 
            FROM officers 
            WHERE faction_id = $fid 
              AND is_commander = 0
              AND current_action_points > 0
        ";
        cmd.Parameters.AddWithValue("$fid", factionId);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                list.Add(new AI_Officer
                {
                    OfficerId = reader.GetInt32(0),
                    LocationId = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    ActionPoints = reader.GetInt32(3)
                });
            }
        }
        return list;
    }

}
