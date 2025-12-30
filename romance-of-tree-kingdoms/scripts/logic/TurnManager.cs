using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class TurnManager : Node
{
	[Signal] public delegate void TurnStartedEventHandler(int factionId, bool isPlayer);
	[Signal] public delegate void TurnEndedEventHandler();
	[Signal] public delegate void NewWeekStartedEventHandler();

	public static TurnManager Instance { get; private set; }
	// private string _dbPath; // Removed

	// Turn State
	private List<int> _turnQueue = new List<int>();
	private int _currentTurnIndex = -1;
	private bool _isTurnActive = false;

	// Conflict Queue: (LocationId, AttackerFactionId, SourceLocationId)
	private Queue<(int cityId, int attackerFactionId, int sourceCityId)> _conflictQueue = new Queue<(int, int, int)>();

	public override void _Ready()
	{
		Instance = this;
		// _dbPath not needed, using DatabaseHelper
	}

	// Called by ActionManager.EndDay or directly from UI "End Turn" button
	// But wait, user ends *their* turn. System ends *Day*.
	// Let's separate it. 
	// StartTurnCycle() -> logic -> EndTurn() -> Next...

	public void EnsureTurnSystemStarted()
	{
		if (_turnQueue.Count == 0)
		{
			GD.Print("Turn System Not Started. Initializing...");
			StartNewDay();
		}
	}

	public void StartNewDay()
	{
		int currentDay = GetCurrentDay();

		// If Day 1 or (Day-1) % 7 == 0 (Weeks start on 1, 8, 15), Re-roll Initiative
		if (currentDay == 1 || (currentDay - 1) % 7 == 0)
		{
			RollInitiative();
			EmitSignal(SignalName.NewWeekStarted);

			// AI Strategic Refresh
			var ai = GetNode<FactionAI>("/root/FactionAI");
			using (var conn = DatabaseHelper.GetConnection())
			{
				conn.Open();

				// Monthly Cycle (approx 4 weeks)
				if (currentDay == 1 || (currentDay - 1) % 28 == 0)
				{
					GD.Print("[TurnManager] NEW MONTH! Refreshing Strategic Goals...");
					RefreshAllFactionGoals(conn, ai, true);
				}
				else
				{
					GD.Print("[TurnManager] NEW WEEK! Refreshing Weekly Tasks...");
					RefreshAllFactionGoals(conn, ai, false);
				}
			}
		}

		_currentTurnIndex = -1;
		AdvanceTurn();
	}

	private void RollInitiative()
	{
		_turnQueue.Clear();
		var factionScores = new Dictionary<int, float>();
		var rng = new Random();

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			// Get Faction Leaders and their stats
			// Assuming Highest Rank or specific 'is_commander' flag. 
			// For now, let's grab the officer with MAX(strategy) in the faction as the "Brain"
			cmd.CommandText = @"
				SELECT f.faction_id, MAX(o.intelligence) as strat, MAX(o.leadership) as lead
				FROM factions f
				JOIN officers o ON f.faction_id = o.faction_id
				GROUP BY f.faction_id
			";

			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					int fid = reader.GetInt32(0);
					int strat = reader.GetInt32(1);
					int lead = reader.GetInt32(2);

					float score = strat + (lead * 0.5f) + rng.Next(-10, 10);
					factionScores[fid] = score;
				}
			}

			// 2. Calculate Initiative for Player (Independent Turn - ID -1)
			var playerCmd = conn.CreateCommand();
			playerCmd.CommandText = "SELECT intelligence, leadership FROM officers WHERE is_player = 1";
			using (var reader = playerCmd.ExecuteReader())
			{
				if (reader.Read())
				{
					int pStrat = reader.GetInt32(0);
					int pLead = reader.GetInt32(1);

					float pScore = pStrat + (pLead * 0.5f) + rng.Next(-10, 10);
					factionScores[-1] = pScore;
				}
			}
		}

		// Sort by score descending
		_turnQueue = factionScores.OrderByDescending(x => x.Value).Select(x => x.Key).ToList();

		GD.Print($"Initiative Rolled for Week! Order: {string.Join(", ", _turnQueue)}");
	}

	private void AdvanceTurn()
	{
		_currentTurnIndex++;

		if (_currentTurnIndex >= _turnQueue.Count)
		{
			// All turns done for the day
			GD.Print("All turns complete. Checking for Conflicts...");

			if (CheckForConflicts())
			{
				ResolveNextConflict();
			}
			else
			{
				EndDayCycle();
			}
			return;
		}

		int factionId = _turnQueue[_currentTurnIndex];
		bool isPlayer = IsPlayerFaction(factionId);

		GD.Print($"Turn Start: Faction {factionId} (Player: {isPlayer})");
		EmitSignal(SignalName.TurnStarted, factionId, isPlayer);

		if (isPlayer)
		{
			// Unlock UI, wait for user to click "End Turn"
			_isTurnActive = true;
		}
		else
		{
			// AI Turn
			// Trigger FactionAI
			var ai = GetNode<FactionAI>("/root/FactionAI");
			ai.ProcessTurn(factionId);
		}
	}

	// Call this when Player clicks "End Turn"
	public void PlayerEndTurn()
	{
		if (_isTurnActive)
		{
			_isTurnActive = false;
			EndTurn();
		}
	}

	// Call this when AI finishes
	public void AIEndTurn()
	{
		EndTurn();
	}

	private void EndTurn()
	{
		EmitSignal(SignalName.TurnEnded);
		AdvanceTurn();
	}

	private bool IsPlayerFaction(int factionId)
	{
		// ID -1 is explicitly the independent Player turn
		if (factionId == -1) return true;

		// Otherwise, check if player is the LEADER of this faction
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT count(*) FROM officers WHERE is_player = 1 AND faction_id = $fid AND rank = 'Commander'";
			cmd.Parameters.AddWithValue("$fid", factionId);
			long count = (long)cmd.ExecuteScalar();
			return count > 0;
		}
	}

	private int GetCurrentDay()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT current_day FROM game_state LIMIT 1";
			var result = cmd.ExecuteScalar();
			return result != null ? Convert.ToInt32(result) : 1;
		}
	}

	private void ProcessCityDecay()
	{
		GD.Print("[TurnManager] Processing City Decay...");
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			// Get all owned cities
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT city_id, name, faction_id, decay_turns FROM cities WHERE faction_id > 0";

			var citiesToCheck = new List<(int id, string name, int fid, int turns)>();
			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					citiesToCheck.Add((
						reader.GetInt32(0),
						reader.GetString(1),
						reader.GetInt32(2),
						reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
					));
				}
			}

			foreach (var c in citiesToCheck)
			{
				// Check for officers
				var offCmd = conn.CreateCommand();
				offCmd.CommandText = "SELECT count(*) FROM officers WHERE location_id = $cid AND faction_id = $fid";
				offCmd.Parameters.AddWithValue("$cid", c.id);
				offCmd.Parameters.AddWithValue("$fid", c.fid);
				long officerCount = (long)offCmd.ExecuteScalar();

				if (officerCount > 0)
				{
					if (c.turns > 0)
					{
						// Reset Decay
						ExecuteSql(conn, $"UPDATE cities SET decay_turns = 0 WHERE city_id = {c.id}");
						GD.Print($"[Decay] {c.name} is occupied. Decay reset.");
					}
				}
				else
				{
					// Increment Decay
					int newDecay = c.turns + 1;
					if (newDecay >= 3)
					{
						// LOST CITY
						ExecuteSql(conn, $"UPDATE cities SET faction_id = NULL, decay_turns = 0 WHERE city_id = {c.id}");
						GD.Print($"[Decay] {c.name} has been abandoned too long! Reverted to Neutral.");
					}
					else
					{
						ExecuteSql(conn, $"UPDATE cities SET decay_turns = {newDecay} WHERE city_id = {c.id}");
						GD.Print($"[Decay] {c.name} is empty! Decay: {newDecay}/3");
					}
				}
			}
		}
	}

	// Conflict Resolution


	private bool CheckForConflicts()
	{
		_conflictQueue.Clear();
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT location_id, attacker_faction_id, source_location_id FROM pending_battles";
			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					int loc = reader.GetInt32(0);
					int att = reader.GetInt32(1);
					int src = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
					_conflictQueue.Enqueue((loc, att, src));
				}
			}
		}
		return _conflictQueue.Count > 0;
	}

	private void ResolveNextConflict()
	{
		if (_conflictQueue.Count == 0)
		{
			EndDayCycle();
			return;
		}

		var conflict = _conflictQueue.Dequeue();
		int cityId = conflict.cityId;
		int attackerFactionId = conflict.attackerFactionId;
		int sourceCityId = conflict.sourceCityId;

		// Check if player is involved
		var bm = GetNode<BattleManager>("/root/BattleManager");
		bm.CreateContext(cityId, sourceCityId); // Prepare context

		bool playerInvolved = bm.CurrentContext.AttackerOfficers.Any(o => o.IsPlayer) ||
							  bm.CurrentContext.DefenderOfficers.Any(o => o.IsPlayer);

		if (playerInvolved)
		{
			GD.Print("Player involved in conflict! Loading Battle Scene...");
			// Load Scene (Wait for callback)
			var scene = GD.Load<PackedScene>("res://scenes/BattleSetupUI.tscn");
			GetTree().Root.AddChild(scene.Instantiate());
			// Note: BattleSetupUI usually loads Map immediately. 
			// We need to ensure when Battle ends, it calls ResumeConflictResolution.
		}
		else
		{
			GD.Print($"AI Conflict at City {cityId}. Attacker Faction: {attackerFactionId}. Simulating...");
			// Simulate immediately
			// Find an officer belonging to the identified Attacker Faction
			int attId = 0;
			var champion = bm.CurrentContext.AttackerOfficers
				.Where(o => o.FactionId == attackerFactionId)
				.OrderByDescending(o => o.Strength) // Strongest leads
				.FirstOrDefault();

			if (champion != null)
			{
				attId = champion.OfficerId;
			}
			else
			{
				// Fallback: If no officer of that faction is physically present (Bug? Or they left?), 
				// pick ANY attacker to prevent stuck logic, but log warning.
				GD.PrintErr($"[ResolveNextConflict] No officer of faction {attackerFactionId} found at {cityId}! Picking random attacker.");
				attId = bm.CurrentContext.AttackerOfficers.FirstOrDefault()?.OfficerId ?? 0;
			}

			bm.SimulateBattle(attId, cityId);

			// Remove from pending
			ResolvePendingBattleDB(cityId);

			// Next
			CallDeferred(nameof(ResolveNextConflict));
		}
	}
	public void ResumeConflictResolution(int resolvedCityId = -1)
	{
		// Called by BattleManager after Player Battle
		if (resolvedCityId > 0)
		{
			ResolvePendingBattleDB(resolvedCityId);
		}
		else if (BattleManager.Instance.CurrentContext != null)
		{
			ResolvePendingBattleDB(BattleManager.Instance.CurrentContext.LocationId);
		}
		ResolveNextConflict();
	}

	private void ResolvePendingBattleDB(int cityId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "DELETE FROM pending_battles WHERE location_id = $id";
			cmd.Parameters.AddWithValue("$id", cityId);
			cmd.ExecuteNonQuery();
		}
	}

	private void EndDayCycle()
	{
		ProcessCityDecay();

		int currentDay = GetCurrentDay();
		if (currentDay % 28 == 0)
		{
			ProcessMonthlyHarvest();
		}

		// Ronin Agency: Independent officers can join factions if they like them
		FactionAI.Instance.ProcessRoninTurns();

		var am = GetNode<ActionManager>("/root/ActionManager");
		am.EndDay();

		StartNewDay();
	}

	private void ProcessMonthlyHarvest()
	{
		GD.Print("[TurnManager] Processing Monthly Harvest (Tax & Agri)...");
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// For each faction, sum their city yields
			var cmd = conn.CreateCommand();
			cmd.CommandText = @"
                SELECT 
                    f.faction_id, 
                    SUM(c.commerce * (CAST(c.public_order AS FLOAT) / 100.0)) as gold_yield,
                    SUM(c.agriculture * (CAST(c.public_order AS FLOAT) / 100.0)) as food_yield
                FROM factions f
                JOIN cities c ON f.faction_id = c.faction_id
				GROUP BY f.faction_id";

			var updates = new List<(int fid, int gold, int food)>();
			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					updates.Add((
						Convert.ToInt32(reader.GetValue(0)),
						(int)reader.GetDouble(1),
						(int)reader.GetDouble(2) * 10 // Food is usually higher volume
					));
				}
			}

			foreach (var u in updates)
			{
				ExecuteSql(conn, $"UPDATE factions SET gold = gold + {u.gold}, supplies = supplies + {u.food} WHERE faction_id = {u.fid}");
				GD.Print($"[Harvest] Faction {u.fid} collected {u.gold} Gold and {u.food} Supplies.");
			}
		}
	}

	private void RefreshAllFactionGoals(SqliteConnection conn, FactionAI ai, bool monthly)
	{
		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT faction_id FROM factions";
		var fids = new List<int>();
		using (var r = cmd.ExecuteReader())
		{
			while (r.Read()) fids.Add(r.GetInt32(0));
		}

		foreach (var fid in fids)
		{
			if (monthly) ai.UpdateMonthlyGoal(conn, fid);
			ai.UpdateWeeklyTask(conn, fid);
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
