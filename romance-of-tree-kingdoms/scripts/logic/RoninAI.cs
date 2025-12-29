using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class RoninAI : Node
{
    private string _dbPath;

    public override void _Ready()
    {
        _dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
    }

    public void ProcessRoninTurn(int officerId, int currentCityId)
    {
        // Simple State Machine for Ronin
        // 1. Check if can join local faction?
        // 2. Socialize?
        // 3. Wander?

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();

            // Check Local Faction
            int localFactionId = 0;
            int localLeaderId = 0;

            var cityCmd = conn.CreateCommand();
            cityCmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
            cityCmd.Parameters.AddWithValue("$cid", currentCityId);
            var res = cityCmd.ExecuteScalar();
            if (res != null && res != DBNull.Value) localFactionId = Convert.ToInt32(res);

            if (localFactionId > 0)
            {
                // Get Leader
                var leadCmd = conn.CreateCommand();
                leadCmd.CommandText = "SELECT leader_id FROM factions WHERE faction_id = $fid";
                leadCmd.Parameters.AddWithValue("$fid", localFactionId);
                localLeaderId = Convert.ToInt32(leadCmd.ExecuteScalar());

                // Check Relation
                if (RelationshipManager.Instance != null)
                {
                    int rel = RelationshipManager.Instance.GetRelation(officerId, localLeaderId);
                    if (rel >= 50) // High affinity threshold
                    {
                        // Auto Join!
                        GD.Print($"[RoninAI] Officer {officerId} admires Leader {localLeaderId} (Rel: {rel}) and JOINS Faction {localFactionId}!");
                        ExecuteSql(conn, $"UPDATE officers SET faction_id = {localFactionId}, rank = 'Officer' WHERE officer_id = {officerId}");
                        return; // Turn over
                    }
                }
            }

            // 2. Socialize (Boost relations with random local officer)
            // ... (Simplified: Just skip to wander for now to keep it lightweight)

            // 3. Wander (50% chance)
            var rng = new Random();
            if (rng.NextDouble() > 0.5)
            {
                // Get Neighbors
                var routesCmd = conn.CreateCommand();
                routesCmd.CommandText = "SELECT start_city_id, end_city_id FROM routes WHERE start_city_id = $cid OR end_city_id = $cid";
                routesCmd.Parameters.AddWithValue("$cid", currentCityId);
                var neighbors = new List<int>();
                using (var r = routesCmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int s = r.GetInt32(0);
                        int e = r.GetInt32(1);
                        neighbors.Add(s == currentCityId ? e : s);
                    }
                }

                if (neighbors.Count > 0)
                {
                    int dest = neighbors[rng.Next(neighbors.Count)];
                    // Teleport Move for AI (ActionManager's Travel is complex with pathfinding, simple hop is fine for AI background sim)
                    // Or use ActionManager if we want to simulate AP?
                    // Let's just update DB for simulation speed
                    ExecuteSql(conn, $"UPDATE officers SET location_id = {dest} WHERE officer_id = {officerId}");
                    // GD.Print($"[RoninAI] Officer {officerId} moved to {dest}.");
                }
            }
        }
    }

    private void ExecuteSql(SqliteConnection conn, string sql)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
