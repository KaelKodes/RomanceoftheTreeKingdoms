using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class WorldGenerator : Node
{
    // private string _dbPath; // Removed

    private string[] _names = new string[] {
        "Cao Cao", "Liu Bei", "Sun Quan", "Lu Bu", "Guan Yu", "Zhang Fei", "Zhao Yun", "Zhuge Liang",
        "Zhou Yu", "Sima Yi", "Yuan Shao", "Dong Zhuo", "Ma Chao", "Huang Zhong", "Sun Ce", "Taishi Ci",
        "Lu Meng", "Lu Su", "Guo Jia", "Xun Yu", "Xiahou Dun", "Xiahou Yuan", "Zhang Liao", "Xu Huang",
        "Dian Wei", "Pang Tong", "Wei Yan", "Jiang Wei", "Deng Ai", "Zhong Hui"
    };

    private string[] _factionNames = new string[] { "Crimson Empire", "Azure Alliance", "Emerald Coalition" };
    private string[] _factionColors = new string[] { "#FF4444", "#4444FF", "#44FF44" };

    private Dictionary<string, (int Source, string Coords)> _fixedPortraits = new Dictionary<string, (int, string)>()
    {
		// Shu (Source 5)
		{ "Liu Bei", (5, "0,0") }, { "Guan Yu", (5, "1,0") }, { "Zhang Fei", (5, "2,0") },
        { "Zhuge Liang", (5, "3,0") }, { "Zhao Yun", (5, "0,1") }, { "Huang Zhong", (5, "1,1") },

		// Wei (Source 6)
		{ "Cao Cao", (6, "0,0") }, { "Guo Jia", (6, "1,0") }, { "Zhang Liao", (6, "2,0") },
        { "Xiahou Yuan", (6, "3,0") }, { "Sima Yi", (6, "0,1") }, { "Xu Huang", (6, "1,1") },

		// Other1 (Source 3)
		{ "Xu Shu", (3, "0,0") }, { "Lu Bu", (3, "1,0") }, { "Diaochan", (3, "2,0") },
        { "Chen Gong", (3, "3,0") }, { "Yuan Shao", (3, "0,1") }, { "Ma Chao", (3, "1,1") },
        { "Hu Ji", (3, "0,2") },

		// Other2 (Source 4) - Unique Sizes
		{ "Fisherman", (4, "2,1") }, { "Lin Zhi", (4, "3,1") },

		// More1 (Source 2) - New Portraits
		{ "Pang Tong", (2, "1,0") }, { "Xun Yu", (2, "2,0") },
        { "Wei Yan", (2, "0,1") }, { "Deng Ai", (2, "1,1") },
        { "Jiang Wei", (2, "0,2") }, { "Taishi Ci", (2, "1,2") },

		// More2 (Source 8) - New Portraits
		{ "Xiahou Dun", (8, "0,0") }, { "Dong Zhuo", (8, "2,0") },
        { "Lu Su", (8, "3,0") },

		// Custom (Source 0)
		{ "Han De", (0, "2,2") }, { "Zhong Hui", (0, "2,0") }, { "Dian Wei", (0, "1,0") },

		// Wu (Source 7)
		{ "Zhou Yu", (7, "3,0") }, { "Sun Quan", (7, "2,0") }, { "Sun Ce", (7, "0,0") },
        { "Sun Jian", (7, "0,0") }, { "Lu Meng", (7, "0,1") }, { "Lu Zun", (7, "1,1") }
    };

    private List<(int Source, string Coords)> _genericPool = new List<(int, string)>();

    public override void _Ready()
    {
        // 1. Collect all fixed portraits to exclude them from the generic pool
        var excludedTiles = new HashSet<string>();
        foreach (var p in _fixedPortraits.Values)
        {
            excludedTiles.Add($"{p.Source}:{p.Coords}");
        }

        // Add specific manual exclusions
        excludedTiles.Add("0:0,0"); // Player

        // 2. Pre-populate generic pool based on actual atlas availability in CustomOfficers.tres
        // Format: AddGeneric(sourceId, rows, cols, excludedList, globalExclusions)

        // Custom (0): 4x4 but missing some? .tres has many tiles.
        // Looking at .tres: 0:0, 1:0, 2:0, 3:0, 0:1, 1:1, 2:1, 3:1, 0:2, 1:2, 2:2, 3:2, 0:3, 1:3
        AddGeneric(0, 4, 4, new List<string> { "2,3", "3,3" }, excludedTiles);

        // Females (1): 4 cols, 3 rows (partial)
        // 0:0-3:0, 0:1-3:1, 0:2-1:2
        AddGeneric(1, 4, 3, new List<string> { "2,2", "3,2" }, excludedTiles);

        // Wu (7): 4 cols, 2 rows (partial)
        // 0:0-3:0, 0:1-1:1
        AddGeneric(7, 4, 2, new List<string> { "2,1", "3,1" }, excludedTiles);

        // Others (just examples, let's add more if they have space)
        AddGeneric(3, 4, 3, new List<string> { "1,2", "2,2", "3,2" }, excludedTiles); // Other1
        AddGeneric(2, 3, 3, null, excludedTiles); // More1
        AddGeneric(8, 4, 1, null, excludedTiles); // More2 (1 row of 4)

        // Shuffle the pool
        var rng = new Random();
        _genericPool = _genericPool.OrderBy(x => rng.Next()).ToList();
    }

    private void AddGeneric(int source, int cols, int rows, List<string> localExclude, HashSet<string> globalExclude)
    {
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                string coords = $"{x},{y}";
                if (localExclude != null && localExclude.Contains(coords)) continue;

                string globalKey = $"{source}:{coords}";
                if (globalExclude.Contains(globalKey)) continue;

                _genericPool.Add((source, coords));
            }
        }
    }

    private (int Source, string Coords) GetPortraitFor(string name, bool isPlayer)
    {
        if (isPlayer) return (0, "0,0"); // Lock Player to Custom 0,0

        if (_fixedPortraits.ContainsKey(name)) return _fixedPortraits[name];

        // Assign from generic pool
        if (_genericPool.Count > 0)
        {
            var p = _genericPool[0];
            _genericPool.RemoveAt(0);
            return p;
        }

        return (0, "0,0"); // Fallback
    }

    public void GenerateNewWorld()
    {
        GD.Print("[WorldGenerator] Generating New World...");

        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();
            using (var transaction = conn.BeginTransaction())
            {
                try
                {
                    // 1. Wipe Old Data
                    // Clear all relations and dependent tables first
                    ExecuteSql(conn, "DELETE FROM faction_relations");
                    ExecuteSql(conn, "DELETE FROM officer_faction_relations");
                    ExecuteSql(conn, "DELETE FROM officer_relations");
                    ExecuteSql(conn, "DELETE FROM pending_battles");

                    // Break FK links in core tables
                    ExecuteSql(conn, "UPDATE officers SET faction_id = NULL");
                    ExecuteSql(conn, "UPDATE cities SET faction_id = NULL, is_hq = 0");

                    // Now safe to delete core entities
                    ExecuteSql(conn, "DELETE FROM factions");
                    ExecuteSql(conn, "DELETE FROM officers WHERE is_player = 0");

                    // 2. Create Factions
                    var factionIds = new List<long>();
                    for (int i = 0; i < 3; i++)
                    {
                        var cmd = conn.CreateCommand();
                        cmd.CommandText = "INSERT INTO factions (name, color, leader_id) VALUES ($name, $color, 0); SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("$name", _factionNames[i]);
                        cmd.Parameters.AddWithValue("$color", _factionColors[i]);
                        long fid = (long)cmd.ExecuteScalar();
                        factionIds.Add(fid);
                    }

                    // 3. Generate 25 Officers
                    var rng = new Random();
                    var officerIds = new List<long>();

                    // Shuffle names
                    var shuffledNames = _names.OrderBy(x => rng.Next()).Take(25).ToList();

                    foreach (var name in shuffledNames)
                    {
                        var (src, coords) = GetPortraitFor(name, false);

                        var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT INTO officers (name, leadership, intelligence, strength, politics, charisma, is_player, rank, reputation, troops, is_commander, max_troops, max_action_points, current_action_points, base_leadership, base_intelligence, base_strength, base_politics, base_charisma, portrait_source_id, portrait_coords) 
                            VALUES ($name, $lea, $int, $str, $pol, $cha, 0, $rnk, 0, $tr, 0, $mt, 3, 3, $lea, $int, $str, $pol, $cha, $psrc, $pcoords);
                            SELECT last_insert_rowid();";

                        cmd.Parameters.AddWithValue("$name", name);
                        cmd.Parameters.AddWithValue("$rnk", GameConstants.RANK_VOLUNTEER);
                        cmd.Parameters.AddWithValue("$tr", GameConstants.TROOPS_VOLUNTEER);
                        cmd.Parameters.AddWithValue("$mt", GameConstants.TROOPS_VOLUNTEER);
                        cmd.Parameters.AddWithValue("$lea", rng.Next(20, 75));
                        cmd.Parameters.AddWithValue("$int", rng.Next(20, 75));
                        cmd.Parameters.AddWithValue("$str", rng.Next(20, 75));
                        cmd.Parameters.AddWithValue("$pol", rng.Next(20, 75));
                        cmd.Parameters.AddWithValue("$cha", rng.Next(30, 80));
                        cmd.Parameters.AddWithValue("$psrc", src);
                        cmd.Parameters.AddWithValue("$pcoords", coords);

                        long oid = (long)cmd.ExecuteScalar();
                        officerIds.Add(oid);
                    }

                    // Update Player Portrait
                    var (pSrc, pCoords) = GetPortraitFor("Player", true); // Name ignored
                    ExecuteSql(conn, $"UPDATE officers SET portrait_source_id = {pSrc}, portrait_coords = '{pCoords}' WHERE is_player = 1");

                    // 4. Assign Leaders
                    // Pick 3 random officers to be leaders
                    var leaders = officerIds.OrderBy(x => rng.Next()).Take(3).ToList();
                    var freeOfficers = officerIds.Except(leaders).ToList();

                    for (int i = 0; i < 3; i++)
                    {
                        long lid = leaders[i];
                        long fid = factionIds[i];

                        // Buff Stats, Set Rank 9 (Sovereign), Troops 5000
                        // Leaders are strong but not "capped" at 100 on day 1. 
                        // Variety: Some are better at fighting, some at thinking.
                        int lLea = rng.Next(80, 92);
                        int lInt = rng.Next(80, 92);
                        int lStr = rng.Next(80, 92);
                        int lPol = rng.Next(75, 88);
                        int lCha = rng.Next(85, 95);

                        ExecuteSql(conn, $"UPDATE officers SET leadership = {lLea}, intelligence = {lInt}, strength = {lStr}, politics = {lPol}, charisma = {lCha}, rank = 'Sovereign', troops = {GameConstants.TROOPS_SOVEREIGN}, max_troops = {GameConstants.TROOPS_SOVEREIGN}, max_action_points = 5, current_action_points = 5, faction_id = " + fid + " WHERE officer_id = " + lid);

                        ExecuteSql(conn, "UPDATE factions SET leader_id = " + lid + " WHERE faction_id = " + fid);

                        // Assign 1-3 Members
                        int membersCount = rng.Next(2, 5);
                        for (int m = 0; m < membersCount; m++)
                        {
                            if (freeOfficers.Count == 0) break;
                            long mid = freeOfficers[0];
                            freeOfficers.RemoveAt(0);

                            // Rank 1-5, Troops 1000-3000
                            int rankIdx = rng.Next(1, 4); // Regular to Captain
                            string rankTitle = GameConstants.GetRankTitle(rankIdx);
                            int troops = GameConstants.GetMaxTroopsByLevel(rankIdx);
                            // Ensure max_troops matches
                            ExecuteSql(conn, $"UPDATE officers SET faction_id = {fid}, rank = '{rankTitle}', troops = {troops}, max_troops = {troops} WHERE officer_id = {mid}");
                        }
                    }

                    // 5. Assign HQs and Place Officers
                    var cityIds = new List<long>();
                    var cityCmd = conn.CreateCommand();
                    cityCmd.CommandText = "SELECT city_id FROM cities";
                    using (var r = cityCmd.ExecuteReader())
                    {
                        while (r.Read()) cityIds.Add(r.GetInt64(0));
                    }

                    // Load Graph for Distance Check
                    var cityGraph = LoadCityGraph(conn);
                    var hqs = PickDistantHQs(cityIds, cityGraph, 3, 3); // 3 Factions, Min Dist 3

                    if (hqs == null)
                    {
                        GD.PrintErr("Could not find valid HQs with min distance 3. Fallback to random.");
                        hqs = cityIds.OrderBy(x => rng.Next()).Take(3).ToList();
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        long fid = factionIds[i];
                        long hqId = hqs[i];
                        long lid = leaders[i];

                        // Set HQ Ownership - Faction Leader is the initial Governor
                        ExecuteSql(conn, $"UPDATE cities SET faction_id = {fid}, is_hq = 1, governor_id = {lid} WHERE city_id = {hqId}");

                        // Move all Faction Members there
                        ExecuteSql(conn, $"UPDATE officers SET location_id = {hqId} WHERE faction_id = {fid}");

                        // Set Leader as Commander (Sovereign)
                        ExecuteSql(conn, $"UPDATE officers SET is_commander = 1, rank = '{GameConstants.RANK_SOVEREIGN}', max_troops = {GameConstants.TROOPS_SOVEREIGN}, troops = {GameConstants.TROOPS_SOVEREIGN}, max_action_points = 5, current_action_points = 5 WHERE officer_id = {lid}");
                    }

                    // 6. Free Agents Logic
                    foreach (var oid in freeOfficers)
                    {
                        long loc = cityIds[rng.Next(cityIds.Count)];
                        // Free Agents get a small personal guard
                        int troops = GameConstants.TROOPS_VOLUNTEER;
                        ExecuteSql(conn, $"UPDATE officers SET location_id = {loc}, troops = {troops}, max_troops = {GameConstants.TROOPS_VOLUNTEER} WHERE officer_id = {oid}");
                    }

                    transaction.Commit();
                    GD.Print("[WorldGenerator] World Generation Complete!");
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    GD.PrintErr($"[WorldGenerator] Generation Failed: {ex.Message}");
                }
            }
        }
    }

    private Dictionary<long, List<long>> LoadCityGraph(SqliteConnection conn)
    {
        var graph = new Dictionary<long, List<long>>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT start_city_id, end_city_id FROM routes";
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                long u = r.GetInt64(0);
                long v = r.GetInt64(1);
                if (!graph.ContainsKey(u)) graph[u] = new List<long>();
                // if (!graph.ContainsKey(v)) graph[v] = new List<long>(); // Routes are duplicated in DB? Let's assume directional for now or add both

                graph[u].Add(v);
                // graph[v].Add(u); // DB seems to double entries in seed_db.py, but safe to add just in case logic changes
            }
        }
        return graph;
    }

    private List<long> PickDistantHQs(List<long> allCities, Dictionary<long, List<long>> graph, int count, int minHops)
    {
        var rng = new Random();
        // Try X times to find a valid config
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var candidates = allCities.OrderBy(x => rng.Next()).Take(count).ToList();
            if (IsValidConfiguration(candidates, graph, minHops))
            {
                return candidates;
            }
        }
        return null;
    }

    private bool IsValidConfiguration(List<long> sites, Dictionary<long, List<long>> graph, int minHops)
    {
        // Check distance between every pair
        for (int i = 0; i < sites.Count; i++)
        {
            for (int j = i + 1; j < sites.Count; j++)
            {
                int dist = GetShortestPath(sites[i], sites[j], graph);
                if (dist < minHops) return false;
            }
        }
        return true;
    }

    private int GetShortestPath(long start, long end, Dictionary<long, List<long>> graph)
    {
        if (start == end) return 0;
        var visited = new HashSet<long>();
        var queue = new Queue<Tuple<long, int>>();
        queue.Enqueue(new Tuple<long, int>(start, 0));
        visited.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            long node = current.Item1;
            int depth = current.Item2;

            if (node == end) return depth;

            if (graph.ContainsKey(node))
            {
                foreach (var neighbor in graph[node])
                {
                    if (!visited.Contains(neighbor))
                    {
                        visited.Add(neighbor);
                        queue.Enqueue(new Tuple<long, int>(neighbor, depth + 1));
                    }
                }
            }
        }
        return 999; // Unreachable
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
