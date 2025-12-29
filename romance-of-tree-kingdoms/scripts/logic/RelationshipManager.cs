using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class RelationshipManager : Node
{
    public static RelationshipManager Instance { get; private set; }
    private string _dbPath;

    public override void _Ready()
    {
        Instance = this;
        _dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
    }

    // --- Officer to Officer Relations ---

    public int GetRelation(int officer1Id, int officer2Id)
    {
        if (officer1Id == officer2Id) return 100; // Self love?

        // Force smaller ID first to ensure consistent key (relations are bidirectional here)
        int idA = Math.Min(officer1Id, officer2Id);
        int idB = Math.Max(officer1Id, officer2Id);

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM officer_relations WHERE officer_1_id = $id1 AND officer_2_id = $id2";
            cmd.Parameters.AddWithValue("$id1", idA);
            cmd.Parameters.AddWithValue("$id2", idB);

            var res = cmd.ExecuteScalar();
            return (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0; // Default 0 (Neutral)
        }
    }

    public void ModifyRelation(int officer1Id, int officer2Id, int delta)
    {
        if (officer1Id == officer2Id) return;

        int idA = Math.Min(officer1Id, officer2Id);
        int idB = Math.Max(officer1Id, officer2Id);

        int current = GetRelation(idA, idB);
        int newVal = Math.Clamp(current + delta, -100, 100);

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            // Upsert Logic
            cmd.CommandText = @"
                INSERT INTO officer_relations (officer_1_id, officer_2_id, value) 
                VALUES ($id1, $id2, $val)
                ON CONFLICT(officer_1_id, officer_2_id) DO UPDATE SET value = $val";

            cmd.Parameters.AddWithValue("$id1", idA);
            cmd.Parameters.AddWithValue("$id2", idB);
            cmd.Parameters.AddWithValue("$val", newVal);
            cmd.ExecuteNonQuery();
        }
        GD.Print($"Relation between {idA} and {idB} is now {newVal} ({delta:+0;-0})");
    }

    // --- Officer to Faction Relations ---

    public int GetFactionRelation(int officerId, int factionId)
    {
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM officer_faction_relations WHERE officer_id = $oid AND faction_id = $fid";
            cmd.Parameters.AddWithValue("$oid", officerId);
            cmd.Parameters.AddWithValue("$fid", factionId);

            var res = cmd.ExecuteScalar();
            return (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;
        }
    }

    public void ModifyFactionRelation(int officerId, int factionId, int delta)
    {
        int current = GetFactionRelation(officerId, factionId);
        int newVal = Math.Clamp(current + delta, -100, 100);

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO officer_faction_relations (officer_id, faction_id, value) 
                VALUES ($oid, $fid, $val)
                ON CONFLICT(officer_id, faction_id) DO UPDATE SET value = $val";

            cmd.Parameters.AddWithValue("$oid", officerId);
            cmd.Parameters.AddWithValue("$fid", factionId);
            cmd.Parameters.AddWithValue("$val", newVal);
            cmd.ExecuteNonQuery();
        }
    }
}
