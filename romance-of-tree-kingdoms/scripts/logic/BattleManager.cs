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
	public void CreateContext(int cityId, int sourceCityId = -1)
	{
		GD.Print($"Generating Battle Context for City {cityId} (Source: {sourceCityId})...");

		CurrentContext = new BattleContext();
		CurrentContext.LocationId = cityId;
		CurrentContext.SourceCityId = sourceCityId;

		// Fetch LeaderId from pending_battles if possible
		if (sourceCityId > 0)
		{
			using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
			{
				conn.Open();
				var cmd = conn.CreateCommand();
				cmd.CommandText = "SELECT leader_id FROM pending_battles WHERE location_id = $lid AND source_location_id = $sid";
				cmd.Parameters.AddWithValue("$lid", cityId);
				cmd.Parameters.AddWithValue("$sid", sourceCityId);
				var res = cmd.ExecuteScalar();
				if (res != null && res != DBNull.Value)
				{
					CurrentContext.LeaderId = Convert.ToInt32(res);
					GD.Print($"[BattleManager] Attack Leader Identified: {CurrentContext.LeaderId}");
				}
			}
		}

		FetchCityInfo(cityId);
		FetchOfficers(cityId); // Attack data defaults to 0 here

		// Fetch officers from the Source City (Attacking base)
		if (sourceCityId > 0)
		{
			FetchOfficers(sourceCityId, true);
		}

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
			cmd.CommandText = "SELECT name, faction_id, defense_level FROM cities WHERE city_id = $cid";
			cmd.Parameters.AddWithValue("$cid", cityId);
			using (var reader = cmd.ExecuteReader())
			{
				if (reader.Read())
				{
					CurrentContext.CityName = reader.GetString(0);
					if (!reader.IsDBNull(1)) CurrentContext.OwnerFactionId = reader.GetInt32(1);
					else CurrentContext.OwnerFactionId = 0; // Neutral

					CurrentContext.CityDefense = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
				}
			}
		}
	}

	private void FetchOfficers(int cityId, bool isAttacker = false)
	{
		if (CurrentContext.AllOfficers == null) CurrentContext.AllOfficers = new List<BattleOfficer>();
		if (CurrentContext.AttackerOfficers == null) CurrentContext.AttackerOfficers = new List<BattleOfficer>();

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			// Get Name, ID, Faction, Stats
			cmd.CommandText = @"
				SELECT officer_id, name, faction_id, leadership, intelligence, strength, charisma, is_player, rank, troops
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
						Intelligence = reader.GetInt32(4),
						Strength = reader.GetInt32(5),
						Charisma = reader.IsDBNull(6) ? 50 : reader.GetInt32(6),
						IsPlayer = reader.GetBoolean(7),
						Rank = reader.GetString(8),
						Troops = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
					};
					bo.MaxTroops = GetMaxTroops(bo.Rank);
					CurrentContext.AllOfficers.Add(bo);

					if (isAttacker)
					{
						CurrentContext.AttackerOfficers.Add(bo);
					}
				}
			}
		}


		// Fetch Remote Attackers (Officers of the attacking faction in ADJACENT cities)
		if (CurrentContext.AttackerFactionId > 0)
		{
			using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
			{
				conn.Open();
				var remoteCmd = conn.CreateCommand();
				remoteCmd.CommandText = @"
                    SELECT o.officer_id, o.name, o.faction_id, o.leadership, o.intelligence, o.strength, o.charisma, o.is_player, o.rank, o.troops
                    FROM officers o
                    JOIN routes r ON (o.location_id = r.start_city_id AND r.end_city_id = $cid) 
                                 OR (o.location_id = r.end_city_id AND r.start_city_id = $cid)
					WHERE o.faction_id = $afid AND o.location_id != $cid";
				remoteCmd.Parameters.AddWithValue("$cid", cityId);
				remoteCmd.Parameters.AddWithValue("$afid", CurrentContext.AttackerFactionId);

				using (var reader = remoteCmd.ExecuteReader())
				{
					while (reader.Read())
					{
						var bo = new BattleOfficer
						{
							OfficerId = reader.GetInt32(0),
							Name = reader.GetString(1),
							FactionId = reader.GetInt32(2),
							Leadership = reader.GetInt32(3),
							Intelligence = reader.GetInt32(4),
							Strength = reader.GetInt32(5),
							Charisma = reader.IsDBNull(6) ? 50 : reader.GetInt32(6),
							IsPlayer = reader.GetBoolean(7),
							Rank = reader.GetString(8),
							Troops = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
						};
						bo.MaxTroops = GetMaxTroops(bo.Rank);

						// Avoid duplicates if they are somehow already in list
						if (!CurrentContext.AllOfficers.Any(x => x.OfficerId == bo.OfficerId))
							CurrentContext.AllOfficers.Add(bo);
					}
				}
			}
		}

		// Generate Militia if Neutral
		if (CurrentContext.OwnerFactionId == 0)
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
					Intelligence = 10,
					Strength = 40,
					Charisma = 10,
					IsPlayer = false,
					Rank = "Minion",
					Troops = 500,
					MaxTroops = 500
				};
				CurrentContext.AllOfficers.Add(militia);
			}
		}
	}

	public int GetMaxTroops(string rank)
	{
		return GameConstants.GetMaxTroops(rank);
	}

	public int GetRankLevel(string rank)
	{
		switch (rank)
		{
			case "Sovereign": return 5;
			case "Commander": return 4;
			case "General": return 3;
			case "Lieutenant": return 2;
			case "Regular": return 1;
			case "Volunteer": return 0;
			default: return 0;
		}
	}

	private void DetermineSides()
	{
		CurrentContext.AttackerOfficers = new List<BattleOfficer>();
		CurrentContext.DefenderOfficers = new List<BattleOfficer>();

		int defId = CurrentContext.OwnerFactionId;
		CurrentContext.DefenderFactionId = defId;

		// 1. Separate Known Faction Members
		var ronin = new List<BattleOfficer>();
		foreach (var off in CurrentContext.AllOfficers)
		{
			if (off.FactionId == defId && defId > 0)
			{
				CurrentContext.DefenderOfficers.Add(off);
			}
			else if (off.FactionId > 0 || off.IsPlayer) // Faction members (not defenders) or Player
			{
				CurrentContext.AttackerOfficers.Add(off);
			}
			else
			{
				ronin.Add(off);
			}
		}

		// 2. Handle Ronin Participation (Rare/Relationship based)
		foreach (var r in ronin)
		{
			// Check relation with Defenders
			int defenderCount = CurrentContext.DefenderOfficers.Count;
			if (defenderCount > 0)
			{
				int totalRel = 0;
				foreach (var def in CurrentContext.DefenderOfficers)
				{
					totalRel += RelationshipManager.Instance.GetRelation(r.OfficerId, def.OfficerId);
				}
				int avgRel = totalRel / defenderCount;

				if (avgRel > 45)
				{
					GD.Print($"[Battle] Ronin {r.Name} joins DEFENDERS due to friendship (Avg Rel: {avgRel})");
					CurrentContext.DefenderOfficers.Add(r);
				}
				else if (avgRel < -45)
				{
					GD.Print($"[Battle] Ronin {r.Name} joins ATTACKERS due to grudge (Avg Rel: {avgRel})");
					CurrentContext.AttackerOfficers.Add(r);
				}
				else
				{
					GD.Print($"[Battle] Ronin {r.Name} stays neutral.");
				}
			}
			else if (defId == 0) // Neutral Town - Ronin here are basically defenders/residents
			{
				// If it's a neutral town, Ronin in the town ARE the defenders (acting as militia leaders)
				CurrentContext.DefenderOfficers.Add(r);
			}
		}

		// 3. Determine Primary Attacker Faction
		var primaryAttacker = CurrentContext.AttackerOfficers.FirstOrDefault(o => o.FactionId > 0);
		if (primaryAttacker != null)
		{
			CurrentContext.AttackerFactionId = primaryAttacker.FactionId;
		}
		else if (CurrentContext.AttackerOfficers.Count > 0)
		{
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
				var commander = CurrentContext.DefenderOfficers.OrderByDescending(x => x.Leadership).FirstOrDefault();
				if (commander != null)
				{
					obj.Description = $"Defeat the Enemy Commander {commander.Name} to seize control.";
					obj.TargetId = commander.OfficerId;
				}
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
		// FIX: If the winner is different from the original owner, they capture it.
		// This handles Capture (Attacker Win) and Squatter Takeover (Squatter Defending vs Invader Win -> Squatter keeps it? No, if squatter was defending, they are DefenderFactionId.
		// Wait, if Squatter was forced to be Attacker (vs Militia), and Won, then winnerFaction != Owner (0). Correct.
		if (winnerFaction != CurrentContext.OwnerFactionId && winnerFaction != -1) // -1 is Player/Indy, maybe treat as 0 or logic for Player Faction? Assuming Player has FactionId if they are ruling.
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

				// DYNAMIC RELATIONSHIP: Residents/Defenders lose faith in their faction for losing the city
				int oldFactionId = CurrentContext.OwnerFactionId;
				if (oldFactionId > 0)
				{
					UpdateCityFactionOpinionDirect(conn, CurrentContext.LocationId, oldFactionId, -15);
				}
			}
			GD.Print($"City {CurrentContext.CityName} is now owned by Faction {winnerFaction}!");

			// Handle Retreats / Defeat
			HandlePostBattleConsequences(loserFaction, CurrentContext.LocationId, CurrentContext.AttackerFactionId);
		}

		// Award Victory Gold (Prize Pool)
		var winners = attackersWon ? CurrentContext.AttackerOfficers : CurrentContext.DefenderOfficers;
		var losers = attackersWon ? CurrentContext.DefenderOfficers : CurrentContext.AttackerOfficers;
		var realWinners = winners.Where(o => o.OfficerId > 0).ToList();
		var realLosers = losers.Where(o => o.OfficerId > 0).ToList();

		int prizePool = 0;

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			using (var trans = conn.BeginTransaction())
			{
				// 1. Defeat Logic (Losers)
				bool defeatedCommander = false;

				foreach (var loser in realLosers)
				{
					if (loser.Rank == "Sovereign" || loser.Rank == "Commander" || loser.Rank == "General") defeatedCommander = true;

					// Track Loss
					var loseCmd = conn.CreateCommand();
					loseCmd.CommandText = "UPDATE officers SET battles_lost = battles_lost + 1 WHERE officer_id = $oid";
					loseCmd.Parameters.AddWithValue("$oid", loser.OfficerId);
					loseCmd.ExecuteNonQuery();

					// Loot Gold (10%)
					var checkCmd = conn.CreateCommand();
					checkCmd.CommandText = "SELECT gold FROM officers WHERE officer_id = $oid";
					checkCmd.Parameters.AddWithValue("$oid", loser.OfficerId);
					int gold = Convert.ToInt32(checkCmd.ExecuteScalar());

					if (gold > 0)
					{
						int lostAmt = gold / 10;
						if (lostAmt > 0)
						{
							prizePool += lostAmt;
							var dedCmd = conn.CreateCommand();
							dedCmd.CommandText = "UPDATE officers SET gold = gold - $amt WHERE officer_id = $oid";
							dedCmd.Parameters.AddWithValue("$amt", lostAmt);
							dedCmd.Parameters.AddWithValue("$oid", loser.OfficerId);
							dedCmd.ExecuteNonQuery();
							GD.Print($"{loser.Name} lost {lostAmt}g (Looted).");
						}
					}

					// LITERAL LOSS: Update to actual remaining troops
					var attrCmd = conn.CreateCommand();
					attrCmd.CommandText = "UPDATE officers SET troops = $t WHERE officer_id = $oid";
					attrCmd.Parameters.AddWithValue("$t", Math.Max(0, loser.Troops));
					attrCmd.Parameters.AddWithValue("$oid", loser.OfficerId);
					attrCmd.ExecuteNonQuery();
				}

				// 2. Victory Logic (Winners)
				int repGain = 50 + (defeatedCommander ? 100 : 0);

				foreach (var w in realWinners)
				{
					// Track Win & Rep
					var winCmd = conn.CreateCommand();
					winCmd.CommandText = "UPDATE officers SET battles_won = battles_won + 1, reputation = reputation + $rep WHERE officer_id = $oid";
					winCmd.Parameters.AddWithValue("$oid", w.OfficerId);
					winCmd.Parameters.AddWithValue("$rep", repGain);
					winCmd.ExecuteNonQuery();

					// LITERAL LOSS: Update to actual remaining troops
					var attrCmd = conn.CreateCommand();
					attrCmd.CommandText = "UPDATE officers SET troops = $t WHERE officer_id = $oid";
					attrCmd.Parameters.AddWithValue("$t", Math.Max(0, w.Troops));
					attrCmd.Parameters.AddWithValue("$oid", w.OfficerId);
					attrCmd.ExecuteNonQuery();
				}

				trans.Commit();
				GD.Print($"Battle Rep Awarded: {repGain} (Commander Bonus: {defeatedCommander})");

				// ADVANCE ON WIN: If attackers won, move them into the city
				if (attackersWon)
				{
					int sourceCityId = CurrentContext.SourceCityId;
					int leaderId = CurrentContext.LeaderId;
					int targetCityId = CurrentContext.LocationId;

					// 1. Fetch source city governor
					int sourceGovernorId = -1;
					var govCmd = conn.CreateCommand();
					govCmd.CommandText = "SELECT governor_id FROM cities WHERE city_id = $cid";
					govCmd.Parameters.AddWithValue("$cid", sourceCityId);
					var sourceGovRes = govCmd.ExecuteScalar();
					if (sourceGovRes != null && sourceGovRes != DBNull.Value) sourceGovernorId = Convert.ToInt32(sourceGovRes);

					// 2. Identify Faction Leader
					int factionLeaderId = -1;
					var leaderCmd = conn.CreateCommand();
					leaderCmd.CommandText = "SELECT leader_id FROM factions WHERE faction_id = $fid";
					leaderCmd.Parameters.AddWithValue("$fid", winnerFaction);
					var fLeaderRes = leaderCmd.ExecuteScalar();
					if (fLeaderRes != null && fLeaderRes != DBNull.Value) factionLeaderId = Convert.ToInt32(fLeaderRes);

					// 3. Appoint New Governor for captured city
					// Priority: Highest rank among winners who isn't already the Faction Leader or the Source Governor.
					// Actually, the user says "appoints someone from the attack force". 
					// We'll pick the highest rank (Rank > Charisma)
					var potentialNewGovs = realWinners
						.OrderByDescending(o => o.OfficerId == factionLeaderId ? 10 : 0) // Prefer Leader? No, User says Leader appoints someone.
						.OrderByDescending(o => GetRankLevel(o.Rank))
						.ThenByDescending(o => o.Charisma)
						.ToList();

					int newGovernorId = -1;
					if (potentialNewGovs.Any())
					{
						// If Faction Leader is in the group, they are the boss, but we need a "Governor" for the town.
						// User: "Leader appoints someone... should they win".
						// We'll pick the best non-leader if possible, else the leader.
						var bestNonLeader = potentialNewGovs.FirstOrDefault(o => o.OfficerId != factionLeaderId);
						newGovernorId = (bestNonLeader != null) ? bestNonLeader.OfficerId : factionLeaderId;
					}

					if (newGovernorId != -1)
					{
						ExecuteSql(conn, $"UPDATE cities SET governor_id = {newGovernorId} WHERE city_id = {targetCityId}");
						GD.Print($"[Governor] {potentialNewGovs.First(o => o.OfficerId == newGovernorId).Name} appointed as Governor of {CurrentContext.CityName}.");
					}

					// 4. Movement Logic
					foreach (var attacker in realWinners)
					{
						bool isFactionLeader = attacker.OfficerId == factionLeaderId;
						bool isSourceGovernor = attacker.OfficerId == sourceGovernorId;
						bool isNewGovernor = attacker.OfficerId == newGovernorId;

						// Rules:
						// - Source Governor MUST stay behind to protect the source city (even if leader).
						// - New Governor must move to their new city.
						// - Others move by default to occupy.

						bool shouldMove = true;

						if (isSourceGovernor)
						{
							shouldMove = false; // Governor stays behind
						}

						// Exception: If the New Governor was also the Source Governor (weird but possible), they move.
						if (isNewGovernor) shouldMove = true;

						if (shouldMove)
						{
							ExecuteSql(conn, $"UPDATE officers SET location_id = {targetCityId} WHERE officer_id = {attacker.OfficerId}");
							GD.Print($"{attacker.Name} advances into {CurrentContext.CityName}.");
						}
						else
						{
							GD.Print($"{attacker.Name} (Governor) remains in the source city to maintain order.");
						}
					}
				}
			}
		}

		if (realWinners.Count > 0 && prizePool > 0)
		{
			int share = prizePool / realWinners.Count;
			foreach (var winner in realWinners)
			{
				AwardGold(winner.OfficerId, share);
			}
			GD.Print($"Victory Prize! Looted {prizePool}g. {realWinners.Count} officers split ({share}g each).");
		}
		else if (realWinners.Count > 0)
		{
			GD.Print("Victory! But the enemy had no gold to loot.");
		}

		// Clean up
		int finishedCityId = CurrentContext?.LocationId ?? -1;
		CurrentContext = null;

		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		turnMgr?.ResumeConflictResolution(finishedCityId);
	}

	// AI Autoresolve
	public void SimulateBattle(int primaryAttackerId, int cityId)
	{
		GD.Print($"Simulating AI Battle @ City {cityId}...");

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();

			// 1. Identify Attacker Faction
			var facCmd = conn.CreateCommand();
			facCmd.CommandText = "SELECT faction_id FROM officers WHERE officer_id = $oid";
			facCmd.Parameters.AddWithValue("$oid", primaryAttackerId);
			var facRes = facCmd.ExecuteScalar();
			int attackerFaction = (facRes != null && facRes != DBNull.Value) ? Convert.ToInt32(facRes) : 0;

			var cityFacCmd = conn.CreateCommand();
			cityFacCmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
			cityFacCmd.Parameters.AddWithValue("$cid", cityId);
			var cityFacRes = cityFacCmd.ExecuteScalar();
			int defenderFaction = (cityFacRes != null && cityFacRes != DBNull.Value) ? Convert.ToInt32(cityFacRes) : 0;

			if (attackerFaction == defenderFaction && attackerFaction > 0) return;

			// 2. Gather Participants and Strengths
			// Attackers: Primary + Any others of same faction in adjacent cities
			var attackers = new List<(int id, string name, int str, int troops)>();
			var attListCmd = conn.CreateCommand();
			attListCmd.CommandText = @"
				SELECT DISTINCT o.officer_id, o.name, o.strength, o.troops 
				FROM officers o
				LEFT JOIN routes r ON (o.location_id = r.start_city_id AND r.end_city_id = $cid) 
				                   OR (o.location_id = r.end_city_id AND r.start_city_id = $cid)
				WHERE (o.officer_id = $pid) OR (o.faction_id = $fid AND (o.location_id = $cid OR r.route_id IS NOT NULL))";
			attListCmd.Parameters.AddWithValue("$cid", cityId);
			attListCmd.Parameters.AddWithValue("$fid", attackerFaction);
			attListCmd.Parameters.AddWithValue("$pid", primaryAttackerId);

			using (var r = attListCmd.ExecuteReader())
			{
				while (r.Read()) attackers.Add((Convert.ToInt32(r.GetValue(0)), r.GetString(1), Convert.ToInt32(r.GetValue(2)), Convert.ToInt32(r.GetValue(3))));
			}

			// Defenders: All officers in the city (if defenderFaction > 0) or Militia (if 0)
			var defenders = new List<(int id, string name, int str, int troops)>();
			if (defenderFaction > 0)
			{
				var defListCmd = conn.CreateCommand();
				defListCmd.CommandText = "SELECT officer_id, name, strength, troops FROM officers WHERE location_id = $cid AND faction_id = $fid";
				defListCmd.Parameters.AddWithValue("$cid", cityId);
				defListCmd.Parameters.AddWithValue("$fid", defenderFaction);
				using (var r = defListCmd.ExecuteReader())
				{
					while (r.Read()) defenders.Add((Convert.ToInt32(r.GetValue(0)), r.GetString(1), Convert.ToInt32(r.GetValue(2)), Convert.ToInt32(r.GetValue(3))));
				}
			}

			long totalAttStr = attackers.Sum(a => a.str);
			long totalDefStr = defenders.Sum(d => d.str) + 20; // 20 for City Walls/Militia Base

			if (defenders.Count == 0 && defenderFaction == 0) totalDefStr = 40; // Neutral Town Militia Strength

			// 3. Resolve Winner
			int roll = new Random().Next(-20, 20);
			bool attackerWins = (totalAttStr + roll) > totalDefStr;

			GD.Print($"Auto-Resolve: Attackers({totalAttStr}) vs Defenders({totalDefStr}) -> {(attackerWins ? "Win" : "Loss")}");

			// 4. Apply Persistent Damage (Literal)
			// Winner loses 10-30%, Loser loses 70-90%
			float winnerLossMult = 0.1f + (float)new Random().NextDouble() * 0.2f;
			float loserLossMult = 0.7f + (float)new Random().NextDouble() * 0.2f;

			foreach (var att in attackers)
			{
				int newTroops = (int)(att.troops * (attackerWins ? (1.0f - winnerLossMult) : (1.0f - loserLossMult)));
				ExecuteSql(conn, $"UPDATE officers SET troops = {newTroops} WHERE officer_id = {att.id}");
			}

			foreach (var def in defenders)
			{
				int newTroops = (int)(def.troops * (attackerWins ? (1.0f - loserLossMult) : (1.0f - winnerLossMult)));
				ExecuteSql(conn, $"UPDATE officers SET troops = {newTroops} WHERE officer_id = {def.id}");
			}

			// 5. Cleanup / Capture
			if (attackerWins)
			{
				ExecuteSql(conn, $"UPDATE cities SET faction_id = {(attackerFaction > 0 ? attackerFaction.ToString() : "NULL")}, is_hq = 0 WHERE city_id = {cityId}");
				if (defenderFaction > 0) UpdateCityFactionOpinionDirect(conn, cityId, defenderFaction, -15);

				foreach (var att in attackers) ExecuteSql(conn, $"UPDATE officers SET location_id = {cityId} WHERE officer_id = {att.id}");
				GD.Print($"City {cityId} Captured by Faction {attackerFaction}!");
				HandlePostBattleConsequences(defenderFaction, cityId, attackerFaction);
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

	private void HandlePostBattleConsequences(int defeatedFactionId, int lostCityId, int winnerFactionId)
	{
		if (defeatedFactionId <= 0) return; // Neutral/Indy has no retreat logic

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();

			// 1. Check if Faction has any cities left
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid LIMIT 1";
			checkCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
			var res = checkCmd.ExecuteScalar();

			int retreatCityId = (res != null) ? Convert.ToInt32(res) : -1;

			if (retreatCityId != -1)
			{
				// RETREAT: Move all officers of this faction in the lost city to the new location
				var moveCmd = conn.CreateCommand();
				moveCmd.CommandText = @"
					UPDATE officers 
					SET location_id = $target 
					WHERE location_id = $lost AND faction_id = $fid";
				moveCmd.Parameters.AddWithValue("$target", retreatCityId);
				moveCmd.Parameters.AddWithValue("$lost", lostCityId);
				moveCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
				int moved = moveCmd.ExecuteNonQuery();
				GD.Print($"Faction {defeatedFactionId} retreats! {moved} officers moved to City {retreatCityId}.");
			}
			else
			{
				// DEFEAT: Faction destroyed!
				// 1. Unassign all officers (Ronin)
				var freeCmd = conn.CreateCommand();
				freeCmd.CommandText = "UPDATE officers SET faction_id = NULL, rank = 'Free' WHERE faction_id = $fid";
				freeCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
				int freed = freeCmd.ExecuteNonQuery();

				// 2. Cleanup Dependencies (Foreign Keys)
				// Faction Relations
				var delRelCmd = conn.CreateCommand();
				delRelCmd.CommandText = "DELETE FROM faction_relations WHERE source_faction_id = $fid OR target_faction_id = $fid";
				delRelCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
				delRelCmd.ExecuteNonQuery();

				// Officer Faction Relations (Manual Table)
				try
				{
					var delOffFacCmd = conn.CreateCommand();
					delOffFacCmd.CommandText = "DELETE FROM officer_faction_relations WHERE faction_id = $fid";
					delOffFacCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
					delOffFacCmd.ExecuteNonQuery();
				}
				catch { } // Ignore if table missing

				// Pending Battles
				try
				{
					var delPenCmd = conn.CreateCommand();
					delPenCmd.CommandText = "DELETE FROM pending_battles WHERE attacker_faction_id = $fid";
					delPenCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
					delPenCmd.ExecuteNonQuery();
				}
				catch { }

				// 3. Delete Faction Record (Optional, or mark inactive)
				var delCmd = conn.CreateCommand();
				delCmd.CommandText = "DELETE FROM factions WHERE faction_id = $fid";
				delCmd.Parameters.AddWithValue("$fid", defeatedFactionId);
				delCmd.ExecuteNonQuery();

				GD.Print($"Faction {defeatedFactionId} has been ELIMINATED! {freed} officers are now Ronin.");

				// PLAYER AP REWARD: If player's faction defeated this faction, +1 Max AP (Cap 5)
				AwardPlayerDefeatBonus(winnerFactionId);
			}
		}
	}
	private void AwardPlayerDefeatBonus(int winnerFactionId)
	{
		if (winnerFactionId <= 0) return;

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			// 1. Get player's faction
			var pCmd = conn.CreateCommand();
			pCmd.CommandText = "SELECT faction_id, max_action_points FROM officers WHERE is_player = 1";
			int pFaction = -1;
			int pMaxAP = 0;
			using (var r = pCmd.ExecuteReader())
			{
				if (r.Read())
				{
					pFaction = r.IsDBNull(0) ? -2 : r.GetInt32(0);
					pMaxAP = r.GetInt32(1);
				}
			}

			if (pFaction == winnerFactionId && pMaxAP < 5)
			{
				ExecuteSql(conn, "UPDATE officers SET max_action_points = MIN(5, max_action_points + 1) WHERE is_player = 1");
				GD.Print("[BattleManager] Player's faction eliminated an enemy! +1 Max Action Point awarded.");
			}
		}
	}
	public void AwardGold(int officerId, int amount)
	{
		if (officerId <= 0) return; // Only real officers/player

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET gold = gold + $amt WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$amt", amount);
			cmd.Parameters.AddWithValue("$oid", officerId);
			cmd.ExecuteNonQuery();
		}
		GD.Print($"Officer {officerId} earned {amount} Gold!");
	}

	private void UpdateCityFactionOpinionDirect(SqliteConnection conn, int cityId, int factionId, int delta)
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

	// --- Retreat / Surrender WIP ---

	public void HandleRetreat(int factionId, int cityId)
	{
		GD.Print($"Faction {factionId} is RETREATING from City {cityId}!");
		// Logic to find nearest city and move all units there
		// This will be triggered from BattleController or AI
		HandlePostBattleConsequences(factionId, cityId, -1); // -1 means they retreated themselves
		ResolveBattle(factionId != CurrentContext.AttackerFactionId); // Loser is the one retreating
	}

	public void HandleSurrender(int factionId, int cityId, int winnerFactionId)
	{
		GD.Print($"Faction {factionId} SURRENDERS to Faction {winnerFactionId}!");
		// Soft defeat: absorbed into the winning faction
		// TODO: Implement absorption logic (change all officer faction_ids)
		HandlePostBattleConsequences(factionId, cityId, winnerFactionId);
	}
}

// Data Structures
public class BattleContext
{
	public int LocationId;
	public int SourceCityId; // Where the attackers came from
	public string CityName;
	public int CityDefense; // New Field
	public int OwnerFactionId;

	public int AttackerFactionId;
	public int DefenderFactionId;
	public int LeaderId; // Who initiated the attack

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
	public int Intelligence;
	public int Strength;
	public int Charisma;
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
