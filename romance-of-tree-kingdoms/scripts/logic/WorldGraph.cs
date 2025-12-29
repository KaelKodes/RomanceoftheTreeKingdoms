using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class WorldGraph : Node
{
    private string _dbPath;

    public override void _Ready()
    {
        _dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
    }

    // Checks if a specific city is connected to the faction's HQ
    public bool IsConnectedToHQ(int factionId, int targetCityId)
    {
        // 1. Get all cities owned by this faction
        var ownedCities = GetOwnedCities(factionId);

        // If target isn't even owned (or we are checking if we CAN attack it from a connected city),
        // usage depends on context.
        // For "Can I attack target from source?": Source must be connected to HQ.
        // For "Is this city supplied?": City must be connected to HQ.

        // Let's implement: "Can I reach Target from HQ using only friendly nodes?"
        // Note: The Target itself doesn't need to be friendly if we are attacking it. 
        // But the path TO the target's neighbor must be friendly.

        // Actually, the rule is: "In order to capture a new town, it must connect to their HQ"
        // This implies the ATTACKING ORIGIN must be supplied. 
        // So we need to check if the attacking officer's current location is connected to HQ.

        // Get HQ
        int hqId = GetFactionHQ(factionId, ownedCities);
        if (hqId == -1) return true; // No HQ defined, allow all (graceful failure) or allow none? Allow all for now.

        if (hqId == targetCityId) return true; // Is HQ

        // adj list
        var edges = GetAdjacencyList();

        // BFS
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(hqId);
        visited.Add(hqId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == targetCityId) return true;

            if (edges.ContainsKey(current))
            {
                foreach (var neighbor in edges[current])
                {
                    if (!visited.Contains(neighbor))
                    {
                        // Traversable if owned by faction OR is the target (we can move into the target)
                        // But wait, supply lines usually mean "Friendly Territory".
                        // Logic: A city is "Supplied" if connected to HQ via friendly cities.
                        if (ownedCities.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }
        }

        return false;
    }

    private List<int> GetOwnedCities(int factionId)
    {
        var list = new List<int>();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid";
            cmd.Parameters.AddWithValue("$fid", factionId);
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) list.Add(r.GetInt32(0));
            }
        }
        return list;
    }

    private int GetFactionHQ(int factionId, List<int> ownedCities)
    {
        // Try to find marked HQ
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid AND is_hq = 1 LIMIT 1";
            cmd.Parameters.AddWithValue("$fid", factionId);

            try
            {
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value) return Convert.ToInt32(res);
            }
            catch (SqliteException ex)
            {
                GD.PrintErr($"[WorldGraph] DB Error checking HQ (Migration pending?): {ex.Message}");
                return -1;
            }
        }

        // Fallback: If no HQ, pick the first owned city? 
        // Or technically, if they have no HQ, they might be in trouble.
        // For stability, let's say the first city in their list is de-facto HQ.
        if (ownedCities.Count > 0) return ownedCities[0];

        return -1;
    }

    private Dictionary<int, List<int>> GetAdjacencyList()
    {
        var dict = new Dictionary<int, List<int>>();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT start_city_id, end_city_id FROM routes";
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    int a = r.GetInt32(0);
                    int b = r.GetInt32(1);

                    if (!dict.ContainsKey(a)) dict[a] = new List<int>();
                    if (!dict.ContainsKey(b)) dict[b] = new List<int>();

                    dict[a].Add(b);
                    dict[b].Add(a);
                }
            }
        }
        return dict;
    }
}
