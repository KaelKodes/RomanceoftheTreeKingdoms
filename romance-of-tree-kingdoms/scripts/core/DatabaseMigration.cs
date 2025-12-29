using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class DatabaseMigration
{
    public static void Run(string dbPath)
    {
        GD.Print("[Migration] Checking Database Schema...");

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();

            // Migration 1: Add is_hq column
            bool hasIsHq = ColumnExists(conn, "cities", "is_hq");
            GD.Print($"[Migration] 'is_hq' exists? {hasIsHq}");
            if (!hasIsHq)
            {
                GD.Print("[Migration] Adding 'is_hq' column to cities...");
                try
                {
                    ExecuteSql(conn, "ALTER TABLE cities ADD COLUMN is_hq INTEGER DEFAULT 0;");
                    GD.Print("[Migration] 'is_hq' added successfully.");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Migration] Failed to add 'is_hq': {ex.Message}");
                }
            }

            // Migration 2: Add decay_turns column
            bool hasDecay = ColumnExists(conn, "cities", "decay_turns");
            GD.Print($"[Migration] 'decay_turns' exists? {hasDecay}");
            if (!hasDecay)
            {
                GD.Print("[Migration] Adding 'decay_turns' column to cities...");
                try
                {
                    ExecuteSql(conn, "ALTER TABLE cities ADD COLUMN decay_turns INTEGER DEFAULT 0;");
                    GD.Print("[Migration] 'decay_turns' added successfully.");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Migration] Failed to add 'decay_turns': {ex.Message}");
                }
            }

            // Migration 3: Add pending_battles table for Deferred Battle Resolution
            bool hasPendingBattles = TableExists(conn, "pending_battles");
            GD.Print($"[Migration] 'pending_battles' table exists? {hasPendingBattles}");
            if (!hasPendingBattles)
            {
                GD.Print("[Migration] Creating 'pending_battles' table...");
                try
                {
                    ExecuteSql(conn, @"
                        CREATE TABLE pending_battles (
                            location_id INTEGER PRIMARY KEY,
                            attacker_faction_id INTEGER
                        );");
                    GD.Print("[Migration] 'pending_battles' created successfully.");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Migration] Failed to create 'pending_battles': {ex.Message}");
                }
            }

            // Migration 4: Relationship System & Troops
            // 4a. Add troops column to officers
            bool hasTroops = ColumnExists(conn, "officers", "troops");
            if (!hasTroops)
            {
                GD.Print("[Migration] Adding 'troops' column to officers...");
                try { ExecuteSql(conn, "ALTER TABLE officers ADD COLUMN troops INTEGER DEFAULT 0;"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add 'troops': {ex.Message}"); }
            }

            // 4b. Create officer_relations
            if (!TableExists(conn, "officer_relations"))
            {
                GD.Print("[Migration] Creating 'officer_relations'...");
                try
                {
                    ExecuteSql(conn, @"
                        CREATE TABLE officer_relations (
                            officer_1_id INTEGER,
                            officer_2_id INTEGER,
                            value INTEGER DEFAULT 0,
                            PRIMARY KEY (officer_1_id, officer_2_id)
                        );");
                }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to create 'officer_relations': {ex.Message}"); }
            }

            // 4c. Create officer_faction_relations
            if (!TableExists(conn, "officer_faction_relations"))
            {
                GD.Print("[Migration] Creating 'officer_faction_relations'...");
                try
                {
                    ExecuteSql(conn, @"
                        CREATE TABLE officer_faction_relations (
                            officer_id INTEGER,
                            faction_id INTEGER,
                            value INTEGER DEFAULT 0,
                            PRIMARY KEY (officer_id, faction_id)
                        );");
                }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to create 'officer_faction_relations': {ex.Message}"); }
            }

            // Migration 5: Add leader_id to factions (Required for WorldGenerator)
            bool hasLeader = ColumnExists(conn, "factions", "leader_id");
            if (!hasLeader)
            {
                GD.Print("[Migration] Adding 'leader_id' column to factions...");
                try { ExecuteSql(conn, "ALTER TABLE factions ADD COLUMN leader_id INTEGER DEFAULT 0;"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add 'leader_id': {ex.Message}"); }
            }

            GD.Print("[Migration] Schema Check Complete.");
        }
    }

    private static bool ColumnExists(SqliteConnection conn, string tableName, string columnName)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                // Column 1 is name
                if (reader.GetString(1) == columnName) return true;
            }
        }
        return false;
    }

    private static void ExecuteSql(SqliteConnection conn, string sql)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() != null;
    }
}
