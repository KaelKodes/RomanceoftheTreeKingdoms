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
	[Signal] public delegate void PlayerStatsChangedEventHandler();
	[Signal] public delegate void MapStateChangedEventHandler();


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
				FixLeaderRanks(conn);
				FixPlayerBaselines(conn);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"Schema Migration Error: {ex.Message}");
			}
		}
	}

	private void FixPlayerBaselines(SqliteConnection conn)
	{
		// Force sync if baselines are higher than current stats (clamping issue) or NULL/0
		var upCmd = conn.CreateCommand();
		upCmd.CommandText = "UPDATE officers SET base_strength = strength, base_leadership = leadership, base_intelligence = intelligence, base_politics = politics, base_charisma = charisma WHERE is_player = 1 AND (base_strength IS NULL OR base_strength = 0 OR base_strength > strength OR base_leadership > leadership);";
		int rows = upCmd.ExecuteNonQuery();
		if (rows > 0)
		{
			GD.Print($"[ActionManager] Synced baseline stats for player ({rows} fields checked).");
			EmitSignal(SignalName.PlayerStatsChanged);
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

				if (rank == "Officer" && rep < 50 && wins == 0) // Relaxed from 300 to 50 for corrective cleanup only
				{
					r.Close();
					var fixCmd = conn.CreateCommand();
					fixCmd.CommandText = "UPDATE officers SET rank = $rnk, max_troops = $mt, troops = MIN(troops, $mt) WHERE is_player = 1";
					fixCmd.Parameters.AddWithValue("$rnk", GameConstants.RANK_VOLUNTEER);
					fixCmd.Parameters.AddWithValue("$mt", GameConstants.TROOPS_VOLUNTEER);
					fixCmd.ExecuteNonQuery();
					GD.Print("Fixed invalid player rank (Officer -> Volunteer) and clamped troops.");
				}
			}
		}
	}

	private void FixLeaderRanks(SqliteConnection conn)
	{
		// All Faction Commanders should be Sovereign rank with 5 AP and appropriate troops
		var fixCmd = conn.CreateCommand();
		fixCmd.CommandText = "UPDATE officers SET rank = $rnk, max_troops = $mt, max_action_points = 5, current_action_points = MAX(current_action_points, 5) WHERE is_commander = 1 AND rank != $rnk";
		fixCmd.Parameters.AddWithValue("$rnk", GameConstants.RANK_SOVEREIGN);
		fixCmd.Parameters.AddWithValue("$mt", GameConstants.TROOPS_SOVEREIGN);
		int rows = fixCmd.ExecuteNonQuery();
		if (rows > 0) GD.Print($"Fixed rank for {rows} Faction Leaders (Promoted to Sovereign).");
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
			// 0. Proximity Check
			var locCmd = conn.CreateCommand();
			locCmd.CommandText = "SELECT (o1.location_id = o2.location_id) FROM officers o1, officers o2 WHERE o1.officer_id = $pid AND o2.officer_id = $tid";
			locCmd.Parameters.AddWithValue("$pid", playerId);
			locCmd.Parameters.AddWithValue("$tid", targetOfficerId);
			if ((long)locCmd.ExecuteScalar() == 0) { GD.Print("You must be in the same city to talk!"); return; }

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

			// 0. Proximity Check
			var locCmd = conn.CreateCommand();
			locCmd.CommandText = "SELECT (o1.location_id = o2.location_id) FROM officers o1, officers o2 WHERE o1.officer_id = $pid AND o2.officer_id = $tid";
			locCmd.Parameters.AddWithValue("$pid", playerId);
			locCmd.Parameters.AddWithValue("$tid", targetOfficerId);
			if ((long)locCmd.ExecuteScalar() == 0) { GD.Print("You must be in the same city to wine and dine!"); return; }

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

					int oldMax = GameConstants.TROOPS_VOLUNTEER; // Assume coming from Ronin
					int newMax = GameConstants.TROOPS_REGULAR;
					int troopGain = newMax - oldMax;

					var joinCmd = conn.CreateCommand();
					joinCmd.CommandText = "UPDATE officers SET faction_id = $fid, rank = $rnk, max_troops = $mt, troops = troops + $gain WHERE officer_id = $tid";
					joinCmd.Parameters.AddWithValue("$fid", pFaction);
					joinCmd.Parameters.AddWithValue("$rnk", GameConstants.RANK_REGULAR);
					joinCmd.Parameters.AddWithValue("$mt", newMax);
					joinCmd.Parameters.AddWithValue("$gain", troopGain);
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

	public bool PerformTroopOutfitting(int officerId, TroopType type, int tier)
	{
		var data = TroopDataManager.GetTroopData(type, tier);
		if (data == null) return false;

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			// 1. Get Officer/City Info
			var infoCmd = conn.CreateCommand();
			infoCmd.CommandText = @"
				SELECT o.faction_id, c.city_id, c.technology, f.gold_treasury, o.troops
				FROM officers o
				JOIN cities c ON o.location_id = c.city_id
				LEFT JOIN factions f ON o.faction_id = f.faction_id
				WHERE o.officer_id = $oid";
			infoCmd.Parameters.AddWithValue("$oid", officerId);

			int factionId = 0, cityId = 0, tech = 0, gold = 0, troops = 0;
			using (var r = infoCmd.ExecuteReader())
			{
				if (r.Read())
				{
					factionId = r.IsDBNull(0) ? 0 : r.GetInt32(0);
					cityId = r.GetInt32(1);
					tech = r.GetInt32(2);
					gold = r.IsDBNull(3) ? 0 : r.GetInt32(3);
					troops = r.GetInt32(4);
				}
				else return false;
			}

			// 2. Checks
			if (tech < data.TechRequirement)
			{
				GD.Print($"[ActionManager] Outfitting Failed: Tech too low ({tech} < {data.TechRequirement})");
				return false;
			}

			float cost = TroopDataManager.CalculateOutfittingCost(type, tier, troops);
			if (gold < (int)cost)
			{
				GD.Print($"[ActionManager] Outfitting Failed: Insufficient Gold ({gold} < {cost})");
				return false;
			}

			// 3. Execute
			using (var trans = conn.BeginTransaction())
			{
				try
				{
					// Deduct Gold from Faction
					var goldCmd = conn.CreateCommand();
					goldCmd.CommandText = "UPDATE factions SET gold_treasury = gold_treasury - $cost WHERE faction_id = $fid";
					goldCmd.Parameters.AddWithValue("$cost", (int)cost);
					goldCmd.Parameters.AddWithValue("$fid", factionId);
					goldCmd.ExecuteNonQuery();

					// Update Officer
					var updCmd = conn.CreateCommand();
					updCmd.CommandText = @"
						UPDATE officers 
						SET main_troop_type = $mtt, officer_type = $mtt, troop_tier = $tier, troop_variant = $var 
						WHERE officer_id = $oid";
					updCmd.Parameters.AddWithValue("$mtt", (int)type + 1);
					updCmd.Parameters.AddWithValue("$tier", tier);
					updCmd.Parameters.AddWithValue("$var", data.Variant);
					updCmd.Parameters.AddWithValue("$oid", officerId);
					updCmd.ExecuteNonQuery();

					trans.Commit();
					GD.Print($"[ActionManager] Officer {officerId} outfitted with {data.Variant} (Tier {tier}). Cost: {cost}g");
					return true;
				}
				catch (Exception ex)
				{
					GD.PrintErr($"[ActionManager] Outfitting Transaction Error: {ex.Message}");
					trans.Rollback();
					return false;
				}
			}
		}
	}

	public void EndDay()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET current_action_points = max_action_points";
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
		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT name, rank, reputation, max_troops FROM officers WHERE officer_id = $oid";
		cmd.Parameters.AddWithValue("$oid", officerId);

		using (var r = cmd.ExecuteReader())
		{
			if (!r.Read()) return;
			string name = r.GetString(0);
			string rank = r.GetString(1);
			int rep = r.IsDBNull(2) ? 0 : r.GetInt32(2);
			int maxTroops = r.IsDBNull(3) ? 0 : r.GetInt32(3);

			int currentLevel = GetRankLevel(rank);
			if (currentLevel >= 10) return; // Sovereign/Max reached

			// Find highest qualifying level
			int targetLevel = currentLevel;
			for (int l = 1; l <= 10; l++)
			{
				if (rep >= GameConstants.GetRequiredRep(l))
				{
					targetLevel = l;
				}
				else break;
			}

			if (targetLevel > currentLevel)
			{
				string newRank = GameConstants.GetRankTitle(targetLevel);
				int newMaxTroops = GameConstants.GetMaxTroopsByLevel(targetLevel);
				int troopGain = newMaxTroops - maxTroops;

				r.Close(); // Close reader to update

				// Check if player for stat points
				var checkPlayerCmd = conn.CreateCommand();
				checkPlayerCmd.CommandText = "SELECT is_player, strength, leadership, intelligence, politics, charisma FROM officers WHERE officer_id = $oid";
				checkPlayerCmd.Parameters.AddWithValue("$oid", officerId);
				bool isPlayer = false;
				int s = 0, l = 0, i = 0, p = 0, c = 0;
				using (var r2 = checkPlayerCmd.ExecuteReader())
				{
					if (r2.Read())
					{
						isPlayer = r2.GetInt32(0) == 1;
						s = r2.GetInt32(1); l = r2.GetInt32(2); i = r2.GetInt32(3); p = r2.GetInt32(4); c = r2.GetInt32(5);
					}
				}

				var upCmd = conn.CreateCommand();
				if (isPlayer)
				{
					upCmd.CommandText = @"
                        UPDATE officers 
                        SET rank = $rnk, 
                            max_troops = $mt, 
                            troops = MIN($mt, troops + $gain), 
                            last_promotion_day = (SELECT current_day FROM game_state), 
                            stat_points = stat_points + 5,
                            base_strength = $s, base_leadership = $l, base_intelligence = $i, base_politics = $p, base_charisma = $c
						WHERE officer_id = $oid";
					upCmd.Parameters.AddWithValue("$s", s);
					upCmd.Parameters.AddWithValue("$l", l);
					upCmd.Parameters.AddWithValue("$i", i);
					upCmd.Parameters.AddWithValue("$p", p);
					upCmd.Parameters.AddWithValue("$c", c);
				}
				else
				{
					upCmd.CommandText = "UPDATE officers SET rank = $rnk, max_troops = $mt, troops = MIN($mt, troops + $gain), last_promotion_day = (SELECT current_day FROM game_state) WHERE officer_id = $oid";
				}

				upCmd.Parameters.AddWithValue("$rnk", newRank);
				upCmd.Parameters.AddWithValue("$mt", newMaxTroops);
				upCmd.Parameters.AddWithValue("$gain", troopGain);
				upCmd.Parameters.AddWithValue("$oid", officerId);
				upCmd.ExecuteNonQuery();

				GD.Print($"[Promotion] {name} reached Rank {targetLevel}: {newRank} (+{troopGain} troops cap)");
				if (isPlayer) EmitSignal(SignalName.PlayerStatsChanged);
			}
		}
	}

	private int GetRankLevel(string rank) => GameConstants.GetLevelByRankName(rank);

	public bool DeclareAttack(int officerId, int cityId)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return false; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Get Officer Stats (Faction, Troops, MaxTroops)
			var offCmd = conn.CreateCommand();
			offCmd.CommandText = "SELECT faction_id, troops, max_troops FROM officers WHERE officer_id = $oid";
			offCmd.Parameters.AddWithValue("$oid", officerId);

			int factionId = 0;
			int troops = 0;
			int maxTroops = 0;

			using (var r = offCmd.ExecuteReader())
			{
				if (r.Read())
				{
					factionId = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
					troops = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
					maxTroops = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
				}
			}

			if (factionId <= 0)
			{
				GD.Print("Independent officers cannot declare war! Join a faction first.");
				return false;
			}

			// TROOP CHECK: Must have at least 15% of max troops or 150 (whichever is lower)
			int threshold = Math.Min(150, (int)(maxTroops * 0.15f));
			if (troops < threshold)
			{
				GD.Print($"You don't have enough troops to lead an attack! (Have: {troops}, Need: {threshold})");
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

			// 3b. Reverse Conflict Check (Prevent A <-> B Loops)
			// If Target is already attacking My Location, I cannot attack them back (I must defend)
			var revCmd = conn.CreateCommand();
			revCmd.CommandText = "SELECT COUNT(*) FROM pending_battles WHERE location_id = $myLoc AND source_location_id = $targetLoc";
			revCmd.Parameters.AddWithValue("$myLoc", currentLoc);
			revCmd.Parameters.AddWithValue("$targetLoc", cityId);
			long reverseCount = (long)revCmd.ExecuteScalar();

			if (reverseCount > 0)
			{
				GD.Print($"Cannot declare attack! City {cityId} is already marching on your location ({currentLoc}). You must defend!");
				return false;
			}

			// 3c. Friendly Fire Check
			var ownCmd = conn.CreateCommand();
			ownCmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
			ownCmd.Parameters.AddWithValue("$cid", cityId);
			var targetFactionObj = ownCmd.ExecuteScalar();
			int targetFactionId = (targetFactionObj != null && targetFactionObj != DBNull.Value) ? Convert.ToInt32(targetFactionObj) : 0;

			if (targetFactionId == factionId)
			{
				GD.Print($"Cannot attack City {cityId}! It already belongs to your faction ({factionId}).");
				return false;
			}

			// 4. Deduct AP and Declare
			// Update pending_battles schema if needed (HOTFIX MIGRATION)
			try
			{
				var alterCmd = conn.CreateCommand();
				alterCmd.CommandText = "ALTER TABLE pending_battles ADD COLUMN source_location_id INTEGER";
				alterCmd.ExecuteNonQuery();
			}
			catch { /* Column likely exists */ }
			try
			{
				var alterCmd = conn.CreateCommand();
				alterCmd.CommandText = "ALTER TABLE pending_battles ADD COLUMN leader_id INTEGER";
				alterCmd.ExecuteNonQuery();
			}
			catch { /* Column likely exists */ }

			ExecuteSql(conn, $"UPDATE officers SET current_action_points = current_action_points - 1, reputation = reputation + 15 WHERE officer_id = {officerId}");

			// Include source_location_id and leader_id in INSERT
			var insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO pending_battles (location_id, attacker_faction_id, source_location_id, leader_id) VALUES ($loc, $afid, $src, $lid)";
			insertCmd.Parameters.AddWithValue("$loc", cityId);
			insertCmd.Parameters.AddWithValue("$afid", factionId);
			insertCmd.Parameters.AddWithValue("$src", currentLoc);
			insertCmd.Parameters.AddWithValue("$lid", officerId);
			insertCmd.ExecuteNonQuery();

			GD.Print($"Attack DECLARED! Faction {factionId} is marching on City {cityId} from City {currentLoc}.");

			// DYNAMIC RELATIONSHIP: Residents of the city are NOT happy about being attacked
			UpdateCityFactionOpinion(conn, cityId, factionId, -5);

			EmitSignal(SignalName.MapStateChanged);

			EmitSignal(SignalName.ActionPointsChanged);

			return true;
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
	// [Signal] public delegate void PlayerStatsChangedEventHandler(); // New signal for player stat updates

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
			// Get current AP, Troops, Tier, Faction
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT current_action_points, troops, troop_tier, faction_id, gold FROM officers WHERE officer_id = $pid";
			checkCmd.Parameters.AddWithValue("$pid", playerId);

			long ap = 0;
			int troopCount = 0, tier = 1, factionId = 0, pGold = 0;

			using (var r = checkCmd.ExecuteReader())
			{
				if (r.Read())
				{
					ap = r.GetInt64(0);
					troopCount = r.GetInt32(1);
					tier = r.GetInt32(2);
					factionId = r.IsDBNull(3) ? 0 : r.GetInt32(3);
					pGold = r.GetInt32(4);
				}
			}

			if (ap <= 0)
			{
				GD.Print("No AP to travel!");
				return;
			}

			// Organization Fee: paid upfront for the march
			// Scale: (Troops / 10) * Tier
			int organizationFee = (int)((troopCount / 10f) * tier);

			if (factionId > 0)
			{
				var facGoldCmd = conn.CreateCommand();
				facGoldCmd.CommandText = "SELECT gold_treasury FROM factions WHERE faction_id = $fid";
				facGoldCmd.Parameters.AddWithValue("$fid", factionId);
				long treasury = Convert.ToInt64(facGoldCmd.ExecuteScalar());
				if (treasury < organizationFee) { GD.Print($"Faction treasury too low for organization fee ({organizationFee} required)!"); return; }
			}
			else if (pGold < organizationFee)
			{
				GD.Print($"Not enough personal gold for organization fee ({organizationFee} required)!");
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
					// Update AP and Location
					var updateCmd = conn.CreateCommand();
					updateCmd.CommandText = "UPDATE officers SET current_action_points = $ap, location_id = $loc WHERE officer_id = $pid";
					updateCmd.Parameters.AddWithValue("$ap", ap);
					updateCmd.Parameters.AddWithValue("$loc", finalLocation);
					updateCmd.Parameters.AddWithValue("$pid", playerId);
					updateCmd.ExecuteNonQuery();

					// Deduct Organization Fee
					if (factionId > 0)
						ExecuteSql(conn, $"UPDATE factions SET gold_treasury = gold_treasury - {organizationFee} WHERE faction_id = {factionId}");
					else
						ExecuteSql(conn, $"UPDATE officers SET gold = gold - {organizationFee} WHERE officer_id = {playerId}");

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

			// 1. Get Target Faction & Location
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT faction_id, name, location_id FROM officers WHERE officer_id = $tid";
			cmd.Parameters.AddWithValue("$tid", targetOfficerId);
			long targetFactionId = 0;
			string targetName = "";
			int targetLoc = 0;
			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					targetFactionId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
					targetName = r.GetString(1);
					targetLoc = r.GetInt32(2);
				}
			}

			// Proximity Check
			int playerLoc = (int)GetPlayerLocation(playerId);
			if (playerLoc != targetLoc)
			{
				GD.Print("You must be in the same city to request to join a faction!");
				return;
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

					int oldMax = GameConstants.TROOPS_VOLUNTEER;
					int newMax = GameConstants.TROOPS_REGULAR;
					int troopGain = newMax - oldMax;

					// Update Faction
					var joinCmd = conn.CreateCommand();
					joinCmd.CommandText = "UPDATE officers SET faction_id = $fid, rank = $rnk, max_troops = $mt, troops = MIN($mt, troops + $gain) WHERE officer_id = $pid";
					joinCmd.Parameters.AddWithValue("$fid", targetFactionId);
					joinCmd.Parameters.AddWithValue("$rnk", GameConstants.RANK_REGULAR);
					joinCmd.Parameters.AddWithValue("$mt", newMax);
					joinCmd.Parameters.AddWithValue("$gain", troopGain);
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
	public enum DomesticType { Commerce, Agriculture, Defense, PublicOrder, Technology, Security, Stability }

	public void PerformDomesticAction(int officerId, int cityId, DomesticType type)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Get Officer Stats including Skills & Gold
			var cmd = conn.CreateCommand();
			cmd.CommandText = @"
				SELECT name, leadership, intelligence, strength, politics, charisma, rank, faction_id, gold, 
				       farming, business, inventing, fortification, security, governance, public_attitude
				FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);

			string name = "Unknown";
			int lea = 0, intl = 0, str = 0, pol = 0, cha = 50;
			long factionId = 0;
			int officerGold = 0;
			Dictionary<string, int> skills = new Dictionary<string, int>();
			int publicAttitude = 0;
			bool isPlayer = false;

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
					officerGold = r.GetInt32(8);

					skills["farming"] = r.GetInt32(9);
					skills["business"] = r.GetInt32(10);
					skills["inventing"] = r.GetInt32(11);
					skills["fortification"] = r.GetInt32(12);
					skills["security"] = r.GetInt32(13);
					skills["governance"] = r.IsDBNull(14) ? 0 : r.GetInt32(14);
					publicAttitude = r.IsDBNull(15) ? 0 : r.GetInt32(15);
				}
			}

			// Check if Player for Base Stat updates
			var checkPCmd = conn.CreateCommand();
			checkPCmd.CommandText = "SELECT is_player FROM officers WHERE officer_id = $oid";
			checkPCmd.Parameters.AddWithValue("$oid", officerId);
			isPlayer = Convert.ToInt32(checkPCmd.ExecuteScalar()) == 1;

			// 2. Map Action to Stats and Skills
			int statValue = 0;
			string targetColumn = "";
			string actionName = "";
			string influencingSkill = "";
			string statColName = "";

			switch (type)
			{
				case DomesticType.Commerce:
					statValue = pol;
					targetColumn = "commerce";
					actionName = "Develop Business";
					influencingSkill = "business";
					statColName = "politics";
					break;
				case DomesticType.Agriculture:
					statValue = pol;
					targetColumn = "agriculture";
					actionName = "Cultivate Land";
					influencingSkill = "farming";
					statColName = "politics";
					break;
				case DomesticType.Defense:
					statValue = lea;
					targetColumn = "defense_level";
					actionName = "Bolster Defense";
					influencingSkill = "fortification";
					statColName = "leadership";
					break;
				case DomesticType.PublicOrder:
					statValue = str;
					targetColumn = "public_order";
					actionName = "Patrol";
					influencingSkill = "security";
					statColName = "strength";
					break;
				case DomesticType.Technology:
					statValue = intl;
					targetColumn = "technology";
					actionName = "Research";
					influencingSkill = "inventing";
					statColName = "intelligence";
					break;
				case DomesticType.Stability:
					statValue = pol;
					targetColumn = "public_order"; // Unified to Public Order
					actionName = "Stabilize";
					influencingSkill = "governance";
					statColName = "politics";
					break;
				case DomesticType.Security:
					statValue = str;
					targetColumn = "public_order"; // Unified to Public Order
					actionName = "Secure Territory";
					influencingSkill = "security";
					statColName = "strength";
					break;
			}

			// 3. Treasury vs Personal Cost Logic
			var cityInfoCmd = conn.CreateCommand();
			cityInfoCmd.CommandText = @"
				SELECT c.governor_id, c.faction_id, f.leader_id, f.gold_treasury
				FROM cities c
				LEFT JOIN factions f ON c.faction_id = f.faction_id
				WHERE c.city_id = $cid";
			cityInfoCmd.Parameters.AddWithValue("$cid", cityId);

			int cityGovId = 0;
			int cityFactionId = 0;
			int factionLeaderId = 0;
			long goldTreasury = 0;

			using (var r = cityInfoCmd.ExecuteReader())
			{
				if (r.Read())
				{
					cityGovId = r.IsDBNull(0) ? 0 : r.GetInt32(0);
					cityFactionId = r.IsDBNull(1) ? 0 : r.GetInt32(1);
					factionLeaderId = r.IsDBNull(2) ? 0 : r.GetInt32(2);
					goldTreasury = r.IsDBNull(3) ? 0 : r.GetInt64(3);
				}
			}

			bool isAssigned = (officerId == cityGovId || officerId == factionLeaderId) && (factionId == cityFactionId);
			int domesticCost = 100;

			if (isAssigned)
			{
				if (goldTreasury < domesticCost)
				{
					GD.Print($"[ActionManager] Faction treasury is too low! ({goldTreasury} < {domesticCost})");
					return;
				}
			}
			else
			{
				if (officerGold < domesticCost)
				{
					GD.Print($"[ActionManager] Not enough personal gold! ({officerGold} < {domesticCost})");
					return;
				}
			}

			// 4. Check for Redundancy (Is it already maxed?)
			int currentVal = 0;
			int maxStats = 1000;
			var cityCmd = conn.CreateCommand();
			cityCmd.CommandText = $"SELECT {targetColumn}, max_stats FROM cities WHERE city_id = $cid";
			cityCmd.Parameters.AddWithValue("$cid", cityId);
			using (var reader = cityCmd.ExecuteReader())
			{
				if (reader.Read())
				{
					currentVal = reader.GetInt32(0);
					maxStats = reader.IsDBNull(1) ? 1000 : reader.GetInt32(1);
				}
			}

			int cap = (type == DomesticType.PublicOrder) ? 100 : maxStats;
			if (currentVal >= cap)
			{
				GD.Print($"[ActionManager] {actionName} cancelled: {targetColumn} is already at its full potential ({currentVal}/{cap})!");
				return;
			}

			// 5. Calculate Gain
			// Formula: (Stat/2) + (Skill/5) + PublicAttitudeBonus + LinkingBonus + Random(5-15)
			int skillValue = skills.ContainsKey(influencingSkill) ? skills[influencingSkill] : 0;

			// Public Attitude Bonus (Inquire influence)
			float attitudeBonus = publicAttitude * 0.1f;

			// Linking Bonus: Other officers on the same mission in the same city
			int linkingBonuses = 0;
			var linkCmd = conn.CreateCommand();
			linkCmd.CommandText = "SELECT COUNT(*) FROM officers WHERE location_id = $cid AND current_mission = $miss AND officer_id != $me";
			linkCmd.Parameters.AddWithValue("$cid", cityId);
			linkCmd.Parameters.AddWithValue("$miss", type.ToString()); // This assumes mission string matches enum name
			linkCmd.Parameters.AddWithValue("$me", officerId);
			linkingBonuses = Convert.ToInt32(linkCmd.ExecuteScalar());

			int gain = (int)(statValue * 0.5f) + (int)(skillValue * 0.2f) + (int)attitudeBonus + (linkingBonuses * 5) + new Random().Next(5, 16);

			string updateCitySql = $"UPDATE cities SET {targetColumn} = MIN({cap}, {targetColumn} + {gain}) WHERE city_id = {cityId}";

			int meritGain = (gain > (int)(statValue * 0.6f)) ? 20 : 10;
			int repGain = 10 + (gain / 10);

			// 6. Growth Logic
			bool statUpped = new Random().Next(1, 11) == 1; // 10% chance for stat gain
			string statUpdate = statUpped ? $", {statColName} = {statColName} + 1" : "";
			if (statUpped && isPlayer)
				statUpdate += $", base_{statColName} = base_{statColName} + 1";

			// Skills always go up by 1 during the action
			string skillUpdate = $", {influencingSkill} = {influencingSkill} + 1";

			using (var trans = conn.BeginTransaction())
			{
				try
				{
					ExecuteSql(conn, updateCitySql);

					// Apply costs and gains to officer
					string paySql = isAssigned ? "" : $", gold = gold - {domesticCost}";
					ExecuteSql(conn, $@"
						UPDATE officers 
						SET current_action_points = current_action_points - 1, 
						    reputation = reputation + {repGain}, 
							merit_score = merit_score + {meritGain}, 
							days_service = days_service + 1
							{paySql}
							{statUpdate}
							{skillUpdate}
						WHERE officer_id = {officerId}");

					// If assigned, take from treasury
					if (isAssigned)
					{
						ExecuteSql(conn, $"UPDATE factions SET gold_treasury = gold_treasury - {domesticCost} WHERE faction_id = {cityFactionId}");
					}

					trans.Commit();

					string growthMsg = statUpped ? $" and your {statColName} increased!" : ".";
					GD.Print($"{name} performed {actionName} in city {cityId}. Gain: {gain}, +{repGain} Rep, +{meritGain} Merit. Your {influencingSkill} skill improved{growthMsg}");

					if (factionId > 0)
					{
						UpdateCityFactionOpinion(conn, cityId, (int)factionId, 1);
					}

					CheckPromotions(officerId, conn);
					EmitSignal(nameof(ActionPointsChanged));
					if (statUpped && isPlayer) EmitSignal(nameof(PlayerStatsChanged));
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Domestic Action Failed: {ex.Message}");
				}
			}
		}
	}

	private void UpdateCityFactionOpinion(SqliteConnection conn, int cityId, int factionId, int delta)
	{
		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT officer_id FROM officers WHERE location_id = $cid";
		cmd.Parameters.AddWithValue("$cid", cityId);

		var oids = new List<int>();
		using (var r = cmd.ExecuteReader())
		{
			while (r.Read()) oids.Add(r.GetInt32(0));
		}

		foreach (var oid in oids)
		{
			RelationshipManager.Instance.ModifyFactionRelation(oid, factionId, delta);
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
	// 2. Train: Improve Stats (Requires Mentor & Gold)
	public void PerformTrain(int officerId, int mentorId, string statName)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Validate Stat
			string sLow = statName.ToLower();
			string[] valid = { "leadership", "intelligence", "strength", "politics", "charisma" };
			if (Array.IndexOf(valid, sLow) == -1) { GD.PrintErr("Invalid Stat"); return; }

			// 1. Get Trainee Info
			var tCmd = conn.CreateCommand();
			tCmd.CommandText = $"SELECT {sLow}, gold, name, location_id FROM officers WHERE officer_id = $oid";
			tCmd.Parameters.AddWithValue("$oid", officerId);
			int tStat = 0;
			int tGold = 0;
			string tName = "";
			int tLoc = 0;
			using (var r = tCmd.ExecuteReader())
			{
				if (r.Read())
				{
					tStat = r.GetInt32(0);
					tGold = r.GetInt32(1);
					tName = r.GetString(2);
					tLoc = r.GetInt32(3);
				}
			}

			// 2. Validate Mentor
			var mCmd = conn.CreateCommand();
			mCmd.CommandText = $"SELECT {sLow}, name, location_id, last_mentored_day FROM officers WHERE officer_id = $mid";
			mCmd.Parameters.AddWithValue("$mid", mentorId);
			int mStat = 0;
			string mName = "";
			int mLoc = 0;
			int mLastMentored = 0;
			using (var r = mCmd.ExecuteReader())
			{
				if (r.Read())
				{
					mStat = r.GetInt32(0);
					mName = r.GetString(1);
					mLoc = r.GetInt32(2);
					mLastMentored = r.IsDBNull(3) ? 0 : r.GetInt32(3);
				}
			}

			if (mLoc != tLoc)
			{
				GD.Print("Mentor is not in this city!");
				return;
			}

			// Daily Limit check
			int currentDay = GetCurrentDay(conn);
			if (mLastMentored >= currentDay)
			{
				GD.Print($"{mName} is already exhausted from mentoring today!");
				return;
			}

			if (mStat <= tStat)
			{
				GD.Print("Mentor is not skilled enough to teach you!");
				return;
			}

			// 3. Calculate Cost
			int baseCost = GetTrainingCost(tStat);
			int relation = RelationshipManager.Instance.GetRelation(officerId, mentorId);

			// Relation Modifier: +/- 25% based on -50 to +50 delta from neutral
			// Rel 50 = 1.0x, Rel 100 = 0.75x, Rel 0 = 1.25x
			float multiplier = 1.0f - ((relation - 50.0f) / 200.0f);
			int finalCost = (int)(baseCost * multiplier);

			if (tGold < finalCost)
			{
				GD.Print($"Not enough Gold! Need {finalCost}g (Base {baseCost}g with Relation {relation}), Have {tGold}g.");
				return;
			}

			// 4. Execute
			using (var trans = conn.BeginTransaction())
			{
				try
				{
					// Weighted Random for 1-5 Gain
					// 1: 35%, 2: 30%, 3: 20%, 4: 10%, 5: 5%
					int roll = new Random().Next(1, 101);
					int gain = 1;
					if (roll > 95) gain = 5;
					else if (roll > 85) gain = 4;
					else if (roll > 65) gain = 3;
					else if (roll > 35) gain = 2;

					// Deduct Gold & AP, Add Stat
					ExecuteSql(conn, $@"
                        UPDATE officers 
                        SET {sLow} = {sLow} + {gain}, 
                            base_{sLow} = CASE WHEN is_player = 1 THEN base_{sLow} + {gain} ELSE base_{sLow} END,
                            reputation = reputation + 5, 
                            current_action_points = current_action_points - 1,
                            gold = gold - {finalCost}
						WHERE officer_id = {officerId}");

					// Mark Mentor as used today
					ExecuteSql(conn, $"UPDATE officers SET last_mentored_day = {currentDay} WHERE officer_id = {mentorId}");

					// Pay the mentor? Or does it go to the 'void'? 
					// Let's give the mentor a small cut (half) to simulate economy, or just void for now to check inflation.
					// User didn't specify, void is safer for now.

					// Relationship Bonus
					RelationshipManager.Instance.ModifyRelation(officerId, mentorId, 5, conn);

					trans.Commit();
					GD.Print($"Trained {statName} with {mName}! +{gain} Stat, Cost {finalCost}g. Relation +5.");

					CheckPromotions(officerId, conn);
					EmitSignal(nameof(ActionPointsChanged));
					EmitSignal(SignalName.PlayerStatsChanged);
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Training Failed: {ex.Message}");
				}
			}
		}
	}

	public List<(int OfficerId, string Name, int StatValue, int Cost, int Relation, bool IsAvailable)> GetPotentialMentors(int traineeId, string statName)
	{
		var list = new List<(int, string, int, int, int, bool)>();
		string sLow = statName.ToLower();

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			int currentDay = GetCurrentDay(conn);

			// 1. Get Trainee Info
			var tCmd = conn.CreateCommand();
			tCmd.CommandText = $"SELECT location_id, {sLow} FROM officers WHERE officer_id = $oid";
			tCmd.Parameters.AddWithValue("$oid", traineeId);
			int locId = 0;
			int tStat = 0;
			using (var r = tCmd.ExecuteReader())
			{
				if (r.Read())
				{
					locId = r.GetInt32(0);
					tStat = r.GetInt32(1);
				}
			}

			// 2. Find Mentors
			var mCmd = conn.CreateCommand();
			mCmd.CommandText = $"SELECT officer_id, name, {sLow}, last_mentored_day FROM officers WHERE location_id = $loc AND {sLow} > $tStat AND officer_id != $oid";
			mCmd.Parameters.AddWithValue("$loc", locId);
			mCmd.Parameters.AddWithValue("$tStat", tStat);
			mCmd.Parameters.AddWithValue("$oid", traineeId);

			using (var r = mCmd.ExecuteReader())
			{
				while (r.Read())
				{
					int mId = r.GetInt32(0);
					string name = r.GetString(1);
					int mStat = r.GetInt32(2);
					int mLastDay = r.IsDBNull(3) ? 0 : r.GetInt32(3);

					int relation = RelationshipManager.Instance.GetRelation(traineeId, mId);
					int baseCost = GetTrainingCost(tStat); // Cost based on STUDENT level
					float multiplier = 1.0f - ((relation - 50.0f) / 200.0f);
					int cost = (int)(baseCost * multiplier);

					bool isAvailable = (mLastDay < currentDay);
					list.Add((mId, name, mStat, cost, relation, isAvailable));
				}
			}
		}
		return list;
	}

	private int GetCurrentDay(SqliteConnection conn)
	{
		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT current_day FROM game_state LIMIT 1";
		return Convert.ToInt32(cmd.ExecuteScalar());
	}

	private int GetTrainingCost(int currentStat)
	{
		if (currentStat <= 20) return 20;
		if (currentStat <= 35) return 50;
		if (currentStat <= 50) return 100;
		if (currentStat <= 65) return 250;
		if (currentStat <= 75) return 500;
		if (currentStat <= 85) return 750;
		if (currentStat <= 90) return 1000;
		if (currentStat <= 95) return 2500;
		return 5000; // 96-100
	}

	// 4. Talk: Improve relationships
	public void PerformSocialTalk(int officerId, int targetId)
	{
		if (officerId == targetId) return;
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// Get Names
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name, charisma FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);

			string name = "Officer";
			int cha = 50;
			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					name = r.GetString(0);
					cha = r.IsDBNull(1) ? 50 : r.GetInt32(1);
				}
			}

			var tCmd = conn.CreateCommand();
			tCmd.CommandText = "SELECT name FROM officers WHERE officer_id = $tid";
			tCmd.Parameters.AddWithValue("$tid", targetId);
			string tName = "Target";
			using (var r = tCmd.ExecuteReader())
			{
				if (r.Read()) tName = r.GetString(0);
			}

			// Relationship gain based on Charisma
			int delta = 5 + (cha / 20); // 50 cha -> 7 gain, 100 cha -> 10 gain
			RelationshipManager.Instance.ModifyRelation(officerId, targetId, delta);

			ExecuteSql(conn, $"UPDATE officers SET current_action_points = current_action_points - 1, reputation = reputation + 2 WHERE officer_id = {officerId}");

			GD.Print($"{name} talked with {tName}. Relationship +{delta} and +2 Reputation!");
			CheckPromotions(officerId, conn);
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

	// --- Troop Recruitment ---
	public void PerformRecruitTroops(int officerId, UnitRole role)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			int locId = 0, tech = 0, factionGold = 0;
			string name = "";

			int faction_id = 0;
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = @"
					SELECT o.location_id, c.technology, o.name, f.gold_treasury, o.faction_id
					FROM officers o
					JOIN cities c ON o.location_id = c.city_id
					LEFT JOIN factions f ON o.faction_id = f.faction_id
					WHERE o.officer_id = $oid";
				cmd.Parameters.AddWithValue("$oid", officerId);

				using (var r = cmd.ExecuteReader())
				{
					if (r.Read())
					{
						locId = r.GetInt32(0);
						tech = r.GetInt32(1);
						name = r.GetString(2);
						factionGold = r.IsDBNull(3) ? 0 : r.GetInt32(3);
						faction_id = r.IsDBNull(4) ? 0 : r.GetInt32(4);
					}
				}
			}

			int targetUnitId = 0;
			string unitName = "";

			using (var unitCmd = conn.CreateCommand())
			{
				unitCmd.CommandText = @"
					SELECT unit_type_id, name, recruit_cost
					FROM unit_types
					WHERE role = $role
					ORDER BY unit_type_id DESC"; // Assumes higher ID = higher tier based on seed order
				unitCmd.Parameters.AddWithValue("$role", role.ToString());

				using (var r = unitCmd.ExecuteReader())
				{
					while (r.Read())
					{
						int id = r.GetInt32(0);
						string uName = r.GetString(1);

						bool meetsTech = true;
						if (uName.Contains("Heavy Infantry") && tech < 200) meetsTech = false;
						if (uName.Contains("Spear Guard") && tech < 500) meetsTech = false;
						if (uName.Contains("Lancer Cavalry") && tech < 300) meetsTech = false;
						if (uName.Contains("Heavy Cavalry") && tech < 600) meetsTech = false;
						if (uName.Contains("Crossbowman") && tech < 400) meetsTech = false;
						if (uName.Contains("Elite") && tech < 600) meetsTech = false;
						if (uName.Contains("Siege") && tech < 400) meetsTech = false;

						if (meetsTech)
						{
							targetUnitId = id;
							unitName = uName;
							break; // Found highest tier
						}
					}
				}
			}

			if (targetUnitId == 0) { GD.Print("No units available for this role!"); return; }

			// 3. Recruit (Costs 1 AP + Gold + PO Penalty)
			// Rule: 1 Gold per 5 soldiers is NOT used for base recruitment anymore.
			// Conscription costs Gold and significantly lowers Public Order.
			int conscriptionCost = 500;
			int poPenalty = 15;

			var cityPO = 0;
			using (var poCmd = conn.CreateCommand())
			{
				poCmd.CommandText = "SELECT public_order FROM cities WHERE city_id = $cid";
				poCmd.Parameters.AddWithValue("$cid", locId);
				cityPO = Convert.ToInt32(poCmd.ExecuteScalar());
			}

			if (cityPO < 20) { GD.Print("Cannot conscript during a Public Revolt!"); return; }
			if (faction_id > 0)
			{
				// Check faction treasury
				var goldCmd = conn.CreateCommand();
				goldCmd.CommandText = "SELECT gold_treasury FROM factions WHERE faction_id = $fid";
				goldCmd.Parameters.AddWithValue("$fid", faction_id);
				long treasury = Convert.ToInt64(goldCmd.ExecuteScalar());
				if (treasury < conscriptionCost) { GD.Print("Faction does not have enough Gold!"); return; }
			}

			using (var trans = conn.BeginTransaction())
			{
				try
				{
					ExecuteSql(conn, $"UPDATE officers SET unit_type_id = {targetUnitId}, troops = max_troops, current_action_points = current_action_points - 1, reputation = reputation + 5 WHERE officer_id = {officerId}");

					if (faction_id > 0)
						ExecuteSql(conn, $"UPDATE factions SET gold_treasury = gold_treasury - {conscriptionCost} WHERE faction_id = {faction_id}");

					ExecuteSql(conn, $"UPDATE cities SET public_order = MAX(0, public_order - {poPenalty}) WHERE city_id = {locId}");

					trans.Commit();
					GD.Print($"{name} conscripted {unitName}. Costs: {conscriptionCost} Gold, -{poPenalty} Public Order.");
					EmitSignal(nameof(ActionPointsChanged));
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Recruitment Failed: {ex.Message}");
				}
			}
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
			int rankLevel = GameConstants.GetLevelByRankName(rankStr);

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
					ExecuteSql(conn, $"UPDATE officers SET faction_id = {newFactionId}, rank = 'Sovereign', max_troops = {GameConstants.TROOPS_SOVEREIGN}, troops = {GameConstants.TROOPS_SOVEREIGN}, max_action_points = 5, current_action_points = 5 - 1, is_commander = 1 WHERE officer_id = {officerId}");

					// 3. Claim City - Leader becomes the Governor
					ExecuteSql(conn, $"UPDATE cities SET faction_id = {newFactionId}, is_hq = 1, governor_id = {officerId} WHERE city_id = {cityId}");

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



	public void PerformMove(int officerId, int targetCityId)
	{
		if (!HasActionPoints(officerId)) return;

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			// 1. Adjacency check
			var locCmd = conn.CreateCommand();
			locCmd.CommandText = "SELECT location_id FROM officers WHERE officer_id = $oid";
			locCmd.Parameters.AddWithValue("$oid", officerId);
			int currentLoc = Convert.ToInt32(locCmd.ExecuteScalar());

			if (currentLoc == targetCityId) return;

			var adjCmd = conn.CreateCommand();
			adjCmd.CommandText = "SELECT COUNT(*) FROM routes WHERE (start_city_id = $l1 AND end_city_id = $l2) OR (start_city_id = $l2 AND end_city_id = $l1)";
			adjCmd.Parameters.AddWithValue("$l1", currentLoc);
			adjCmd.Parameters.AddWithValue("$l2", targetCityId);
			if ((long)adjCmd.ExecuteScalar() == 0)
			{
				GD.PrintErr($"[ActionManager] Cannot move Officer {officerId} to non-adjacent City {targetCityId}");
				return;
			}

			// 1.5 Fetch Stats for Fee
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT troops, troop_tier, faction_id, gold FROM officers WHERE officer_id = $oid";
			checkCmd.Parameters.AddWithValue("$oid", officerId);
			int troopCount = 0, tier = 1, factionId = 0, pGold = 0;
			using (var r = checkCmd.ExecuteReader())
			{
				if (r.Read())
				{
					troopCount = r.GetInt32(0);
					tier = r.GetInt32(1);
					factionId = r.IsDBNull(2) ? 0 : r.GetInt32(2);
					pGold = r.GetInt32(3);
				}
			}
			int organizationFee = (int)((troopCount / 10f) * tier);

			if (factionId > 0)
			{
				var facGoldCmd = conn.CreateCommand();
				facGoldCmd.CommandText = "SELECT gold_treasury FROM factions WHERE faction_id = $fid";
				facGoldCmd.Parameters.AddWithValue("$fid", factionId);
				long treasury = Convert.ToInt64(facGoldCmd.ExecuteScalar());
				if (treasury < organizationFee) { GD.Print($"[ActionManager] Faction treasury too low for organization fee ({organizationFee})!"); return; }
			}
			else if (pGold < organizationFee)
			{
				GD.Print($"[ActionManager] Not enough personal gold for organization fee ({organizationFee})!");
				return;
			}

			// 2. Execute Move with Fee Deduction
			using (var trans = conn.BeginTransaction())
			{
				try
				{
					ExecuteSql(conn, $"UPDATE officers SET location_id = {targetCityId}, current_action_points = current_action_points - 1 WHERE officer_id = {officerId}");

					if (factionId > 0)
						ExecuteSql(conn, $"UPDATE factions SET gold_treasury = gold_treasury - {organizationFee} WHERE faction_id = {factionId}");
					else
						ExecuteSql(conn, $"UPDATE officers SET gold = gold - {organizationFee} WHERE officer_id = {officerId}");

					trans.Commit();
					GD.Print($"[ActionManager] Officer {officerId} moved to City {targetCityId}. Fee paid: {organizationFee}");
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Move Failed: {ex.Message}");
				}
			}

			EmitSignal(nameof(ActionPointsChanged));
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

	public static int GetHierarchyScore(int officerId, int factionId, int cityId)
	{
		int score = 0;
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Is Faction Leader?
			var leaderCmd = conn.CreateCommand();
			leaderCmd.CommandText = "SELECT COUNT(*) FROM factions WHERE leader_id = $oid AND faction_id = $fid";
			leaderCmd.Parameters.AddWithValue("$oid", officerId);
			leaderCmd.Parameters.AddWithValue("$fid", factionId);
			if ((long)leaderCmd.ExecuteScalar() > 0) return 1000;

			// 2. Is Governor? (Title)
			var govCmd = conn.CreateCommand();
			govCmd.CommandText = "SELECT COUNT(*) FROM cities WHERE governor_id = $oid AND city_id = $cid";
			govCmd.Parameters.AddWithValue("$oid", officerId);
			govCmd.Parameters.AddWithValue("$cid", cityId);
			if ((long)govCmd.ExecuteScalar() > 0) score += 500;

			// 3. Rank Level
			var rankCmd = conn.CreateCommand();
			rankCmd.CommandText = "SELECT rank FROM officers WHERE officer_id = $oid";
			rankCmd.Parameters.AddWithValue("$oid", officerId);
			string rank = (string)rankCmd.ExecuteScalar();
			score += GameConstants.GetLevelByRankName(rank);
		}
		return score;
	}

	// --- Officer Phase AI ---

	private struct OfficerTurnData
	{
		public int Id;
		public string Name;
		public int LocId;
		public int Fid;
		public string Mission;
		public int AP;
		public int MaxAP;
		public int Satisfaction;
	}

	public void ProcessAllOfficerTurns()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			// 1. Fetch all non-player officers
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT officer_id, name, location_id, faction_id, current_mission, current_action_points, max_action_points, satisfaction FROM officers WHERE is_player = 0";

			var officers = new List<OfficerTurnData>();
			using (var r = cmd.ExecuteReader())
			{
				while (r.Read())
				{
					officers.Add(new OfficerTurnData
					{
						Id = r.GetInt32(0),
						Name = r.GetString(1),
						LocId = r.GetInt32(2),
						Fid = r.IsDBNull(3) ? 0 : r.GetInt32(3),
						Mission = r.IsDBNull(4) ? null : r.GetString(4),
						AP = r.GetInt32(5),
						MaxAP = r.GetInt32(6),
						Satisfaction = r.IsDBNull(7) ? 100 : r.GetInt32(7)
					});
				}
			}

			// 2. Process each officer
			foreach (var o in officers)
			{
				ProcessSingleOfficerTurn(o, conn);
			}
		}
	}

	private void ProcessSingleOfficerTurn(OfficerTurnData data, SqliteConnection conn)
	{
		var rng = new Random();
		int currentAP = data.AP;

		// 1. Work Duty (If assigned and satisfied)
		if (!string.IsNullOrEmpty(data.Mission) && data.Satisfaction > 30)
		{
			if (currentAP > 0)
			{
				PerformWorkMission(data.Id, data.LocId, data.Mission);
				currentAP--;
			}
		}

		// 2. Spend Remaining AP on personal actions
		while (currentAP > 0)
		{
			float roll = (float)rng.NextDouble();
			if (roll < 0.5f) // Socialize
			{
				int targetId = FindRandomOfficerInCity(data.Id, data.LocId, conn);
				if (targetId > 0)
				{
					RelationshipManager.Instance.ModifyRelation(data.Id, targetId, 10);
					GD.Print($"[OfficerPhase] {data.Name} socialized with {targetId}");
				}
			}
			else if (roll < 0.8f) // Train
			{
				string[] stats = { "strength", "leadership", "intelligence", "politics", "charisma" };
				string targetStat = stats[rng.Next(stats.Length)];
				ExecuteSql(conn, $"UPDATE officers SET {targetStat} = {targetStat} + 1 WHERE officer_id = {data.Id}");
				GD.Print($"[OfficerPhase] {data.Name} trained {targetStat}");
			}

			currentAP--;
			ExecuteSql(conn, $"UPDATE officers SET current_action_points = {currentAP} WHERE officer_id = {data.Id}");
		}
	}

	private void PerformWorkMission(int officerId, int cityId, string mission)
	{
		if (mission == "Inquire")
		{
			PerformInquireAction(officerId, cityId);
			return;
		}

		DomesticType type;
		switch (mission)
		{
			case "Commerce": type = DomesticType.Commerce; break;
			case "Farming": type = DomesticType.Agriculture; break;
			case "Science": type = DomesticType.Technology; break;
			case "Defense": type = DomesticType.Defense; break;
			case "Order": type = DomesticType.PublicOrder; break;
			case "Security": type = DomesticType.Security; break;
			case "Stability": type = DomesticType.Stability; break;
			default: return;
		}

		PerformDomesticAction(officerId, cityId, type);
	}

	private int FindRandomOfficerInCity(int excludeId, int cityId, SqliteConnection conn)
	{
		var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT officer_id FROM officers WHERE location_id = $cid AND officer_id != $me ORDER BY RANDOM() LIMIT 1";
		cmd.Parameters.AddWithValue("$cid", cityId);
		cmd.Parameters.AddWithValue("$me", excludeId);
		var res = cmd.ExecuteScalar();
		return res != null ? Convert.ToInt32(res) : 0;
	}

	public void PerformInquireAction(int officerId, int cityId)
	{
		if (!HasActionPoints(officerId)) { GD.Print("Not enough AP!"); return; }

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT charisma FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);
			int cha = Convert.ToInt32(cmd.ExecuteScalar());

			int gain = (cha / 10) + new Random().Next(5, 15);

			using (var trans = conn.BeginTransaction())
			{
				try
				{
					ExecuteSql(conn, $"UPDATE officers SET public_attitude = public_attitude + {gain}, current_action_points = current_action_points - 1 WHERE officer_id = {officerId}");
					trans.Commit();
					GD.Print($"[Inquire] Officer {officerId} improved Public Attitude by {gain}.");
					EmitSignal(nameof(ActionPointsChanged));
				}
				catch (Exception ex)
				{
					trans.Rollback();
					GD.PrintErr($"Inquire Failed: {ex.Message}");
				}
			}
		}
	}
}
