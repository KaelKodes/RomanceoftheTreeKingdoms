using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;
using System.Collections.Generic;

public partial class ActionManager : Node
{
	// Signals
	[Signal] public delegate void ActionPointsChangedEventHandler();
	[Signal] public delegate void PlayerLocationChangedEventHandler();
	[Signal] public delegate void NewDayStartedEventHandler();

	// private string _dbPath; // Removed


	public static ActionManager Instance { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		// Ensure DB is ready
		DatabaseHelper.Initialize();
		DatabaseMigration.Run(DatabaseHelper.DbPath);

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			try
			{
				// Self-Correction for Rank Issue
				FixPlayerRank(conn);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"Schema Migration Error: {ex.Message}");
			}
		}
	}


	private void FixPlayerRank(SqliteConnection conn)
	{
		// Check if player is "Officer" but shouldn't be (Low Stats)
		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT rank, reputation, battles_won FROM officers WHERE is_player = 1";
		using (var r = cmd.ExecuteReader())
		{
			if (r.Read())
			{
				string rank = r.GetString(0);
				int rep = r.IsDBNull(1) ? 0 : r.GetInt32(1);
				int wins = r.IsDBNull(2) ? 0 : r.GetInt32(2);

				if (rank == "Officer" && rep < 100 && wins < 1)
				{
					r.Close();
					var fixCmd = conn.CreateCommand();
					fixCmd.CommandText = "UPDATE officers SET rank = 'Volunteer', max_troops = 1000 WHERE is_player = 1";
					fixCmd.ExecuteNonQuery();
					GD.Print("Fixed invalid player rank (Officer -> Volunteer).");
				}
			}
		}
	}

	public bool HasActionPoints(int playerId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT current_action_points FROM officers WHERE officer_id = $id";
			cmd.Parameters.AddWithValue("$id", playerId);
			var result = cmd.ExecuteScalar();
			return (result != null && (long)result > 0);
		}
	}

	// --- Interaction Actions ---

	public void PerformTalk(int playerId, int targetOfficerId)
	{
		if (!HasActionPoints(playerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			// 1. Deduct AP
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = $pid";
			cmd.Parameters.AddWithValue("$pid", playerId);
			cmd.ExecuteNonQuery();

			// 2. Improve Relation (+10)
			RelationshipManager.Instance.ModifyRelation(playerId, targetOfficerId, 10);
			GD.Print($"You chatted with Officer {targetOfficerId}. Relationship +10.");

			EmitSignal(SignalName.ActionPointsChanged);
		}
	}

	public void PerformWineAndDine(int playerId, int targetOfficerId)
	{
		if (!HasActionPoints(playerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Calculate Cost
			int count = 0;
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT count FROM wine_dine_history WHERE player_id = $pid AND target_id = $tid";
			checkCmd.Parameters.AddWithValue("$pid", playerId);
			checkCmd.Parameters.AddWithValue("$tid", targetOfficerId);
			var res = checkCmd.ExecuteScalar();
			if (res != null && res != DBNull.Value) count = Convert.ToInt32(res);

			int cost = 50 * (int)Math.Pow(2, count);

			// 2. Check Gold
			var goldCmd = conn.CreateCommand();
			goldCmd.CommandText = "SELECT gold FROM officers WHERE officer_id = $pid";
			goldCmd.Parameters.AddWithValue("$pid", playerId);
			int currentGold = Convert.ToInt32(goldCmd.ExecuteScalar());

			if (currentGold < cost)
			{
				GD.Print($"Not enough Gold! Need {cost}, have {currentGold}.");
				return;
			}

			// 3. Execute
			using (var trans = conn.BeginTransaction())
			{
				try
				{
					// Deduct AP & Gold
					var deductCmd = conn.CreateCommand();
					deductCmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1, gold = gold - $cost WHERE officer_id = $pid";
					deductCmd.Parameters.AddWithValue("$pid", playerId);
					deductCmd.Parameters.AddWithValue("$cost", cost);
					deductCmd.ExecuteNonQuery();

					// Update Log
					var logCmd = conn.CreateCommand();
					logCmd.CommandText = "INSERT INTO wine_dine_history (player_id, target_id, count) VALUES ($pid, $tid, $cnt) ON CONFLICT(player_id, target_id) DO UPDATE SET count = $cnt";
					logCmd.Parameters.AddWithValue("$pid", playerId);
					logCmd.Parameters.AddWithValue("$tid", targetOfficerId);
					logCmd.Parameters.AddWithValue("$cnt", count + 1);
					logCmd.ExecuteNonQuery();

					// Improve Relation (+25)
					RelationshipManager.Instance.ModifyRelation(playerId, targetOfficerId, 25);
					GD.Print($"Wined and Dined! Cost: {cost}g. Relationship +25.");

					trans.Commit();
					EmitSignal(SignalName.ActionPointsChanged);
				}
				catch
				{
					trans.Rollback();
				}
			}
		}
	}

	public void PerformRecruit(int playerId, int targetOfficerId)
	{
		if (!HasActionPoints(playerId))
		{
			GD.Print("Not enough Action Points!");
			return;
		}

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Get Player Info
			var pCmd = conn.CreateCommand();
			pCmd.CommandText = "SELECT faction_id, location_id, reputation FROM officers WHERE officer_id = $pid";
			pCmd.Parameters.AddWithValue("$pid", playerId);
			long pFaction = 0, pLoc = 0;
			int pRep = 0;
			using (var r = pCmd.ExecuteReader())
			{
				if (r.Read())
				{
					pFaction = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					pLoc = r.GetInt64(1);
					pRep = r.IsDBNull(2) ? 0 : r.GetInt32(2);
				}
			}

			if (pFaction == 0) { GD.Print("You must be in a faction to recruit!"); return; }

			// Get Target Info
			var tCmd = conn.CreateCommand();
			tCmd.CommandText = "SELECT faction_id, location_id, name FROM officers WHERE officer_id = $tid";
			tCmd.Parameters.AddWithValue("$tid", targetOfficerId);
			long tFaction = 0, tLoc = 0;
			string tName = "";
			using (var r = tCmd.ExecuteReader())
			{
				if (r.Read())
				{
					tFaction = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					tLoc = r.GetInt64(1);
					tName = r.GetString(2);
				}
			}

			if (tLoc != pLoc) { GD.Print("Target is not in this city!"); return; }
			if (tFaction != 0) { GD.Print("Target already serves a faction!"); return; }

			// --- New Recruitment Formula ---
			int relation = RelationshipManager.Instance.GetRelation(playerId, targetOfficerId);
			int factionRel = RelationshipManager.Instance.GetFactionRelation(targetOfficerId, (int)pFaction);

			// Score Calculation
			// Base Logic: They want high relation, good reputation, and to like your faction.
			int score = relation + factionRel + pRep;

			// Difficulty Barrier
			// Rel 100, Fac 0, Rep 0 = 100.
			// Rel 20, Fac 0, Rep 0 = 20.
			// Let's say Threshold is 60 for guaranteed, but roll adds variance.

			int roll = new Random().Next(0, 40); // 0-40 variance
			int threshold = 80; // Needed sum

			GD.Print($"Recruit Attempt: Rel({relation}) + FacRel({factionRel}) + Rep({pRep}) + Roll({roll}) vs Threshold({threshold})");

			if (score + roll >= threshold)
			{
				// Success!
				using (var transaction = conn.BeginTransaction())
				{
					var deductCmd = conn.CreateCommand();
					deductCmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = $pid";
					deductCmd.Parameters.AddWithValue("$pid", playerId);
					deductCmd.ExecuteNonQuery();

					var joinCmd = conn.CreateCommand();
					joinCmd.CommandText = "UPDATE officers SET faction_id = $fid, rank = 'Regular', max_troops = 1000 WHERE officer_id = $tid";
					joinCmd.Parameters.AddWithValue("$fid", pFaction);
					joinCmd.Parameters.AddWithValue("$tid", targetOfficerId);
					joinCmd.ExecuteNonQuery();

					// Add Reputation (+10)
					var repCmd = conn.CreateCommand();
					repCmd.CommandText = "UPDATE officers SET reputation = reputation + 10 WHERE officer_id = $pid";
					repCmd.Parameters.AddWithValue("$pid", playerId);
					repCmd.ExecuteNonQuery();

					transaction.Commit();
					GD.Print($"Success! {tName} has joined your faction. Rep +10.");
				}
				EmitSignal(SignalName.ActionPointsChanged);
			}
			else
			{
				// Fail
				using (var transaction = conn.BeginTransaction())
				{
					var deductCmd = conn.CreateCommand();
					deductCmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = $pid";
					deductCmd.Parameters.AddWithValue("$pid", playerId);
					deductCmd.ExecuteNonQuery();

					transaction.Commit();
					GD.Print($"Recruitment failed. {tName} is not interested (Score: {score}). Try improving relations or reputation.");
				}
				EmitSignal(SignalName.ActionPointsChanged);
			}
		}
	}

	public void EndDay()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET current_action_points = 3";
			cmd.ExecuteNonQuery();

			// Advance Date
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT current_day FROM game_state LIMIT 1";
			var res = checkCmd.ExecuteScalar();
			long day = (res != null) ? (long)res : 0;

			if (day == 0)
			{
				var initCmd = conn.CreateCommand();
				initCmd.CommandText = "INSERT INTO game_state (current_day, player_id) VALUES (1, 1)";
				initCmd.ExecuteNonQuery();
			}
			else
			{
				var updateCmd = conn.CreateCommand();
				updateCmd.CommandText = "UPDATE game_state SET current_day = current_day + 1";
				updateCmd.ExecuteNonQuery();
				day++;
			}

			// Increment Days of Service & Check Promotions for Player
			// Only if in a faction
			var servCmd = conn.CreateCommand();
			servCmd.CommandText = "UPDATE officers SET days_service = days_service + 1 WHERE faction_id IS NOT NULL AND faction_id > 0";
			servCmd.ExecuteNonQuery();

			// Check Player Promotion
			var pInfoCmd = conn.CreateCommand();
			pInfoCmd.CommandText = "SELECT officer_id FROM officers WHERE is_player = 1";
			var pRes = pInfoCmd.ExecuteScalar();
			if (pRes != null) CheckPromotions(Convert.ToInt32(pRes), conn);

			// Passive Troop Regeneration (10% or 200, cap at Max) for officers in peaceful cities
			// logic: UPDATE officers SET troops = MIN(max_troops, troops + MAX(200, max_troops / 10))
			// WHERE location_id IN (SELECT city_id FROM cities) 
			// AND location_id NOT IN (SELECT location_id FROM pending_battles)
			// AND faction_id IS NOT NULL (Maybe Ronin too? Sure, why not)

			var regenCmd = conn.CreateCommand();
			regenCmd.CommandText = @"
				UPDATE officers 
				SET troops = MIN(max_troops, troops + MAX(200, max_troops / 10))
				WHERE location_id IN (SELECT city_id FROM cities)
				AND location_id NOT IN (SELECT location_id FROM pending_battles)
			";
			int regenCount = regenCmd.ExecuteNonQuery();
			GD.Print($"Regenerated troops for {regenCount} officers (Peaceful Cities).");

			// Weekly Reset: Wine and Dine
			if (day % 7 == 0)
			{
				var resetWDCmd = conn.CreateCommand();
				resetWDCmd.CommandText = "DELETE FROM wine_dine_history"; // Simple clear all
				resetWDCmd.ExecuteNonQuery();
				GD.Print("Weekly Reset: Wine and Dine costs reset.");
			}

			GD.Print($"Day Ended. It is now Day {day}.");

			EmitSignal(SignalName.ActionPointsChanged);
			EmitSignal(SignalName.NewDayStarted);
		}
	}

	public void CheckPromotions(int officerId, SqliteConnection conn)
	{
		// Gates:
		// Regular: 100 Rep, 1 Win
		// Officer: 300 Rep, 3 Wins
		// Captain: 800 Rep, 5 Wins
		// General: 1500 Rep, 10 Wins

		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT name, rank, reputation, battles_won, max_troops FROM officers WHERE officer_id = $oid";
		cmd.Parameters.AddWithValue("$oid", officerId);

		using (var r = cmd.ExecuteReader())
		{
			if (!r.Read()) return;
			string name = r.GetString(0);
			string rank = r.GetString(1);
			int rep = r.IsDBNull(2) ? 0 : r.GetInt32(2);
			int wins = r.IsDBNull(3) ? 0 : r.GetInt32(3);
			int maxTroops = r.IsDBNull(4) ? 0 : r.GetInt32(4);

			string newRank = rank;
			int newMaxTroops = maxTroops;

			// Promotion Logic
			if (rep >= 1500 && wins >= 10) { newRank = GameConstants.RANK_GENERAL; newMaxTroops = GameConstants.TROOPS_GENERAL; }
			else if (rep >= 800 && wins >= 5) { newRank = GameConstants.RANK_CAPTAIN; newMaxTroops = GameConstants.TROOPS_CAPTAIN; }
			else if (rep >= 300 && wins >= 3) { newRank = GameConstants.RANK_OFFICER; newMaxTroops = GameConstants.TROOPS_OFFICER; }
			else if (rep >= 100 && wins >= 1) { newRank = GameConstants.RANK_REGULAR; newMaxTroops = GameConstants.TROOPS_REGULAR; }
			// Else stay same (or Volunteer)

			if (newRank != rank)
			{
				int currentLevel = GetRankLevel(rank);
				int newLevel = GetRankLevel(newRank);

				if (newLevel > currentLevel)
				{
					r.Close(); // Close reader to execute update
					var upCmd = conn.CreateCommand();
					upCmd.CommandText = "UPDATE officers SET rank = $rnk, max_troops = $mt, last_promotion_day = (SELECT current_day FROM game_state) WHERE officer_id = $oid";
					upCmd.Parameters.AddWithValue("$rnk", newRank);
					upCmd.Parameters.AddWithValue("$mt", newMaxTroops);
					upCmd.Parameters.AddWithValue("$oid", officerId);
					upCmd.ExecuteNonQuery();

					GD.Print($"PROMOTION! {name} has reached the rank of {newRank}!");
				}
			}
		}
	}

	private int GetRankLevel(string rank)
	{
		switch (rank)
		{
			case GameConstants.RANK_GENERAL: return 5;
			case GameConstants.RANK_CAPTAIN: return 4;
			case GameConstants.RANK_OFFICER: return 3;
			case GameConstants.RANK_REGULAR: return 2;
			default: return 1; // Volunteer
		}
	}

	public bool DeclareAttack(int officerId, int cityId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Get Officer Faction
			var offCmd = conn.CreateCommand();
			offCmd.CommandText = "SELECT faction_id FROM officers WHERE officer_id = $oid";
			offCmd.Parameters.AddWithValue("$oid", officerId);
			var res = offCmd.ExecuteScalar();
			int factionId = (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;

			if (factionId <= 0)
			{
				GD.Print("Independent officers cannot declare war! Join a faction first.");
				return false;
			}

			// 2. Limit Check: One attack per faction per day
			var limitCmd = conn.CreateCommand();
			limitCmd.CommandText = "SELECT COUNT(*) FROM pending_battles WHERE attacker_faction_id = $fid";
			limitCmd.Parameters.AddWithValue("$fid", factionId);
			long pendingCount = (long)limitCmd.ExecuteScalar();

			if (pendingCount > 0)
			{
				GD.Print($"Faction {factionId} already has a pending battle! Cannot declare another.");
				return false;
			}

			// 3. Adjacency Check: Is the officer in a city connected to the target?
			var locationCmd = conn.CreateCommand();
			locationCmd.CommandText = "SELECT location_id FROM officers WHERE officer_id = $oid";
			locationCmd.Parameters.AddWithValue("$oid", officerId);
			int currentLoc = Convert.ToInt32(locationCmd.ExecuteScalar());

			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = @"
                SELECT COUNT(*) 
                FROM routes 
                WHERE (start_city_id = $loc AND end_city_id = $cid)
				   OR (start_city_id = $cid AND end_city_id = $loc)";
			checkCmd.Parameters.AddWithValue("$loc", currentLoc);
			checkCmd.Parameters.AddWithValue("$cid", cityId);

			long connected = (long)checkCmd.ExecuteScalar();

			if (connected == 0)
			{
				GD.Print("Cannot attack! Target is not adjacent to your current location.");
				return false;
			}

			// 4. Declare Battle
			try
			{
				var cmd = conn.CreateCommand();
				cmd.CommandText = "INSERT INTO pending_battles (location_id, attacker_faction_id) VALUES ($loc, $att)";
				cmd.Parameters.AddWithValue("$loc", cityId);
				cmd.Parameters.AddWithValue("$att", factionId);
				cmd.ExecuteNonQuery();
				GD.Print($"Battle Declared at City {cityId} by Faction {factionId}!");

				// Deduct AP (Optional?)
				// Emit update
				EmitSignal(SignalName.ActionPointsChanged); // Force refresh
				return true;
			}
			catch (SqliteException)
			{
				GD.Print("Battle already declared here.");
				return false;
			}
		}
	}

	public void PerformAssist(int playerId, int cityId)
	{
		if (!HasActionPoints(playerId))
		{
			GD.Print("Not enough Action Points!");
			return;
		}

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			using (var transaction = conn.BeginTransaction())
			{
				try
				{
					// 1. Deduct AP
					var deductCmd = conn.CreateCommand();
					deductCmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = $pid";
					deductCmd.Parameters.AddWithValue("$pid", playerId);
					deductCmd.ExecuteNonQuery();

					// 2. Boost City Stats (Training)
					var boostCmd = conn.CreateCommand();
					boostCmd.CommandText = "UPDATE cities SET economic_value = economic_value + 10 WHERE city_id = $cid";
					boostCmd.Parameters.AddWithValue("$cid", cityId);
					boostCmd.ExecuteNonQuery();

					// 3. Add Reputation (+5)
					var repCmd = conn.CreateCommand();
					repCmd.CommandText = "UPDATE officers SET reputation = reputation + 5 WHERE officer_id = $pid";
					repCmd.Parameters.AddWithValue("$pid", playerId);
					repCmd.ExecuteNonQuery();

					transaction.Commit();
					GD.Print("Assist Action Successful! City Econ +10, Rep +5.");

					EmitSignal(SignalName.ActionPointsChanged);
				}
				catch (Exception ex)
				{
					transaction.Rollback();
					GD.PrintErr($"Action Failed: {ex.Message}");
				}
			}
		}
	}





	public long GetPlayerLocation(int playerId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT location_id FROM officers WHERE officer_id = $id";
			cmd.Parameters.AddWithValue("$id", playerId);
			var result = cmd.ExecuteScalar();
			return result != null ? (long)result : -1;
		}
	}

	// Additional Signal
	// [Signal] public delegate void PlayerLocationChangedEventHandler(); // Already defined above

	public void PerformTravel(int playerId, int targetCityId)
	{
		long currentLoc = GetPlayerLocation(playerId);
		if (currentLoc == -1 || currentLoc == targetCityId) return;

		// 1. Calculate Path (BFS)
		List<long> path = GetShortestPath((int)currentLoc, targetCityId);

		if (path.Count == 0)
		{
			GD.Print("No route found to target!");
			return;
		}

		// 2. Determine how far we can go
		int moves = 0;
		long finalLocation = currentLoc;
		bool reachedDestination = false;

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			// Get current AP
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT current_action_points FROM officers WHERE officer_id = $pid";
			checkCmd.Parameters.AddWithValue("$pid", playerId);
			long ap = (long)checkCmd.ExecuteScalar();

			if (ap <= 0)
			{
				GD.Print("No AP to travel!");
				return;
			}

			// Move step by step
			foreach (long nextCityId in path)
			{
				if (ap > 0)
				{
					ap--;
					moves++;
					finalLocation = nextCityId;
				}
				else
				{
					break;
				}
			}

			if (finalLocation == targetCityId) reachedDestination = true;

			// 3. Commit the Move
			using (var transaction = conn.BeginTransaction())
			{
				try
				{
					// Update AP
					var apCmd = conn.CreateCommand();
					apCmd.CommandText = "UPDATE officers SET current_action_points = $ap WHERE officer_id = $pid";
					apCmd.Parameters.AddWithValue("$ap", ap);
					apCmd.Parameters.AddWithValue("$pid", playerId);
					apCmd.ExecuteNonQuery();

					// Update Location
					var locCmd = conn.CreateCommand();
					locCmd.CommandText = "UPDATE officers SET location_id = $loc WHERE officer_id = $pid";
					locCmd.Parameters.AddWithValue("$loc", finalLocation);
					locCmd.Parameters.AddWithValue("$pid", playerId);
					locCmd.ExecuteNonQuery();

					// Update Destination (Persistence)
					var destCmd = conn.CreateCommand();
					if (reachedDestination)
					{
						destCmd.CommandText = "UPDATE officers SET destination_city_id = NULL WHERE officer_id = $pid";
					}
					else
					{
						destCmd.CommandText = "UPDATE officers SET destination_city_id = $dest WHERE officer_id = $pid";
						destCmd.Parameters.AddWithValue("$dest", targetCityId);
					}
					destCmd.Parameters.AddWithValue("$pid", playerId);
					destCmd.ExecuteNonQuery();

					transaction.Commit();

					if (reachedDestination)
						GD.Print($"Arrived at City {targetCityId}!");
					else
						GD.Print($"Traveled {moves} hops. Stopped at City {finalLocation} (Out of AP). Continuing tomorrow.");

					EmitSignal(SignalName.ActionPointsChanged);
					EmitSignal(SignalName.PlayerLocationChanged);
				}
				catch (Exception ex)
				{
					transaction.Rollback();
					GD.PrintErr($"Travel error: {ex.Message}");
				}
			}
		}
	}

	public void ContinueTravel(int playerId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT destination_city_id FROM officers WHERE officer_id = $pid";
			cmd.Parameters.AddWithValue("$pid", playerId);
			var result = cmd.ExecuteScalar();

			if (result != null && result != DBNull.Value)
			{
				int destId = Convert.ToInt32(result);
				GD.Print($"Resuming travel to {destId}...");
				PerformTravel(playerId, destId);
			}
			else
			{
				GD.Print("No active destination to resume.");
			}
		}
	}

	public void CancelTravel(int playerId)
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET destination_city_id = NULL WHERE officer_id = $pid";
			cmd.Parameters.AddWithValue("$pid", playerId);
			cmd.ExecuteNonQuery();
			GD.Print("Travel plan cancelled.");
		}
	}

	// BFS to find shortest path of City IDs
	private List<long> GetShortestPath(int startId, int endId)
	{
		// Adjacency List
		var graph = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>>();

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT start_city_id, end_city_id FROM routes";
			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					long u = reader.GetInt64(0);
					long v = reader.GetInt64(1);
					if (!graph.ContainsKey(u)) graph[u] = new System.Collections.Generic.List<long>();
					if (!graph.ContainsKey(v)) graph[v] = new System.Collections.Generic.List<long>(); // Routes are bidirectional? Assuming yes for roads

					graph[u].Add(v);
					graph[v].Add(u);
				}
			}
		}

		// BFS
		var queue = new System.Collections.Generic.Queue<long>();
		var parent = new System.Collections.Generic.Dictionary<long, long>();
		var visited = new System.Collections.Generic.HashSet<long>();

		queue.Enqueue(startId);
		visited.Add(startId);

		bool found = false;
		while (queue.Count > 0)
		{
			long curr = queue.Dequeue();
			if (curr == endId)
			{
				found = true;
				break;
			}

			if (graph.ContainsKey(curr))
			{
				foreach (var neighbor in graph[curr])
				{
					if (!visited.Contains(neighbor))
					{
						visited.Add(neighbor);
						parent[neighbor] = curr;
						queue.Enqueue(neighbor);
					}
				}
			}
		}

		var path = new System.Collections.Generic.List<long>();
		if (found)
		{
			long curr = endId;
			while (curr != startId)
			{
				path.Add(curr);
				curr = parent[curr];
			}
			path.Reverse();
		}
		return path;
	}

	public void PerformJoinFaction(int playerId, int targetOfficerId)
	{
		if (!HasActionPoints(playerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Get Target Faction
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT faction_id, name FROM officers WHERE officer_id = $tid";
			cmd.Parameters.AddWithValue("$tid", targetOfficerId);
			long targetFactionId = 0;
			string targetName = "";
			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					targetFactionId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					targetName = r.GetString(1);
				}
			}

			if (targetFactionId == 0)
			{
				GD.Print($"{targetName} is a Ronin/Free Officer. You cannot join them!");
				return;
			}

			// 2. Execute Join
			using (var trans = conn.BeginTransaction())
			{
				try
				{
					// Deduct AP
					var apCmd = conn.CreateCommand();
					apCmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = $pid";
					apCmd.Parameters.AddWithValue("$pid", playerId);
					apCmd.ExecuteNonQuery();

					// Update Faction
					var joinCmd = conn.CreateCommand();
					joinCmd.CommandText = "UPDATE officers SET faction_id = $fid, rank = 'Regular', max_troops = 1000 WHERE officer_id = $pid";
					joinCmd.Parameters.AddWithValue("$fid", targetFactionId);
					joinCmd.Parameters.AddWithValue("$pid", playerId);
					joinCmd.ExecuteNonQuery();

					trans.Commit();
					GD.Print($"You have joined Faction {targetFactionId}! Welcome aboard.");

					EmitSignal(SignalName.ActionPointsChanged);
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Join Failed: {ex.Message}");
				}
			}
		}
	}

	// --- City Management ---
	public enum DomesticType { Commerce, Agriculture, Defense, PublicOrder, Technology }

	public void PerformDomesticAction(int officerId, int cityId, DomesticType type)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Get Officer Stats
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name, leadership, intelligence, strength, politics, charisma, rank, faction_id FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);

			string name = "Unknown";
			int lea = 0, intl = 0, str = 0, pol = 0, cha = 50;
			long factionId = 0;

			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					name = r.GetString(0);
					lea = r.GetInt32(1);
					intl = r.GetInt32(2);
					str = r.GetInt32(3);
					pol = r.GetInt32(4);
					cha = r.IsDBNull(5) ? 50 : r.GetInt32(5);
					factionId = r.IsDBNull(7) ? 0 : r.GetInt64(7);
				}
			}

			// 3. Calculate Gain
			int statValue = 0;
			string targetColumn = "";
			string actionName = "";

			switch (type)
			{
				case DomesticType.Commerce:
					statValue = pol;
					targetColumn = "commerce";
					actionName = "Develop Commerce";
					break;
				case DomesticType.Agriculture:
					statValue = pol;
					targetColumn = "agriculture";
					actionName = "Cultivate Land";
					break;
				case DomesticType.Defense:
					statValue = lea;
					targetColumn = "defense_level";
					actionName = "Bolster Defense";
					break;
				case DomesticType.PublicOrder:
					statValue = str; // RotTK8 uses Strength for Order (Martial Law)
					targetColumn = "public_order";
					actionName = "Patrol";
					break;
				case DomesticType.Technology:
					statValue = intl; // RotTK8 uses Intelligence for Tech
					targetColumn = "technology";
					actionName = "Research";
					break;
			}

			// Formula: Base (Stat/2) + Random(5-15)
			// Example: 90 Pol -> 45 + 10 = 55 gain
			int gain = (int)(statValue * 0.5f) + new Random().Next(5, 16);

			string updateSql = $"UPDATE cities SET {targetColumn} = {targetColumn} + {gain} WHERE city_id = {cityId}";
			if (type == DomesticType.PublicOrder)
			{
				updateSql = $"UPDATE cities SET public_order = MIN(100, public_order + {gain}) WHERE city_id = {cityId}";
			}

			ExecuteSql(conn, updateSql);

			// 4. Rewards (Reputation, Gold, Exp)
			ExecuteSql(conn, $"UPDATE officers SET current_action_points = current_action_points - 1, reputation = reputation + 10, gold = gold + 50, days_service = days_service + 1 WHERE officer_id = {officerId}");

			GD.Print($"{name} performed {actionName} in city {cityId}. Gain: {gain}!");

			// Notify UI
			EmitSignal(nameof(ActionPointsChanged)); // Refresh HUD
		}
	}

	// --- Personal Actions (Wisdom) ---

	// 1. Rest: Heal Troops (30% + random bonus)
	public void PerformRest(int officerId)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Get current and max troops
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT troops, max_troops, name FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);

			int current = 0;
			int max = 0;
			string name = "";

			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					current = r.GetInt32(0);
					max = r.GetInt32(1);
					name = r.GetString(2);
				}
			}

			if (current >= max)
			{
				GD.Print($"{name} is already at full strength.");
				return; // Optional: Don't charge AP? Or charge for "R&R"? Let's charge for now as they "Rest".
			}

			int heal = (int)(max * 0.3f) + new Random().Next(10, 50);
			int newTroops = Math.Min(max, current + heal);

			ExecuteSql(conn, $"UPDATE officers SET troops = {newTroops}, current_action_points = current_action_points - 1 WHERE officer_id = {officerId}");
			GD.Print($"{name} rested and recovered {newTroops - current} troops!");

			EmitSignal(nameof(ActionPointsChanged));
		}
	}

	// 2. Study: Improve Stats
	public void PerformStudy(int officerId, string statName)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Validate Stat Name
			string[] validStats = { "leadership", "intelligence", "strength", "politics", "charisma" };
			if (Array.IndexOf(validStats, statName.ToLower()) == -1)
			{
				GD.PrintErr("Invalid Stat for study");
				return;
			}

			int gain = new Random().Next(1, 3); // +1 or +2
												// Experience gain could be implicit (e.g. 100 exp = +1 stat), but simpler direct gain for now.
												// RotTK 8 uses exp, but for MVP we do direct small gains.

			// Cap stats at 100? or 255? Let's assume soft cap 100 for now or just let them grow.
			// We'll just add.

			ExecuteSql(conn, $"UPDATE officers SET {statName.ToLower()} = {statName.ToLower()} + {gain}, current_action_points = current_action_points - 1 WHERE officer_id = {officerId}");

			GD.Print($"Studied {statName}! Gained +{gain}!");
			EmitSignal(nameof(ActionPointsChanged));
		}
	}

	// 3. Resign: Leave Faction
	public void PerformResign(int officerId)
	{
		// Costs 0 AP usually, but let's charge 1 to prevent spam/abuse logic in turn 1.
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Check if Leader (Leaders cannot resign)
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT faction_id, rank, name FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);

			string rank = "";
			long factionId = 0;
			string name = "";

			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					factionId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					rank = r.GetString(1);
					name = r.GetString(2);
				}
			}

			if (factionId == 0) { GD.Print("You are already a Ronin!"); return; }
			if (rank == GameConstants.RANK_SOVEREIGN || rank == "Sovereign") // Safety check
			{
				GD.Print("Sovereigns cannot resign! You must be defeated or die.");
				return;
			}

			// Execute Resianation
			// Set Faction NULL, Rank Volunteer (or Ronin concept), Troops 0 (Personal Guard only logic) or Keep Troops?
			// RotTK: Resigning means giving up command, so troops go to 0 or very low private guard.
			// Let's set Troops 0, Rank Volunteer.

			ExecuteSql(conn, $"UPDATE officers SET faction_id = NULL, rank = 'Volunteer', troops = 0, current_action_points = current_action_points - 1 WHERE officer_id = {officerId}");

			GD.Print($"{name} has resigned from their post and is now a Ronin.");
			EmitSignal(nameof(ActionPointsChanged)); // Refresh HUD
		}
	}

	// 4. Rise Up: Form Faction
	public void PerformRiseUp(int officerId, int cityId)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Check if Ronin
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT faction_id, name FROM officers WHERE officer_id = $oid";
			checkCmd.Parameters.AddWithValue("$oid", officerId);
			long currentFid = 0;
			string name = "";
			using (var r = checkCmd.ExecuteReader())
			{
				if (r.Read())
				{
					currentFid = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					name = r.GetString(1);
				}
			}

			if (currentFid != 0) { GD.Print("You must be a Ronin (Free Officer) to Rise Up!"); return; }

			// Check City Owner
			var cityCmd = conn.CreateCommand();
			cityCmd.CommandText = "SELECT faction_id, (SELECT count(*) from pending_battles where location_id = $cid) as pending FROM cities WHERE city_id = $cid";
			cityCmd.Parameters.AddWithValue("$cid", cityId);
			long cityOwnerId = 0;
			long isPending = 0;
			using (var r = cityCmd.ExecuteReader())
			{
				if (r.Read())
				{
					cityOwnerId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					isPending = r.GetInt64(1);
				}
			}

			// Restriction 1: Must be in Current City (Implicit usually, but good to check)
			// Restriction 2: Conflict Check
			if (isPending > 0)
			{
				GD.Print("Cannot Rise Up! The city is under threat of battle.");
				return;
			}

			if (cityOwnerId != 0)
			{
				GD.Print("Cannot Rise Up in an occupied city (Revolt not implemented yet). Go to a Neutral City.");
				return;
			}

			// Restriction 3: Rank Check
			// Get Rank from officer (we did query name/fid above, let's query rank too)
			var rankCmd = conn.CreateCommand();
			rankCmd.CommandText = "SELECT rank FROM officers WHERE officer_id = $oid";
			rankCmd.Parameters.AddWithValue("$oid", officerId);
			string rankStr = (string)rankCmd.ExecuteScalar();
			int rankLevel = GameConstants.GetRankLevel(rankStr);

			if (rankLevel < 3)
			{
				GD.Print($"You lack the stature to lead a rebellion! (Rank: {rankStr}, Required: Captain/Lev 3)");
				return;
			}

			// Execute Rise Up
			using (var trans = conn.BeginTransaction())
			{
				try
				{
					// 1. Create Faction
					var newFacCmd = conn.CreateCommand();
					newFacCmd.CommandText = "INSERT INTO factions (name, color, leader_id) VALUES ($name || '''s Army', '#FFFFFF', $oid); SELECT last_insert_rowid();";
					newFacCmd.Parameters.AddWithValue("$name", name);
					newFacCmd.Parameters.AddWithValue("$oid", officerId);
					long newFactionId = (long)newFacCmd.ExecuteScalar();

					// 2. Assign Officer
					ExecuteSql(conn, $"UPDATE officers SET faction_id = {newFactionId}, rank = 'Sovereign', max_troops = {GameConstants.TROOPS_SOVEREIGN}, troops = {GameConstants.TROOPS_SOVEREIGN}, is_commander = 1, current_action_points = current_action_points - 1 WHERE officer_id = {officerId}");

					// 3. Claim City
					ExecuteSql(conn, $"UPDATE cities SET faction_id = {newFactionId}, is_hq = 1 WHERE city_id = {cityId}");

					trans.Commit();
					GD.Print($"{name} has Risen Up in {cityId} and founded a new kingdom!");
					EmitSignal(nameof(ActionPointsChanged));
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Rise Up Failed: {ex.Message}");
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
