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

			// DB Migration: Ensure formation_type column exists
			var checkCmd = conn.CreateCommand();
			checkCmd.CommandText = "PRAGMA table_info(officers)";
			bool hasFormation = false;
			bool hasMainTroopType = false;
			bool hasOfficerType = false;
			using (var r = checkCmd.ExecuteReader())
			{
				while (r.Read())
				{
					string col = r["name"].ToString();
					if (col == "formation_type") hasFormation = true;
					if (col == "main_troop_type") hasMainTroopType = true;
					if (col == "officer_type") hasOfficerType = true;
				}
			}

			if (!hasFormation)
			{
				var alterCmd = conn.CreateCommand();
				alterCmd.CommandText = "ALTER TABLE officers ADD COLUMN formation_type INTEGER DEFAULT 0";
				alterCmd.ExecuteNonQuery();
				GD.Print("[BattleManager] Migrated DB: Added formation_type column.");
			}

			if (!hasMainTroopType)
			{
				var alterCmd = conn.CreateCommand();
				alterCmd.CommandText = "ALTER TABLE officers ADD COLUMN main_troop_type INTEGER DEFAULT 0";
				alterCmd.ExecuteNonQuery();
				GD.Print("[BattleManager] Migrated DB: Added main_troop_type column.");
			}

			if (!hasOfficerType)
			{
				var alterCmd = conn.CreateCommand();
				alterCmd.CommandText = "ALTER TABLE officers ADD COLUMN officer_type INTEGER DEFAULT 0";
				alterCmd.ExecuteNonQuery();
				GD.Print("[BattleManager] Migrated DB: Added officer_type column.");
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

		// Fetch LeaderId and AttackerFactionId from pending_battles if possible
		if (sourceCityId > 0)
		{
			using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
			{
				conn.Open();
				var cmd = conn.CreateCommand();
				cmd.CommandText = "SELECT leader_id, attacker_faction_id FROM pending_battles WHERE location_id = $lid AND source_location_id = $sid";
				cmd.Parameters.AddWithValue("$lid", cityId);
				cmd.Parameters.AddWithValue("$sid", sourceCityId);
				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						CurrentContext.LeaderId = reader.GetInt32(0);
						CurrentContext.AttackerFactionId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
						GD.Print($"[BattleManager] Attack Identified: Leader={CurrentContext.LeaderId}, Faction={CurrentContext.AttackerFactionId}");
					}
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
			SELECT officer_id, name, faction_id, leadership, intelligence, strength, charisma, is_player, rank, troops, formation_type, politics, main_troop_type, officer_type
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
						Politics = reader.IsDBNull(11) ? 50 : reader.GetInt32(11),
						IsPlayer = reader.GetBoolean(7),
						Rank = reader.GetString(8),
						Troops = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
					};
					bo.MaxTroops = GetMaxTroops(bo.Rank);

					// Load types from DB if they exist (non-zero)
					int mtt = reader.IsDBNull(12) ? 0 : reader.GetInt32(12);
					int oct = reader.IsDBNull(13) ? 0 : reader.GetInt32(13);

					if (mtt > 0) bo.MainTroopType = (TroopType)(mtt - 1); // We store 1-indexed to avoid 0/NULL ambiguity
					if (oct > 0) bo.OfficerType = (TroopType)(oct - 1);

					if (mtt == 0 || oct == 0)
					{
						AssignIntelligentTroopType(bo);
					}

					int fType = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
					bo.Formation = (FormationShape)fType;

					// Initialize Extended Stats
					bo.MaxOfficerHP = bo.Strength + bo.Leadership;
					if (bo.OfficerId == 1) bo.MaxOfficerHP *= 2; // Buff player? Or just keep scaling
					bo.OfficerHP = bo.MaxOfficerHP;
					bo.OfficerArmor = bo.Intelligence / 2;
					bo.TroopArmor = bo.Leadership / 5;

					// Determine Combat Position
					if (bo.Strength > 70 || bo.MainTroopType == TroopType.Infantry) bo.CombatPosition = "Front";
					else if (bo.Intelligence > 70 || bo.MainTroopType == TroopType.Archer) bo.CombatPosition = "Rear";
					else bo.CombatPosition = "Middle";

					// PREVENT DUPLICATES (If officer was already added by a previous fetch, e.g. Remote Logic)
					if (!CurrentContext.AllOfficers.Any(x => x.OfficerId == bo.OfficerId))
					{
						CurrentContext.AllOfficers.Add(bo);
						// Only add to specific side lists if not already handled by DetermineSides later
						// Actually DetermineSides clears and rebuilds lists, so we just need uniqueness in AllOfficers.
						// BUT wait, DetermineSides uses AllOfficers. 

						// The old logic added to AttackerOfficers directly here if isAttacker was true.
						// We should probably rely on DetermineSides to sort them out based on FactionId.
						// However, to keep existing logic safe, we can leave the specific add, but guarded.
					}

					// NOTE: We don't need to manually add to AttackerOfficers/DefenderOfficers here 
                    // because DetermineSides() is called at the end of CreateContext() which sorts everyone.
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
                    SELECT o.officer_id, o.name, o.faction_id, o.leadership, o.intelligence, o.strength, o.charisma, o.is_player, o.rank, o.troops, o.politics, o.main_troop_type, o.officer_type
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
                            Politics = reader.IsDBNull(10) ? 50 : reader.GetInt32(10),
                            IsPlayer = reader.GetBoolean(7),
                            Rank = reader.GetString(8),
                            Troops = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
                        };
                        bo.MaxTroops = GetMaxTroops(bo.Rank);

                        // Load types from DB if they exist
                        int mtt = reader.IsDBNull(11) ? 0 : reader.GetInt32(11); // politics is 10
                        int oct = reader.IsDBNull(12) ? 0 : reader.GetInt32(12);
                        if (mtt > 0) bo.MainTroopType = (TroopType)(mtt - 1);
                        if (oct > 0) bo.OfficerType = (TroopType)(oct - 1);
                        if (mtt == 0 || oct == 0) AssignIntelligentTroopType(bo);

                        // Initialize Extended Stats
                        bo.MaxOfficerHP = bo.Strength + bo.Leadership;
                        bo.OfficerHP = bo.MaxOfficerHP;
                        bo.OfficerArmor = bo.Intelligence / 2;
                        bo.TroopArmor = bo.Leadership / 5;

                        if (bo.Strength > 70) bo.CombatPosition = "Front";
                        else if (bo.Intelligence > 70) bo.CombatPosition = "Rear";
                        else bo.CombatPosition = "Middle";

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
                    Politics = 10,
                    IsPlayer = false,
                    Rank = "Minion",
                    Troops = 500,
                    MaxTroops = 500,
                    MainTroopType = TroopType.Infantry,
                    Formation = FormationShape.Vanguard,
                    MaxOfficerHP = 70,
                    OfficerHP = 70,
                    OfficerArmor = 5,
                    TroopArmor = 2,
                    CombatPosition = "Front"
                };
                CurrentContext.AllOfficers.Add(militia);
            }
        }
    }

    private static readonly Random _battleRng = new Random();

    private void AssignIntelligentTroopType(BattleOfficer bo)
    {
        // Weights for RPS units (Infantry, Archer, Cavalry)
        int infWeight = 40;
        int arcWeight = 25;
        int cavWeight = 25;
        int siegeWeight = 5;
        int eliteWeight = 5;

        // Stat modifiers: High stats make an officer MORE LIKELY to be given that unit
		// but it's not guaranteed. This maintains the RPS dynamic across the army.
		if (bo.Intelligence > 70) arcWeight += 50;
		if (bo.Strength > 70) cavWeight += 50;
		if (bo.Leadership > 80) eliteWeight += 30;
		if (bo.Intelligence > 50 && bo.Politics > 50) siegeWeight += 20;

		int totalWeight = infWeight + arcWeight + cavWeight + siegeWeight + eliteWeight;
		int roll = _battleRng.Next(totalWeight);

		if (roll < infWeight) bo.MainTroopType = TroopType.Infantry;
		else if (roll < infWeight + arcWeight) bo.MainTroopType = TroopType.Archer;
		else if (roll < infWeight + arcWeight + cavWeight) bo.MainTroopType = TroopType.Cavalry;
		else if (roll < infWeight + arcWeight + cavWeight + siegeWeight) bo.MainTroopType = TroopType.Siege;
		else bo.MainTroopType = TroopType.Elite;

		// Officer Preference: Can be different from troops!
		if (bo.Intelligence > bo.Strength + 20) bo.OfficerType = TroopType.Archer;
		else if (bo.Strength > bo.Intelligence + 20) bo.OfficerType = TroopType.Cavalry;
		else bo.OfficerType = TroopType.Infantry;
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

		int attId = CurrentContext.AttackerFactionId;
		int defId = CurrentContext.OwnerFactionId;
		CurrentContext.DefenderFactionId = defId;

		// 1. Separate Known Faction Members
		var ronin = new List<BattleOfficer>();
		var otherFactions = new List<BattleOfficer>();

		foreach (var off in CurrentContext.AllOfficers)
		{
			if (off.FactionId == defId && defId > 0)
			{
				// Step 5: Any faction defending its city will assign all officers with any troops
				if (off.Troops > 0)
					CurrentContext.DefenderOfficers.Add(off);
			}
			else if (off.FactionId == attId && attId > 0)
			{
				// Step 4: All Assigned Officers are present at the Attack From City - They all Join
				if (off.Troops > 0)
					CurrentContext.AttackerOfficers.Add(off);
			}
			else if (off.FactionId > 0)
			{
				otherFactions.Add(off);
			}
			else if (off.IsPlayer)
			{
				// Player handling (already set in context or needs logic?)
				// Default: If player is in either group, they are already sorted. 
				// If player is independent, they go to Ronin logic.
				if (off.FactionId == defId) CurrentContext.DefenderOfficers.Add(off);
				else if (off.FactionId == attId) CurrentContext.AttackerOfficers.Add(off);
				else ronin.Add(off);
			}
			else
			{
				ronin.Add(off);
			}
		}

		// 2. Handle Ronin Participation (Step 6) - Make it very rare!
		var rng = new Random();
		foreach (var r in ronin)
		{
			// user says "very rare". 5% chance to even consider joining.
			// EXCEPTION: Player is never skipped or auto-assigned by this logic. They choose in UI.
			if (r.IsPlayer) continue;

			if (rng.NextDouble() > 0.05)
			{
				GD.Print($"[Battle] Ronin {r.Name} stays neutral (Very Rare Join Check).");
				continue;
			}

			// Check relation with Defenders
			int defenderCount = CurrentContext.DefenderOfficers.Count;
			int attCount = CurrentContext.AttackerOfficers.Count;

			int defRel = (defenderCount > 0) ? CalculateAvgRelation(r.OfficerId, CurrentContext.DefenderOfficers) : 0;
			int attRel = (attCount > 0) ? CalculateAvgRelation(r.OfficerId, CurrentContext.AttackerOfficers) : 0;

			if (defRel > 70 && defRel > attRel)
			{
				GD.Print($"[Battle] Ronin {r.Name} joins DEFENDERS (High Rel: {defRel})");
				CurrentContext.DefenderOfficers.Add(r);
			}
			else if (attRel > 70 && attRel > defRel)
			{
				GD.Print($"[Battle] Ronin {r.Name} joins ATTACKERS (High Rel: {attRel})");
				CurrentContext.AttackerOfficers.Add(r);
			}
		}

		// 3. Handle Other Factions (Mercenaries?) - Similar rare logic
		foreach (var o in otherFactions)
		{
			if (rng.NextDouble() > 0.05) continue;

			int defRel = CalculateAvgRelation(o.OfficerId, CurrentContext.DefenderOfficers);
			int attRel = CalculateAvgRelation(o.OfficerId, CurrentContext.AttackerOfficers);

			if (defRel > 75 && defRel > attRel) CurrentContext.DefenderOfficers.Add(o);
			else if (attRel > 75 && attRel > defRel) CurrentContext.AttackerOfficers.Add(o);
		}

		// Ensure primary attacker faction is set for context if not already
		if (CurrentContext.AttackerFactionId <= 0 && CurrentContext.AttackerOfficers.Count > 0)
		{
			CurrentContext.AttackerFactionId = CurrentContext.AttackerOfficers[0].FactionId;
		}
	}

	private int CalculateAvgRelation(int officerId, List<BattleOfficer> group)
	{
		if (group.Count == 0) return 0;
		int total = 0;
		foreach (var member in group)
		{
			total += RelationshipManager.Instance.GetRelation(officerId, member.OfficerId);
		}
		return total / group.Count;
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

			ActionManager.Instance.EmitSignal(ActionManager.SignalName.MapStateChanged);

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

					// Promotion Check
					ActionManager.Instance.CheckPromotions(w.OfficerId, conn);
				}

				trans.Commit();
				GD.Print($"Battle Rep Awarded: {repGain} (Commander Bonus: {defeatedCommander})");

				// ADVANCE ON WIN: If attackers won, move them into the city using the unified helper
				if (attackersWon)
				{
					ApplyPostBattleMovement(conn, winnerFaction, CurrentContext.SourceCityId, CurrentContext.LocationId, realWinners);
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

		// Use context if it exists and matches
		if (CurrentContext == null || CurrentContext.LocationId != cityId)
		{
			CreateContext(cityId); // Fallback
		}

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();

			// 1. Participant Lists from Context
			var attackers = CurrentContext.AttackerOfficers;
			var defenders = CurrentContext.DefenderOfficers;

			int attackerFaction = CurrentContext.AttackerFactionId;
			int defenderFaction = CurrentContext.DefenderFactionId;

			if (attackerFaction == defenderFaction && attackerFaction > 0) return;

			// 2. Calculate Strengths
			long totalAttStr = attackers.Sum(a => (long)a.Strength + (a.Troops / 100)); // Strength + Troop weight
			long totalDefStr = defenders.Sum(d => (long)d.Strength + (d.Troops / 100)) + 20; // 20 for City Walls/Militia Base

			if (defenders.Count == 0 && defenderFaction == 0) totalDefStr = 40; // Neutral Town Militia Strength

			// 3. Resolve Winner
			int roll = new Random().Next(-20, 20);
			bool attackerWins = (totalAttStr + roll) > totalDefStr;

			GD.Print($"Auto-Resolve: Attackers({totalAttStr}) vs Defenders({totalDefStr}) -> {(attackerWins ? "Win" : "Loss")}");

			// 4. Apply Persistent Damage & Reputation (Step 8/9/12)
			// Winner loses 10-30%, Loser loses 70-90%
			float winnerLossMult = 0.1f + (float)new Random().NextDouble() * 0.2f;
			float loserLossMult = 0.7f + (float)new Random().NextDouble() * 0.2f;

			foreach (var att in attackers)
			{
				int newTroops = (int)(att.Troops * (attackerWins ? (1.0f - winnerLossMult) : (1.0f - loserLossMult)));
				int repGain = attackerWins ? 50 : 5;
				ExecuteSql(conn, $"UPDATE officers SET troops = {newTroops}, reputation = reputation + {repGain} WHERE officer_id = {att.OfficerId}");
				ActionManager.Instance.CheckPromotions(att.OfficerId, conn);
			}

			foreach (var def in defenders)
			{
				int newTroops = (int)(def.Troops * (!attackerWins ? (1.0f - winnerLossMult) : (1.0f - loserLossMult)));
				int repGain = !attackerWins ? 50 : 5;
				ExecuteSql(conn, $"UPDATE officers SET troops = {newTroops}, reputation = reputation + {repGain} WHERE officer_id = {def.OfficerId}");
				ActionManager.Instance.CheckPromotions(def.OfficerId, conn);
			}

			// 5. Cleanup / Capture (Step 9-10)
			if (attackerWins)
			{
				ExecuteSql(conn, $"UPDATE cities SET faction_id = {(attackerFaction > 0 ? attackerFaction.ToString() : "NULL")}, is_hq = 0 WHERE city_id = {cityId}");
				if (defenderFaction > 0) UpdateCityFactionOpinionDirect(conn, cityId, defenderFaction, -15);

				// Unified Movement Logic for AI Sim
				ApplyPostBattleMovement(conn, attackerFaction, CurrentContext.SourceCityId, cityId, attackers);

				GD.Print($"City {cityId} Captured by Faction {attackerFaction}!");
				ActionManager.Instance.EmitSignal(ActionManager.SignalName.MapStateChanged);
				HandlePostBattleConsequences(defenderFaction, cityId, attackerFaction);
			}

			// Clean up Context if it matches the simulated battle
			if (CurrentContext != null && CurrentContext.LocationId == cityId)
			{
				CurrentContext = null;
			}

			// Resume Turn Cycle
			var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
			turnMgr?.ResumeConflictResolution(cityId);
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

	private void ApplyPostBattleMovement(SqliteConnection conn, int winnerFactionId, int sourceCityId, int targetCityId, List<BattleOfficer> winners)
	{
		if (winnerFactionId <= 0) return;

		// 1. Fetch Source City Governor
		int sourceGovernorId = -1;
		using (var govCmd = conn.CreateCommand())
		{
			govCmd.CommandText = "SELECT governor_id FROM cities WHERE city_id = $cid";
			govCmd.Parameters.AddWithValue("$cid", sourceCityId);
			var res = govCmd.ExecuteScalar();
			if (res != null && res != DBNull.Value) sourceGovernorId = Convert.ToInt32(res);
		}

		// 2. Identify Faction Leader and HQ
		int factionLeaderId = -1;
		int hqCityId = -1;
		using (var leaderCmd = conn.CreateCommand())
		{
			leaderCmd.CommandText = "SELECT leader_id FROM factions WHERE faction_id = $fid";
			leaderCmd.Parameters.AddWithValue("$fid", winnerFactionId);
			var res = leaderCmd.ExecuteScalar();
			if (res != null && res != DBNull.Value) factionLeaderId = Convert.ToInt32(res);
		}

		using (var hqCmd = conn.CreateCommand())
		{
			hqCmd.CommandText = "SELECT city_id FROM cities WHERE faction_id = $fid AND is_hq = 1 LIMIT 1";
			hqCmd.Parameters.AddWithValue("$fid", winnerFactionId);
			var res = hqCmd.ExecuteScalar();
			if (res != null && res != DBNull.Value) hqCityId = Convert.ToInt32(res);
			else hqCityId = sourceCityId; // Fallback
		}

		// 3. Appoint New Governor for captured city
		var potentialNewGovs = winners
			.OrderByDescending(o => o.OfficerId == factionLeaderId ? -10 : 0) // Avoid leader as governor
			.OrderByDescending(o => GetRankLevel(o.Rank))
			.ThenByDescending(o => o.Politics)
			.ToList();

		int newGovernorId = -1;
		if (potentialNewGovs.Any())
		{
			var bestNonLeader = potentialNewGovs.FirstOrDefault(o => o.OfficerId != factionLeaderId);
			newGovernorId = (bestNonLeader != null) ? bestNonLeader.OfficerId : factionLeaderId;
		}

		if (newGovernorId != -1)
		{
			ExecuteSql(conn, $"UPDATE cities SET governor_id = {newGovernorId} WHERE city_id = {targetCityId}");
			var gov = winners.FirstOrDefault(w => w.OfficerId == newGovernorId);
			if (gov != null) GD.Print($"[Governor] {gov.Name} appointed as Governor of City {targetCityId}.");
		}

		// 4. Execute Movement
		foreach (var winner in winners)
		{
			if (winner.OfficerId <= 0) continue;

			bool isFactionLeader = winner.OfficerId == factionLeaderId;
			bool isSourceGovernor = winner.OfficerId == sourceGovernorId;
			bool isNewGovernor = winner.OfficerId == newGovernorId;

			// Scenario Logic:
			// - New Governor MUST stay in the Target City
			// - Source Governor MUST return/stay in Source City
			// - Faction Leader (if not governor) SHOULD return to HQ
			// - Others advance to Target City

			if (isNewGovernor)
			{
				ExecuteSql(conn, $"UPDATE officers SET location_id = {targetCityId} WHERE officer_id = {winner.OfficerId}");
				GD.Print($"{winner.Name} (New Governor) takes post at City {targetCityId}.");
			}
			else if (isSourceGovernor)
			{
				ExecuteSql(conn, $"UPDATE officers SET location_id = {sourceCityId} WHERE officer_id = {winner.OfficerId}");
				GD.Print($"{winner.Name} (Source Governor) returns to City {sourceCityId}.");
			}
			else if (isFactionLeader)
			{
				ExecuteSql(conn, $"UPDATE officers SET location_id = {hqCityId} WHERE officer_id = {winner.OfficerId}");
				GD.Print($"{winner.Name} (Faction Leader) returns to HQ at City {hqCityId}.");
			}
			else
			{
				ExecuteSql(conn, $"UPDATE officers SET location_id = {targetCityId} WHERE officer_id = {winner.OfficerId}");
				GD.Print($"{winner.Name} advances into City {targetCityId}.");
			}
		}
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
		HandlePostBattleConsequences(factionId, cityId, winnerFactionId);
	}

	public string GetFactionName(int factionId)
	{
		if (factionId <= 0) return "Neutral";

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name FROM factions WHERE faction_id = $id";
			cmd.Parameters.AddWithValue("$id", factionId);
			var result = cmd.ExecuteScalar();

			if (result != null && result != DBNull.Value)
			{
				return result.ToString();
			}
		}
		return $"Faction {factionId}";
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
	public int Politics;
	public bool IsPlayer;
	public string Rank;
	public int Troops;
	public int MaxTroops;
	public TroopType MainTroopType;
	public FormationShape Formation;

	// Real-Time Stats
	public int OfficerHP;
	public int MaxOfficerHP;
	public int OfficerArmor;
	public int TroopArmor;
	public string CombatPosition = "Middle"; // Front, Middle, Rear
	public int Morale = 80; // 0-100
	public TroopType OfficerType; // Infantry, Archer, Cavalry
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
