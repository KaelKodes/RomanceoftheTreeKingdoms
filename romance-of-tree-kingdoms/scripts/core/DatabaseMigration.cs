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

            // Migration 2.1: Add City Management Columns
            string[] cityCols = {
                "commerce INTEGER DEFAULT 0",
                "agriculture INTEGER DEFAULT 0",
                "technology INTEGER DEFAULT 0",
                "public_order INTEGER DEFAULT 50"
            };
            foreach (var col in cityCols)
            {
                string colName = col.Split(' ')[0];
                if (!ColumnExists(conn, "cities", colName))
                {
                    GD.Print($"[Migration] Adding '{colName}' to cities...");
                    try { ExecuteSql(conn, $"ALTER TABLE cities ADD COLUMN {col}"); }
                    catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add '{colName}': {ex.Message}"); }
                }
            }

            // Migration 2.2: Add governor_id to cities
            if (!ColumnExists(conn, "cities", "governor_id"))
            {
                GD.Print("[Migration] Adding 'governor_id' to cities...");
                try { ExecuteSql(conn, "ALTER TABLE cities ADD COLUMN governor_id INTEGER DEFAULT 0;"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add 'governor_id': {ex.Message}"); }
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

            // Migration 6: ActionManager Schema Requirements
            // destination_city_id, gold, reputation, etc.
            string[] officerColumns = {
                "destination_city_id INTEGER DEFAULT NULL",
                "gold INTEGER DEFAULT 200", // Player starts with 200
                "reputation INTEGER DEFAULT 0",
                "battles_won INTEGER DEFAULT 0",
                "battles_lost INTEGER DEFAULT 0",
                "days_service INTEGER DEFAULT 0",
                "last_promotion_day INTEGER DEFAULT 0",
                "max_troops INTEGER DEFAULT 1000",
                "is_commander INTEGER DEFAULT 0",
                "current_action_points INTEGER DEFAULT 3",
                "max_action_points INTEGER DEFAULT 3"
            };

            foreach (var colDef in officerColumns)
            {
                string colName = colDef.Split(' ')[0];
                if (!ColumnExists(conn, "officers", colName))
                {
                    GD.Print($"[Migration] Adding '{colName}' to officers...");
                    try { ExecuteSql(conn, $"ALTER TABLE officers ADD COLUMN {colDef}"); }
                    catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add '{colName}': {ex.Message}"); }
                }
            }

            // wine_dine_history
            if (!TableExists(conn, "wine_dine_history"))
            {
                GD.Print("[Migration] Creating 'wine_dine_history'...");
                try
                {
                    ExecuteSql(conn, "CREATE TABLE IF NOT EXISTS wine_dine_history (player_id INTEGER, target_id INTEGER, count INTEGER, PRIMARY KEY(player_id, target_id))");
                }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to create 'wine_dine_history': {ex.Message}"); }
            }

            // Cleanup: Ensure no NULL action points
            ExecuteSql(conn, "UPDATE officers SET max_action_points = 5 WHERE is_commander = 1 OR rank = 'Sovereign'");
            ExecuteSql(conn, "UPDATE officers SET max_action_points = 3 WHERE max_action_points IS NULL");
            ExecuteSql(conn, "UPDATE officers SET current_action_points = max_action_points WHERE current_action_points IS NULL");

            GD.Print("[Migration] Schema Check Complete.");

            // Migration 7: RotTK8 Stat Alignment (Combat->Strength, Strategy->Intelligence, +Charisma)
            // Rename columns if old names exist
            if (ColumnExists(conn, "officers", "combat"))
            {
                GD.Print("[Migration] Renaming 'combat' to 'strength'...");
                try { ExecuteSql(conn, "ALTER TABLE officers RENAME COLUMN combat TO strength"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Rename combat failed: {ex.Message}"); }
            }
            if (ColumnExists(conn, "officers", "strategy"))
            {
                GD.Print("[Migration] Renaming 'strategy' to 'intelligence'...");
                try { ExecuteSql(conn, "ALTER TABLE officers RENAME COLUMN strategy TO intelligence"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Rename strategy failed: {ex.Message}"); }
            }

            // Add Charisma
            if (!ColumnExists(conn, "officers", "charisma"))
            {
                GD.Print("[Migration] Adding 'charisma' to officers...");
                try { ExecuteSql(conn, "ALTER TABLE officers ADD COLUMN charisma INTEGER DEFAULT 50"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add charisma: {ex.Message}"); }
            }
            // Migration 8: Add source_location_id and leader_id to pending_battles
            if (TableExists(conn, "pending_battles"))
            {
                if (!ColumnExists(conn, "pending_battles", "source_location_id"))
                {
                    GD.Print("[Migration] Adding 'source_location_id' to pending_battles...");
                    try
                    {
                        ExecuteSql(conn, "ALTER TABLE pending_battles ADD COLUMN source_location_id INTEGER DEFAULT 0;");
                        GD.Print("[Migration] 'source_location_id' added successfully.");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[Migration] Failed to add 'source_location_id': {ex.Message}");
                    }
                }

                if (!ColumnExists(conn, "pending_battles", "leader_id"))
                {
                    GD.Print("[Migration] Adding 'leader_id' to pending_battles...");
                    try
                    {
                        ExecuteSql(conn, "ALTER TABLE pending_battles ADD COLUMN leader_id INTEGER DEFAULT 0;");
                        GD.Print("[Migration] 'leader_id' added successfully.");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[Migration] Failed to add 'leader_id': {ex.Message}");
                    }
                }
            }

            // Migration 9: Stat Allocation System
            string[] statAllocCols = {
                "stat_points INTEGER DEFAULT 0",
                "base_strength INTEGER DEFAULT 50",
                "base_leadership INTEGER DEFAULT 50",
                "base_intelligence INTEGER DEFAULT 50",
                "base_politics INTEGER DEFAULT 50",
                "base_charisma INTEGER DEFAULT 50"
            };
            foreach (var colDef in statAllocCols)
            {
                string colName = colDef.Split(' ')[0];
                if (!ColumnExists(conn, "officers", colName))
                {
                    GD.Print($"[Migration] Adding '{colName}' to officers...");
                    try { ExecuteSql(conn, $"ALTER TABLE officers ADD COLUMN {colDef}"); }
                    catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add '{colName}': {ex.Message}"); }
                }
            }

            // Migration 10: Ensure baselines are set for existing players
            // Force sync if base_strength is 50 (previous incorrect default sync) or NULL/0
            ExecuteSql(conn, "UPDATE officers SET base_strength = strength, base_leadership = leadership, base_intelligence = intelligence, base_politics = politics, base_charisma = charisma WHERE base_strength IS NULL OR base_strength = 0 OR base_strength = 50;");

            // Migration 11: Add Portrait Source/Coords
            if (!ColumnExists(conn, "officers", "portrait_source_id"))
            {
                GD.Print("[Migration] Adding 'portrait_source_id' to officers...");
                try { ExecuteSql(conn, "ALTER TABLE officers ADD COLUMN portrait_source_id INTEGER DEFAULT 0;"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add portrait_source_id: {ex.Message}"); }
            }
            if (!ColumnExists(conn, "officers", "portrait_coords"))
            {
                GD.Print("[Migration] Adding 'portrait_coords' to officers...");
                try { ExecuteSql(conn, "ALTER TABLE officers ADD COLUMN portrait_coords VARCHAR DEFAULT '0,0';"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add portrait_coords: {ex.Message}"); }
            }
            if (!ColumnExists(conn, "officers", "formation_type"))
            {
                GD.Print("[Migration] Adding 'formation_type' to officers...");
                try { ExecuteSql(conn, "ALTER TABLE officers ADD COLUMN formation_type INTEGER DEFAULT 0;"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add formation_type: {ex.Message}"); }
            }
            if (!ColumnExists(conn, "officers", "last_mentored_day"))
            {
                GD.Print("[Migration] Adding 'last_mentored_day' to officers...");
                try { ExecuteSql(conn, "ALTER TABLE officers ADD COLUMN last_mentored_day INTEGER DEFAULT 0;"); }
                catch (Exception ex) { GD.PrintErr($"[Migration] Failed to add last_mentored_day: {ex.Message}"); }
            }
            // Migration 12: Update Routes (Mistwood Connection)
            // Remove Mistwood <-> River Port
            // Add Mistwood <-> South Fields
            GD.Print("[Migration] Updating Route Connections...");
            try
            {
                // Check if old route exists to avoid spamming or redundant deletes? SQL delete is safe.
                // Mistwood ID? River Port ID?
                // Subqueries are safest if IDs change.
                string deleteSql = @"
                    DELETE FROM routes 
                    WHERE (start_city_id = (SELECT city_id FROM cities WHERE name='Mistwood') AND end_city_id = (SELECT city_id FROM cities WHERE name='River Port')) 
                       OR (start_city_id = (SELECT city_id FROM cities WHERE name='River Port') AND end_city_id = (SELECT city_id FROM cities WHERE name='Mistwood'));";
                ExecuteSql(conn, deleteSql);

                // Check if new route already exists? UNIQUE constraint on (start, end)? 
                // Let's rely on basic check or just INSERT OR IGNORE if tables are set up right, but easier to just check.
                // For simplicity here, we'll try to insert. If it duplicates, we might want to be careful.
                // Assuming standard road for now.
                string insertSql = @"
                    INSERT INTO routes (start_city_id, end_city_id, distance, route_type, is_chokepoint)
                    SELECT c1.city_id, c2.city_id, 2.0, 'Road', 0
                    FROM cities c1, cities c2
                    WHERE c1.name = 'Mistwood' AND c2.name = 'South Fields'
                    AND NOT EXISTS (
                        SELECT 1 FROM routes r 
                        WHERE (r.start_city_id = c1.city_id AND r.end_city_id = c2.city_id)
                           OR (r.start_city_id = c2.city_id AND r.end_city_id = c1.city_id)
                    );";
                ExecuteSql(conn, insertSql);

                // Bi-directional?
                // The seed script adds BOTH ways. We should ensure the other direction is added too? 
                // Actually my logic above only inserts one way. The game treats routes bi-directionally usually or stores both?
                // Seed script adds: (c1, c2) AND (c2, c1). So I must add the reverse too.
                string insertReverseSql = @"
                    INSERT INTO routes (start_city_id, end_city_id, distance, route_type, is_chokepoint)
                    SELECT c2.city_id, c1.city_id, 2.0, 'Road', 0
                    FROM cities c1, cities c2
                    WHERE c1.name = 'Mistwood' AND c2.name = 'South Fields'
                    AND NOT EXISTS (
                        SELECT 1 FROM routes r 
                        WHERE (r.start_city_id = c2.city_id AND r.end_city_id = c1.city_id)
                    );";
                ExecuteSql(conn, insertReverseSql);

                GD.Print("[Migration] Routes updated successfully.");
            }
            catch (Exception ex)
            {
                // Soft fail, maybe cities don't exist yet if this is a fresh fresh run (unlikely as cities created in step 4/5)
                GD.PrintErr($"[Migration] Route update failed: {ex.Message}");
            }
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
