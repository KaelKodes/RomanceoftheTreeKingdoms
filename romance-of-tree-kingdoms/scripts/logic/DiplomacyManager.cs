using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class DiplomacyManager : Node
{
	public static DiplomacyManager Instance { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		EnsureTableExists();
		InitializeRelationsIfNeeded();
	}

	private void EnsureTableExists()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = @"
				CREATE TABLE IF NOT EXISTS faction_relations (
					relation_id INTEGER PRIMARY KEY AUTOINCREMENT,
					source_faction_id INTEGER NOT NULL,
					target_faction_id INTEGER NOT NULL,
					value INTEGER DEFAULT 0,
					FOREIGN KEY(source_faction_id) REFERENCES factions(faction_id),
					FOREIGN KEY(target_faction_id) REFERENCES factions(faction_id)
				);
			";
			cmd.ExecuteNonQuery();
		}
	}

	public void InitializeRelationsIfNeeded()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Check if we have any relations
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT COUNT(*) FROM faction_relations";
			long count = (long)checkCmd.ExecuteScalar();

			if (count == 0)
			{
				GD.Print("Initializing Faction Relations...");
				// Get all faction IDs
				var factions = new List<int>();
				var facCmd = conn.CreateCommand();
				facCmd.CommandText = "SELECT faction_id FROM factions";
				using (var reader = facCmd.ExecuteReader())
				{
					while (reader.Read())
					{
						factions.Add(reader.GetInt32(0));
					}
				}

				using (var transaction = conn.BeginTransaction())
				{
					var rng = new Random();
					foreach (int source in factions)
					{
						foreach (int target in factions)
						{
							if (source == target) continue;

							// Generate value between -5 and -20 (Asymmetric)
							int val = rng.Next(-20, -4); // Upper bound is exclusive in C#, so -4 means max -5

							var insertCmd = conn.CreateCommand();
							insertCmd.CommandText = @"
								INSERT INTO faction_relations (source_faction_id, target_faction_id, value)
								VALUES ($s, $t, $v)";
							insertCmd.Parameters.AddWithValue("$s", source);
							insertCmd.Parameters.AddWithValue("$t", target);
							insertCmd.Parameters.AddWithValue("$v", val);
							insertCmd.ExecuteNonQuery();
						}
					}
					transaction.Commit();
				}
				GD.Print("Faction Relations Initialized with Mild Hostility (-5 to -20).");
			}
		}
	}

	public int GetRelation(int sourceId, int targetId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT value FROM faction_relations WHERE source_faction_id = $s AND target_faction_id = $t";
			cmd.Parameters.AddWithValue("$s", sourceId);
			cmd.Parameters.AddWithValue("$t", targetId);
			var result = cmd.ExecuteScalar();
			return result != null ? Convert.ToInt32(result) : 0;
		}
	}

	public void ModifyRelation(int sourceId, int targetId, int delta)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = @"
				UPDATE faction_relations 
				SET value = MAX(-100, MIN(100, value + $d)) 
				WHERE source_faction_id = $s AND target_faction_id = $t";
			cmd.Parameters.AddWithValue("$s", sourceId);
			cmd.Parameters.AddWithValue("$t", targetId);
			cmd.Parameters.AddWithValue("$d", delta);
			cmd.ExecuteNonQuery();
		}
	}
}
