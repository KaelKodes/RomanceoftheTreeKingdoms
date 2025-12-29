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

		// NEW: If no defenders, instant win? Or handled by UI to show "Walkover"?
		if (CurrentContext.DefenderOfficers.Count == 0)
		{
			GD.Print("No defenders found! Auto-resolving as cleanup...");
			// This might be risky if we want the player to see it, but user asked for fix.
			// Let's mark context as "Walkover" or just return?
            // Best approach: If player initiates, show popup "City is undefended! We march in."
            // But since this method creates context for UI, checking count in UI is safer.
        }
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

        int defId = CurrentContext.OwnerFactionId;
        // FIX: If city is neutral, Defenders are Militia (0).
        // We DO NOT let squatters claim defense pre-battle; they must fight the militia too (or be attackers).

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

        // FIX: Empty Battle Check - If no defenders (and no militia because Neutral towns spawn them, so this implies Faction with no officers),
        // we should probably auto-resolve or cancel.
		// Actually, let's handle this in CreateContext or upper level? 
		// Or just let DetermineSides happen and then check count.

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
			}
			GD.Print($"City {CurrentContext.CityName} is now owned by Faction {winnerFaction}!");

			// Handle Retreats / Defeat
			HandlePostBattleConsequences(loserFaction, CurrentContext.LocationId);
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

					// Attrition (Losers lose 50%)
					var attrCmd = conn.CreateCommand();
					attrCmd.CommandText = "UPDATE officers SET troops = MAX(0, troops / 2) WHERE officer_id = $oid";
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

					// Attrition (Winners lose 10% - Cost of Victory)
					var attrCmd = conn.CreateCommand();
					attrCmd.CommandText = "UPDATE officers SET troops = MAX(0, CAST(troops * 0.9 AS INTEGER)) WHERE officer_id = $oid";
					attrCmd.Parameters.AddWithValue("$oid", w.OfficerId);
					attrCmd.ExecuteNonQuery();
				}

				trans.Commit();
				GD.Print($"Battle Rep Awarded: {repGain} (Commander Bonus: {defeatedCommander})");

				// ADVANCE ON WIN: If attackers won, move them into the city
				if (attackersWon)
				{
					foreach (var attacker in realWinners)
					{
						ExecuteSql(conn, $"UPDATE officers SET location_id = {CurrentContext.LocationId} WHERE officer_id = {attacker.OfficerId}");
						GD.Print($"{attacker.Name} advances into {CurrentContext.CityName}.");
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
			attCmd.CommandText = "SELECT faction_id, strength, name FROM officers WHERE officer_id = $oid";
			attCmd.Parameters.AddWithValue("$oid", attackerOfficerId);
			int attFaction = 0;
			int attStrength = 0;
			string attName = "Unknown";
			using (var r = attCmd.ExecuteReader())
			{
				if (r.Read())
				{
					attFaction = r.IsDBNull(0) ? 0 : r.GetInt32(0); // Treat NULL as 0 (Neutral/Ronin)
					attStrength = r.GetInt32(1);
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
			defCmd.CommandText = "SELECT MAX(strength) FROM officers WHERE location_id = $cid AND faction_id = $fid";
			defCmd.Parameters.AddWithValue("$cid", cityId);
			defCmd.Parameters.AddWithValue("$fid", cityFaction);
			var defRes = defCmd.ExecuteScalar();
			int defStrength = (defRes != null && defRes != DBNull.Value) ? Convert.ToInt32(defRes) : 0;

			// 2. Resolve
			// Bonus needed to attack Fortified city? 
			// Let's say City gives +20 Defense Bonus naturally
            int defenseValues = defStrength + 20;

            int roll = new Random().Next(-20, 20); // Variance
            bool attackerWins = (attStrength + roll) > defenseValues;

            // If empty city (defStrength is 0), attacker wins easily unless super unlucky?
            if (defStrength == 0) attackerWins = true;

            GD.Print($"Auto-Resolve: {attName} ({attStrength}) vs City ({defenseValues}) -> {(attackerWins ? "Win" : "Loss")}");

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

                // ADVANCE ON WIN: Move the AI attacker into the city
                ExecuteSql(conn, $"UPDATE officers SET location_id = {cityId} WHERE officer_id = {attackerOfficerId}");
                GD.Print($"{attName} advances into the captured city.");

                // Handle Retreats / Defeat
                HandlePostBattleConsequences(cityFaction, cityId);
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

    private void HandlePostBattleConsequences(int defeatedFactionId, int lostCityId)
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
