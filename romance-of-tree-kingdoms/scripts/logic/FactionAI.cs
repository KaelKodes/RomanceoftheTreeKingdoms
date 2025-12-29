using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class FactionAI : Node
{
    public static FactionAI Instance { get; private set; }
    private string _dbPath;

    public override void _Ready()
    {
        Instance = this;
        _dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
    }

    public async void ProcessTurn(int factionId)
    {
        GD.Print($"Faction {factionId} is thinking...");

        // 0. Check for Pending Battles (Strict Limit)
        bool hasAttacked = false;
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending_battles WHERE attacker_faction_id = $fid";
            cmd.Parameters.AddWithValue("$fid", factionId);
            hasAttacked = (long)cmd.ExecuteScalar() > 0;
        }
        if (hasAttacked) GD.Print($"[FactionAI] Faction {factionId} already has conflict pending. Skipping attacks.");

        // 1. Get Leader Personality
        var leader = GetFactionLeader(factionId);
        float aggression = 0.5f; // Default balanced
        float caution = 0.5f;

        if (leader != null)
        {
            // Combat -> Aggression (Higher combat = more likely to attack)
            aggression = Math.Clamp(leader.Combat / 100.0f, 0.1f, 0.9f);
            // Strategy -> Caution (Higher strategy = less likely to make risky moves)
            caution = Math.Clamp(leader.Strategy / 100.0f, 0.1f, 0.9f);

            GD.Print($"Leader {leader.Name} (Comb:{leader.Combat}, Strat:{leader.Strategy}) - Aggression: {aggression:F2}, Caution: {caution:F2}");
        }

        // 2. Get Idle Officers
        var officers = GetIdleOfficers(factionId);
        GD.Print($"Faction {factionId} has {officers.Count} idle officers ready to act.");

        // 3. For each officer, decide action based on Personality

        // Connectivity Check Helper
        var graph = new WorldGraph();
        AddChild(graph); // Add to tree so _Ready runs and sets DB path

        foreach (var off in officers)
        {
            // Check Supply Line
            // If the officer is cut off from HQ, they cannot launch offensive attacks (Conquest).
            // They might still be able to defend or move back.
            bool isSupplied = graph.IsConnectedToHQ(factionId, off.LocationId);

            if (!isSupplied)
            {
                // GD.Print($"Officer {off.Name} is CUT OFF from HQ! Cannot attack.");
                // Logic: Try to move towards HQ? For now, just skip attacking.
                continue;
            }

            // Base chance to act this turn varies by aggression
            float rollCheck = GD.Randf();
            if (rollCheck > aggression)
            {
                // Passive this turn
                // GD.Print($"Officer {off.Name} is passive (Roll {rollCheck:F2} > Agg {aggression:F2})");
                continue;
            }

            // Check neighbors using Caution
            var neighbors = GetNeighborCities(off.LocationId);
            if (neighbors.Count == 0)
            {
                // GD.Print($"Officer {off.Name} has no neighbors?"); // Debug
                continue;
            }

            // Sort neighbors? High Strategy leaders might target "Weak" cities. 
            // For now, random shuffle to avoid bias towards DB order
            neighbors = neighbors.OrderBy(x => GD.Randi()).ToList();

            foreach (var n in neighbors)
            {
                int targetFactionId = GetCityFaction(n.CityId);

                // GD.Print($"Checking neighbor {n.Name} (Faction {targetFactionId})...");

                // Determine Relation
                bool isEnemy = false;
                int relation = 0;

                if (targetFactionId != factionId)
                {
                    if (targetFactionId <= 0) // Neutral
                    {
                        // Free Real Estate! High priority.
                        isEnemy = true;
                        // Boost aggression vs Neutrals
                        // aggression += 0.3f; // Don't mutate base aggression in loop
                    }
                    else
                    {
                        // Check Diplomacy
                        var dip = GetNodeOrNull<DiplomacyManager>("/root/DiplomacyManager");
                        if (dip != null)
                        {
                            relation = dip.GetRelation(factionId, targetFactionId);
                            // Lower hostility threshold to encourage conflict
                            if (relation < 50)
                            {
                                isEnemy = true;
                            }
                        }
                        else
                        {
                            // No Diplomacy Manager? Default to War for testing
                            isEnemy = true;
                        }
                    }
                }

                if (isEnemy)
                {
                    // Limit Check
                    if (hasAttacked) continue;

                    // Aggressive Logic Adjustment
                    float attackChance = aggression;
                    if (targetFactionId <= 0) attackChance += 0.3f; // Bonus vs Neutral

                    // Caution only slightly reduces chance now
                    if (relation > -50 && targetFactionId > 0)
                    {
                        attackChance -= (caution * 0.2f);
                    }

                    // Base minimum chance to ensure some chaos
                    attackChance = Math.Max(attackChance, 0.2f);

                    float attackRoll = GD.Randf();
                    // GD.Print($"Considering attack on {n.Name}. Chance: {attackChance:F2} vs Roll: {attackRoll:F2}");

                    if (attackRoll < attackChance)
                    {
                        GD.Print($"Faction {factionId} DECIDED TO ATTACK {n.Name}!");

                        // Declare Attack using ActionManager
                        var am = GetNode<ActionManager>("/root/ActionManager");
                        if (am == null) { GD.PrintErr("ActionManager Missing!"); break; }

                        am.PerformTravel(off.OfficerId, n.CityId);

                        // Try to declare attack (Validated by AM)
                        if (am.DeclareAttack(off.OfficerId, n.CityId))
                        {
                            hasAttacked = true;
                            break;
                        }
                    }
                }
            }
        }

        graph.QueueFree(); // Cleanup helper

        // Simulate "Thinking Time"
        await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);

        // End Turn using Public Method
        var tm = GetNodeOrNull<TurnManager>("/root/TurnManager");
        tm?.AIEndTurn();
    }

    private class AI_Leader { public string Name; public int Strategy; public int Combat; }

    private AI_Leader GetFactionLeader(int factionId)
    {
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            // Find Commander or highest stats
            cmd.CommandText = @"
				SELECT name, strategy, combat 
				FROM officers 
				WHERE faction_id = $fid 
				ORDER BY 
					CASE WHEN rank = 'Commander' THEN 1 ELSE 0 END DESC, 
					(strategy + combat) DESC 
				LIMIT 1";
            cmd.Parameters.AddWithValue("$fid", factionId);

            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    return new AI_Leader
                    {
                        Name = reader.GetString(0),
                        Strategy = reader.GetInt32(1),
                        Combat = reader.GetInt32(2)
                    };
                }
            }
        }
        return null;
    }

    private class AI_Officer { public int OfficerId; public int LocationId; public string Name; }
    private class AI_City { public int CityId; public string Name; }

    private List<AI_Officer> GetIdleOfficers(int factionId)
    {
        var list = new List<AI_Officer>();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            // Idle = No destination, has AP
            cmd.CommandText = @"
				SELECT officer_id, location_id, name 
				FROM officers 
				WHERE faction_id = $fid 
				  AND destination_city_id IS NULL 
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
                        Name = reader.GetString(2)
                    });
                }
            }
        }
        return list;
    }

    private List<AI_City> GetNeighborCities(int cityId)
    {
        var list = new List<AI_City>();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
				SELECT c.city_id, c.name 
				FROM routes r
				JOIN cities c ON (case when r.start_city_id = $cid then r.end_city_id else r.start_city_id end) = c.city_id
				WHERE r.start_city_id = $cid OR r.end_city_id = $cid
			";
            cmd.Parameters.AddWithValue("$cid", cityId);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    list.Add(new AI_City
                    {
                        CityId = reader.GetInt32(0),
                        Name = reader.GetString(1)
                    });
                }
            }
        }
        return list;
    }

    private int GetCityFaction(int cityId)
    {
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
            cmd.Parameters.AddWithValue("$cid", cityId);
            var res = cmd.ExecuteScalar();
            if (res == null || res == DBNull.Value) return 0;
            return Convert.ToInt32(res);
        }
    }
}
