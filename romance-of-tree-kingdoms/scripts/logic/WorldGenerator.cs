using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class WorldGenerator : Node
{
    private string _dbPath;

    private string[] _names = new string[] {
        "Cao Cao", "Liu Bei", "Sun Quan", "Lu Bu", "Guan Yu", "Zhang Fei", "Zhao Yun", "Zhuge Liang",
        "Zhou Yu", "Sima Yi", "Yuan Shao", "Dong Zhuo", "Ma Chao", "Huang Zhong", "Sun Ce", "Taishi Ci",
        "Lu Meng", "Lu Su", "Guo Jia", "Xun Yu", "Xiahou Dun", "Xiahou Yuan", "Zhang Liao", "Xu Huang",
        "Dian Wei", "Pang Tong", "Wei Yan", "Jiang Wei", "Deng Ai", "Zhong Hui"
    };

    private string[] _factionNames = new string[] { "Crimson Empire", "Azure Alliance", "Emerald Coalition" };
    private string[] _factionColors = new string[] { "#FF4444", "#4444FF", "#44FF44" };

    public override void _Ready()
    {
        _dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
    }

    public void GenerateNewWorld()
    {
        GD.Print("[WorldGenerator] Generating New World...");

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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
                    // Only need 25 random names.
                    var rng = new Random();
                    var officerIds = new List<long>();

                    // Shuffle names
                    var shuffledNames = _names.OrderBy(x => rng.Next()).Take(25).ToList();

                    foreach (var name in shuffledNames)
                    {
                        var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT INTO officers (name, leadership, strategy, combat, politics, is_player, rank, reputation) 
                            VALUES ($name, $lea, $str, $com, $pol, 0, 'Free', 0);
                            SELECT last_insert_rowid();";

                        cmd.Parameters.AddWithValue("$name", name);
                        cmd.Parameters.AddWithValue("$lea", rng.Next(30, 90));
                        cmd.Parameters.AddWithValue("$str", rng.Next(30, 90));
                        cmd.Parameters.AddWithValue("$com", rng.Next(30, 90));
                        cmd.Parameters.AddWithValue("$pol", rng.Next(30, 90));

                        long oid = (long)cmd.ExecuteScalar();
                        officerIds.Add(oid);
                    }

                    // 4. Assign Leaders
                    // Pick 3 random officers to be leaders
                    var leaders = officerIds.OrderBy(x => rng.Next()).Take(3).ToList();
                    var freeOfficers = officerIds.Except(leaders).ToList();

                    for (int i = 0; i < 3; i++)
                    {
                        long lid = leaders[i];
                        long fid = factionIds[i];

                        // Buff Stats
                        ExecuteSql(conn, "UPDATE officers SET leadership = 90, strategy = 85, combat = 85, rank = 'Ruler', faction_id = " + fid + " WHERE officer_id = " + lid);
                        ExecuteSql(conn, "UPDATE factions SET leader_id = " + lid + " WHERE faction_id = " + fid);

                        // Assign 1-3 Members
                        int membersCount = rng.Next(1, 4);
                        for (int m = 0; m < membersCount; m++)
                        {
                            if (freeOfficers.Count == 0) break;
                            long mid = freeOfficers[0];
                            freeOfficers.RemoveAt(0);
                            ExecuteSql(conn, "UPDATE officers SET faction_id = " + fid + ", rank = 'Officer' WHERE officer_id = " + mid);
                        }
                    }

                    // 5. Assign HQs and Place Officers
                    // Get all cities
                    var cityIds = new List<long>();
                    var cityCmd = conn.CreateCommand();
                    cityCmd.CommandText = "SELECT city_id FROM cities";
                    using (var r = cityCmd.ExecuteReader())
                    {
                        while (r.Read()) cityIds.Add(r.GetInt64(0));
                    }

                    var hqs = cityIds.OrderBy(x => rng.Next()).Take(3).ToList();
                    for (int i = 0; i < 3; i++)
                    {
                        long fid = factionIds[i];
                        long hqId = hqs[i];

                        // Set HQ Ownership
                        ExecuteSql(conn, $"UPDATE cities SET faction_id = {fid}, is_hq = 1 WHERE city_id = {hqId}");

                        // Move all Faction Members there
                        ExecuteSql(conn, $"UPDATE officers SET location_id = {hqId} WHERE faction_id = {fid}");
                    }

                    // 6. Free Agents Logic?
                    // "All remaining officers are free/scattered". 
                    // Place them in random cities?
                    foreach (var oid in freeOfficers)
                    {
                        long loc = cityIds[rng.Next(cityIds.Count)];
                        ExecuteSql(conn, $"UPDATE officers SET location_id = {loc} WHERE officer_id = {oid}");
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

    private void ExecuteSql(SqliteConnection conn, string sql)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
