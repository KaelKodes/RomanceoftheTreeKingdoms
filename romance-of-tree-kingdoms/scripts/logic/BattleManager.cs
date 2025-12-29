using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class BattleManager : Node
{
	public static BattleManager Instance { get; private set; }
	private string _dbPath;

	// The Context of the pending battle
	public BattleContext CurrentContext { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		_dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");

		// DEBUG: Dump Schema
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table'";
			using (var r = cmd.ExecuteReader())
			{
				while (r.Read())
				{
					GD.Print($"TABLE {r.GetString(0)}: {r.GetString(1)}");
				}
			}
		}
	}

	// Step 1: Generate Data for the Setup Screen
	public void CreateContext(int cityId)
	{
		GD.Print($"Generating Battle Context for City {cityId}...");

		CurrentContext = new BattleContext();
		CurrentContext.LocationId = cityId;

		FetchCityInfo(cityId);
		FetchOfficers(cityId);
		DetermineSides();
		GenerateObjective();

		GD.Print("Battle Context Created!");
		GD.Print($"Attackers: {CurrentContext.AttackerFactionId} ({CurrentContext.AttackerOfficers.Count} officers)");
		GD.Print($"Defenders: {CurrentContext.DefenderFactionId} ({CurrentContext.DefenderOfficers.Count} officers)");
		GD.Print($"Objective: {CurrentContext.Objective.Description}");
	}

	private void FetchCityInfo(int cityId)
	{
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name, faction_id FROM cities WHERE city_id = $cid";
			cmd.Parameters.AddWithValue("$cid", cityId);
			using (var reader = cmd.ExecuteReader())
			{
				if (reader.Read())
				{
					CurrentContext.CityName = reader.GetString(0);
					if (!reader.IsDBNull(1)) CurrentContext.OwnerFactionId = reader.GetInt32(1);
					else CurrentContext.OwnerFactionId = 0; // Neutral
				}
			}
		}
	}

	private void FetchOfficers(int cityId)
	{
		CurrentContext.AllOfficers = new List<BattleOfficer>();

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			// Get Name, ID, Faction, Stats
			cmd.CommandText = @"
				SELECT officer_id, name, faction_id, leadership, strategy, combat, is_player, rank, troops
				FROM officers 
				WHERE location_id = $cid";
			cmd.Parameters.AddWithValue("$cid", cityId);

			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					var bo = new BattleOfficer
					{
						OfficerId = reader.GetInt32(0),
						Name = reader.GetString(1),
						FactionId = reader.IsDBNull(2) ? -1 : reader.GetInt32(2), // -1 for Independent/Player
						Leadership = reader.GetInt32(3),
						Strategy = reader.GetInt32(4),
						Combat = reader.GetInt32(5),
						IsPlayer = reader.GetBoolean(6),
						Rank = reader.GetString(7),
						Troops = reader.IsDBNull(8) ? 0 : reader.GetInt32(8)
					};
					bo.MaxTroops = GetMaxTroops(bo.Rank);
					CurrentContext.AllOfficers.Add(bo);
				}
			}
		}


		// Generate Militia if Neutral and Empty
		if (CurrentContext.OwnerFactionId == 0)
		{
			// Check if any actual faction is squatting here
			// If not, defenders are Militia
			bool hasSquatters = CurrentContext.AllOfficers.Any(o => o.FactionId > 0 && o.FactionId != CurrentContext.AttackerFactionId);

			if (!hasSquatters)
			{
				GD.Print("Generating Militia for Neutral City...");
				var rng = new Random();
				int militiaCount = rng.Next(3, 6);
				for (int i = 0; i < militiaCount; i++)
				{
					var militia = new BattleOfficer
					{
						OfficerId = -100 - i, // Temp ID
						Name = "Militia Guard",
						FactionId = 0, // Neutral
						Leadership = 30,
						Strategy = 10,
						Combat = 40,
						IsPlayer = false,
						Rank = "Minion",
						Troops = 500,
						MaxTroops = 500
					};
					CurrentContext.AllOfficers.Add(militia);
				}
			}
		}
	}

	private int GetMaxTroops(string rank)
	{
		switch (rank)
		{
			case "Ruler": return 5000;
			case "Officer": return 3000;
			case "Free": return 0; // Or small retinue?
			case "Minion": return 500;
			default: return 1000;
		}
	}

	private void DetermineSides()
	{
		CurrentContext.AttackerOfficers = new List<BattleOfficer>();
		CurrentContext.DefenderOfficers = new List<BattleOfficer>();

		// Logic:
		// If City has Owner > 0, they are defenders. Everyone else is attacker.
		// If Neutral, First faction found is defender? Or "King of Hill"?
		// Let's assume Neutral = First officer's faction claims defense (squatters).

		int defId = CurrentContext.OwnerFactionId;
		// If city is neutral, the first present faction "claims" defense for this context
		if (defId == 0 && CurrentContext.AllOfficers.Count > 0)
		{
			// Try to find a real faction first
			var claimer = CurrentContext.AllOfficers.FirstOrDefault(o => o.FactionId > 0);
			if (claimer != null) defId = claimer.FactionId;
			else defId = CurrentContext.AllOfficers[0].FactionId;
		}

		CurrentContext.DefenderFactionId = defId;

		foreach (var off in CurrentContext.AllOfficers)
		{
			if (off.FactionId == defId)
			{
				CurrentContext.DefenderOfficers.Add(off);
			}
			else
			{
				CurrentContext.AttackerOfficers.Add(off);
			}
		}

		// Determine Primary Attacker Faction
		// Prioritize a Real Faction (>0) over Player/Independent (-1)
		var primaryAttacker = CurrentContext.AttackerOfficers.FirstOrDefault(o => o.FactionId > 0);
		if (primaryAttacker != null)
		{
			CurrentContext.AttackerFactionId = primaryAttacker.FactionId;
		}
		else if (CurrentContext.AttackerOfficers.Count > 0)
		{
			// Fallback to Player/Independent if no real factions exist
			CurrentContext.AttackerFactionId = CurrentContext.AttackerOfficers[0].FactionId;
		}
	}

	private void GenerateObjective()
	{
		// Simple Objective Generator V1
		// In V2, we check terrain, supply lines, etc.

		var obj = new BattleObjective();
		obj.Type = ObjectiveType.Elimination;

		// If Player is Attacker
		if (CurrentContext.AttackerOfficers.Any(x => x.IsPlayer))
		{
			obj.Description = "Capture the City! Defeat all enemy officers.";
			if (CurrentContext.DefenderOfficers.Count > 0)
			{
				var commander = CurrentContext.DefenderOfficers.OrderByDescending(x => x.Leadership).First();
				obj.Description = $"Defeat the Enemy Commander {commander.Name} to seize control.";
				obj.TargetId = commander.OfficerId;
			}
		}
		// If Player is Defender
		else if (CurrentContext.DefenderOfficers.Any(x => x.IsPlayer))
		{
			obj.Description = "Hold the City! Repel all invaders.";
			obj.Type = ObjectiveType.Defense;
			// Maybe timer based? "Survive 10 turns"
		}
		else
		{
			// Player is observing? Or Mercenary?
			obj.Description = "Choose a side and ensure their victory.";
		}

		CurrentContext.Objective = obj;
	}
	public void ResolveBattle(bool attackersWon)
	{
		GD.Print($"Resolving Battle... Winner: {(attackersWon ? "Attackers" : "Defenders")}");

		int winnerFaction = attackersWon ? CurrentContext.AttackerFactionId : CurrentContext.DefenderFactionId;
		int loserFaction = attackersWon ? CurrentContext.DefenderFactionId : CurrentContext.AttackerFactionId;

		// Map Changes
		if (attackersWon)
		{
			// Flip City Ownership
			using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
			{
				conn.Open();
				var cmd = conn.CreateCommand();
				cmd.CommandText = "UPDATE cities SET faction_id = $fid WHERE city_id = $cid";

				if (winnerFaction > 0)
					cmd.Parameters.AddWithValue("$fid", winnerFaction);
				else
					cmd.Parameters.AddWithValue("$fid", DBNull.Value); // Player/Independent -> Neutral for now

				cmd.Parameters.AddWithValue("$cid", CurrentContext.LocationId);
				cmd.ExecuteNonQuery();
			}
			GD.Print($"City {CurrentContext.CityName} is now owned by Faction {winnerFaction}!");
		}

		// Clean up
		CurrentContext = null;

		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		turnMgr?.ResumeConflictResolution();
	}

	// AI Autoresolve
	public void SimulateBattle(int attackerOfficerId, int cityId)
	{
		GD.Print($"Simulating AI Battle @ City {cityId}...");

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();

			// 1. Get Participants
			// Attacker
			var attCmd = conn.CreateCommand();
			attCmd.CommandText = "SELECT faction_id, combat, name FROM officers WHERE officer_id = $oid";
			attCmd.Parameters.AddWithValue("$oid", attackerOfficerId);
			int attFaction = 0;
			int attCombat = 0;
			string attName = "Unknown";
			using (var r = attCmd.ExecuteReader())
			{
				if (r.Read())
				{
					attFaction = r.IsDBNull(0) ? 0 : r.GetInt32(0); // Treat NULL as 0 (Neutral/Ronin)
					attCombat = r.GetInt32(1);
					attName = r.GetString(2);
				}
			}

			// City / Defenders
			var cityCmd = conn.CreateCommand();
			cityCmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
			cityCmd.Parameters.AddWithValue("$cid", cityId);
			var cityRes = cityCmd.ExecuteScalar();
			int cityFaction = (cityRes != null && cityRes != DBNull.Value) ? Convert.ToInt32(cityRes) : 0;

			if (attFaction == cityFaction) return; // Friendly visit

			// Defenders
			var defCmd = conn.CreateCommand();
			defCmd.CommandText = "SELECT MAX(combat) FROM officers WHERE location_id = $cid AND faction_id = $fid";
			defCmd.Parameters.AddWithValue("$cid", cityId);
			defCmd.Parameters.AddWithValue("$fid", cityFaction);
			var defRes = defCmd.ExecuteScalar();
			int defCombat = (defRes != null && defRes != DBNull.Value) ? Convert.ToInt32(defRes) : 0;

			// 2. Resolve
			// Bonus needed to attack Fortified city? 
			// Let's say City gives +20 Defense Bonus naturally
			int defenseValues = defCombat + 20;

			int roll = new Random().Next(-20, 20); // Variance
			bool attackerWins = (attCombat + roll) > defenseValues;

			// If empty city (defCombat is 0), attacker wins easily unless super unlucky?
			if (defCombat == 0) attackerWins = true;

			GD.Print($"Auto-Resolve: {attName} ({attCombat}) vs City ({defenseValues}) -> {(attackerWins ? "Win" : "Loss")}");

			if (attackerWins)
			{
				var flipCmd = conn.CreateCommand();
				flipCmd.CommandText = "UPDATE cities SET faction_id = $fid WHERE city_id = $cid";

				if (attFaction > 0)
					flipCmd.Parameters.AddWithValue("$fid", attFaction);
				else
					flipCmd.Parameters.AddWithValue("$fid", DBNull.Value);

				flipCmd.Parameters.AddWithValue("$cid", cityId);
				flipCmd.ExecuteNonQuery();
				GD.Print($"City Captured by {attName}!");
			}
		}
	}
}

// Data Structures
public class BattleContext
{
	public int LocationId;
	public string CityName;
	public int OwnerFactionId;

	public int AttackerFactionId;
	public int DefenderFactionId;

	public List<BattleOfficer> AllOfficers;
	public List<BattleOfficer> AttackerOfficers;
	public List<BattleOfficer> DefenderOfficers;

	public BattleObjective Objective;

	// Supplies (Mocked for now)
	public int AttackerSupplies = 100;
	public int DefenderSupplies = 100;
}

public class BattleOfficer
{
	public int OfficerId;
	public string Name;
	public int FactionId;
	public int Leadership;
	public int Strategy;
	public int Combat;
	public bool IsPlayer;
	public string Rank;
	public int Troops;
	public int MaxTroops;
}

public class BattleObjective
{
	public string Description;
	public ObjectiveType Type;
	public int TargetId; // Commander ID or Zone ID
}

public enum ObjectiveType
{
	Elimination,
	Defense,
	CapturePoint,
	Duel
}
