using Godot;
using Microsoft.Data.Sqlite;
using System.IO;

public static class DatabaseHelper
{
    private static string _dbPath;

    public static string DbPath
    {
        get
        {
            if (string.IsNullOrEmpty(_dbPath))
            {
                _dbPath = Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
            }
            return _dbPath;
        }
    }

    public static SqliteConnection GetConnection()
    {
        return new SqliteConnection($"Data Source={DbPath}");
    }

    public static void Initialize()
    {
        GD.Print($"[DatabaseHelper] Path resolved to: {DbPath}");
    }
}
