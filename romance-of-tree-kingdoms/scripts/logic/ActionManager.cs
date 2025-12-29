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

	private string _dbPath;

	public static ActionManager Instance { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		_dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");

		// Ensure Schema Update (Quick n dirty migration)
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			try
			{
				var cmd = conn.CreateCommand();
				cmd.CommandText = "ALTER TABLE officers ADD COLUMN destination_city_id INTEGER DEFAULT NULL";
				cmd.ExecuteNonQuery();
				GD.Print("Added destination_city_id column.");
			}
			catch
			{
				// Ignore if exists
			}
		}
	}

	public bool HasActionPoints(int playerId)
	{
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT current_action_points FROM officers WHERE officer_id = $id";
			cmd.Parameters.AddWithValue("$id", playerId);
			var result = cmd.ExecuteScalar();
			return (result != null && (long)result > 0);
		}
	}

	public bool DeclareAttack(int officerId, int cityId)
	{
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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

			// 3. Connectivity Check: Is target connected to ANY city owned by this faction?
			// "Supply Line" Check
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = @"
                SELECT COUNT(*) 
                FROM routes r
                JOIN cities c ON (CASE WHEN r.start_city_id = $cid THEN r.end_city_id ELSE r.start_city_id END) = c.city_id
                WHERE (r.start_city_id = $cid OR r.end_city_id = $cid)
				  AND c.faction_id = $fid";
			checkCmd.Parameters.AddWithValue("$cid", cityId);
			checkCmd.Parameters.AddWithValue("$fid", factionId);

			long connected = (long)checkCmd.ExecuteScalar();

			if (connected == 0)
			{
				GD.Print("Cannot attack! Target is not connected to any territory owned by your faction.");
				// Optional: Emit failure signal for UI feedback
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

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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

					transaction.Commit();
					GD.Print("Assist Action Successful! City Econ +10.");

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

	public void PerformRecruit(int playerId, int targetOfficerId)
	{
		if (!HasActionPoints(playerId))
		{
			GD.Print("Not enough Action Points!");
			return;
		}

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();

			// Get Player Info
			var pCmd = conn.CreateCommand();
			pCmd.CommandText = "SELECT faction_id, location_id, leadership FROM officers WHERE officer_id = $pid";
			pCmd.Parameters.AddWithValue("$pid", playerId);
			long pFaction = 0, pLoc = 0, pLead = 0;
			using (var r = pCmd.ExecuteReader())
			{
				if (r.Read())
				{
					pFaction = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					pLoc = r.GetInt64(1);
					pLead = r.GetInt64(2);
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

			// Attempt Recruit
			// Simple Roll: Leadership vs Difficulty (50)
			int roll = new Random().Next(0, 100);
			bool success = (pLead + roll) > 100; // e.g. 60 lead needs >40 roll (60% chance)

			using (var transaction = conn.BeginTransaction())
			{
				// Deduct AP regardless
				var deductCmd = conn.CreateCommand();
				deductCmd.CommandText = "UPDATE officers SET current_action_points = current_action_points - 1 WHERE officer_id = $pid";
				deductCmd.Parameters.AddWithValue("$pid", playerId);
				deductCmd.ExecuteNonQuery();

				if (success)
				{
					var joinCmd = conn.CreateCommand();
					joinCmd.CommandText = "UPDATE officers SET faction_id = $fid, rank = 'Officer' WHERE officer_id = $tid";
					joinCmd.Parameters.AddWithValue("$fid", pFaction);
					joinCmd.Parameters.AddWithValue("$tid", targetOfficerId);
					joinCmd.ExecuteNonQuery();
					GD.Print($"Success! {tName} has joined your faction.");
				}
				else
				{
					GD.Print($"Recruitment failed. {tName} was not impressed.");
				}
				transaction.Commit();
				EmitSignal(SignalName.ActionPointsChanged);
			}
		}
	}

	public void EndDay()
	{
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			// Restore AP for all officers (Rank based logic could go here)
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET current_action_points = 3"; // Default reset
			cmd.ExecuteNonQuery();

			// Advance Date
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT COUNT(*) FROM game_state";
			var count = (long)checkCmd.ExecuteScalar();

			if (count == 0)
			{
				// Init GameState if missing
				var initCmd = conn.CreateCommand();
				initCmd.CommandText = "INSERT INTO game_state (current_day, player_id) VALUES (1, 1)"; // Assuming player_id 1
				initCmd.ExecuteNonQuery();
			}
			else
			{
				var updateCmd = conn.CreateCommand();
				updateCmd.CommandText = "UPDATE game_state SET current_day = current_day + 1";
				updateCmd.ExecuteNonQuery();
			}

			GD.Print("Day Ended. AP Restored. Date Advanced.");

			EmitSignal(SignalName.ActionPointsChanged);
			EmitSignal(SignalName.NewDayStarted);
		}
	}

	public long GetPlayerLocation(int playerId)
	{
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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
		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
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
}
