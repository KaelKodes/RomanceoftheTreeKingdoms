using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class DatabaseClient : Node
{
    private string _dbPath;

    public override void _Ready()
    {
        // DB is two levels up from the project folder: ../../tree_kingdoms.db
        // In Godot editor, "res://" is the project root.
        // We need the absolute path on the file system.
        string projectPath = ProjectSettings.GlobalizePath("res://");
        // Navigate up: Project -> Root -> DB
        // Actually, let's use a relative path from the executable/project root.

        // Assumption: running from editor or build in folder.
        // Project: D:/Kael Kodes/Tree Kingdoms/romance-of-tree-kingdoms/
        // DB:      D:/Kael Kodes/Tree Kingdoms/tree_kingdoms.db

        _dbPath = System.IO.Path.Combine(projectPath, "../tree_kingdoms.db");
        GD.Print($"[DB] Path: {_dbPath}");

        // Run Migrations Synchronously
        DatabaseMigration.Run(_dbPath);

        TestConnection();
    }

    public void TestConnection()
    {
        try
        {
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                connection.Open();
                GD.Print("[DB] Connection Successful!");

                var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM factions";
                var count = (long)command.ExecuteScalar();

                GD.Print($"[DB] Faction Count: {count}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DB] Connection Failed: {ex.Message}");
        }
    }
}
