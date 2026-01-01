using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class TurnManager : Node
{
	[Signal] public delegate void TurnStartedEventHandler(int factionId, bool isPlayer);
	[Signal] public delegate void CouncilTriggeredEventHandler(int factionId, int cityId, int cpAmount);
	[Signal] public delegate void TurnEndedEventHandler();
	[Signal] public delegate void NewWeekStartedEventHandler();

	public static TurnManager Instance { get; private set; }
	// private string _dbPath; // Removed

	// Turn State
	private List<int> _turnQueue = new List<int>();
	private int _currentTurnIndex = -1;
	private bool _isTurnActive = false;

	public bool IsPlayerTurnActive => _isTurnActive;

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
        GD.Print("\n--- Start New Day ---");
        int currentDay = GetCurrentDay();

        // 1. Check for Weekly Council (Day 1, 8, 15, 22...)
        if (currentDay % 7 == 1)
        {
            var eligibility = GetPlayerCouncilData();
            if (eligibility.isEligible)
            {
                GD.Print("[TurnManager] Weekly Council Triggered for Player.");
                EmitSignal(SignalName.CouncilTriggered, eligibility.fid, eligibility.cid, eligibility.cp);
                return; // PAUSE DAY START
            }
        }

        ContinueStartNewDay(currentDay);
    }

    public void ResumeDayFromCouncil()
    {
        GD.Print("[TurnManager] Resuming Day from Council...");
        ContinueStartNewDay(GetCurrentDay());
    }

    private void ContinueStartNewDay(int currentDay)
    {
		// If Turn Queue is missing (Loaded game?) or it's a new week, Roll Initiative
		if (_turnQueue.Count == 0 || currentDay == 1 || (currentDay - 1) % 7 == 0)
		{
			bool isNewWeek = (currentDay == 1 || (currentDay - 1) % 7 == 0);
			if (isNewWeek || _turnQueue.Count == 0)
			{
				RollInitiative();
				if (isNewWeek) EmitSignal(SignalName.NewWeekStarted);
			}

			// AI Strategic Logic
			var ai = GetNode<FactionAI>("/root/FactionAI");
			using (var conn = DatabaseHelper.GetConnection())
			{
				conn.Open();
				var cmd = conn.CreateCommand();
				cmd.CommandText = "SELECT faction_id FROM factions";
				var fids = new List<int>();
				using (var r = cmd.ExecuteReader())
				{
					while (r.Read()) fids.Add(r.GetInt32(0));
				}

				if (isNewWeek)
				{
					// Monthly Finance
					if (currentDay == 1 || (currentDay - 1) % 28 == 0)
					{
						foreach (int fid in fids) ai.ProcessMonthlyFinances(conn, fid);
					}

					GD.Print("[TurnManager] Stage 1: Preparation...");
					foreach (int fid in fids)
					{
						// Evaluation
						var cities = ai.GetCityIdsByFaction(conn, fid);
						foreach (var cid in cities) ai.PerformPrefectEvaluation(conn, cid);

						ai.EnsureGoalsExist(conn, fid);
						ai.RepositionOfficers(conn, fid);
						ai.PerformStage1Prep(conn, fid);
					}
				}
				else
				{
					// Stage 2: Execution
					foreach (int fid in fids)
					{
						ai.PerformStage2Execution(conn, fid);
					}
				}
			}
		}

		_currentTurnIndex = -1;
		GD.Print($"[TurnManager] Starting Day {currentDay}. Queue Count: {_turnQueue.Count}");
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

        // Sort AI factions by score descending, then add Player (-1) at the VERY END
        _turnQueue = factionScores
            .Where(x => x.Key != -1)
            .OrderByDescending(x => x.Value)
            .Select(x => x.Key)
            .ToList();

        if (factionScores.ContainsKey(-1))
        {
            _turnQueue.Add(-1);
        }

        GD.Print($"Initiative Rolled for Week! Order: {string.Join(", ", _turnQueue)}");
    }

    private void AdvanceTurn()
    {
        _currentTurnIndex++;

        if (_currentTurnIndex >= _turnQueue.Count)
        {
            // All Faction turns done for the day. Proceed to Officer Phase.
            GD.Print("[TurnManager] Faction Turns Complete. Starting Officer Phase...");
            ProcessOfficerPhase();
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

    private void ProcessOfficerPhase()
    {
        // Trigger ActionManager to process all NPC daily actions
        var am = GetNode<ActionManager>("/root/ActionManager");
        am.ProcessAllOfficerTurns();

        // After all officers have moved, check for conflicts
        GD.Print("[TurnManager] Officer Phase Complete. Checking for Conflicts...");
        if (CheckForConflicts())
        {
            ResolveNextConflict();
        }
        else
        {
            EndDayCycle();
        }
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

    private (bool isEligible, int fid, int cid, int cp) GetPlayerCouncilData()
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();
            // 1. Is Leader?
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT faction_id, leader_id FROM factions WHERE leader_id = (SELECT officer_id FROM officers WHERE is_player = 1)";
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    int fid = r.GetInt32(0);
                    // Leaders hold council in their current city
                    r.Close();
                    var locCmd = conn.CreateCommand();
                    locCmd.CommandText = "SELECT location_id FROM officers WHERE is_player = 1";
                    int cid = Convert.ToInt32(locCmd.ExecuteScalar());
                    return (true, fid, cid, GameConstants.CP_RULER);
                }
            }

            // 2. Is Governor?
            var govCmd = conn.CreateCommand();
            govCmd.CommandText = "SELECT city_id, faction_id FROM cities WHERE governor_id = (SELECT officer_id FROM officers WHERE is_player = 1)";
            using (var r = govCmd.ExecuteReader())
            {
                if (r.Read())
                {
                    return (true, r.GetInt32(1), r.GetInt32(0), GameConstants.CP_GOVERNOR);
                }
            }
        }
        return (false, 0, 0, 0);
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
                    ActionManager.Instance.EmitSignal(ActionManager.SignalName.MapStateChanged);
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
        bm.CreateContext(cityId, sourceCityId);         // Check if player is involved (Anywhere in the city)
        bool playerInvolved = bm.CurrentContext.AllOfficers.Any(o => o.IsPlayer);

        if (playerInvolved)
        {
            GD.Print("Player involved in conflict! Loading Battle Scene...");

            // Close WorldMap Menus to prevent overlap
            var worldMap = GetTree().Root.FindChild("WorldMap", true, false) as WorldMap;
            if (IsInstanceValid(worldMap))
            {
                worldMap.HUD?.ClearSelectedCity();
                worldMap.OfficerCardDialog?.Hide();
            }

            // Try to find existing BattleSetupUI (e.g. permanent one in WorldMap)
            var existingUI = GetTree().Root.FindChild("BattleSetupUI", true, false) as BattleSetupUI;
            if (IsInstanceValid(existingUI))
            {
                GD.Print("Found existing BattleSetupUI. Opening...");
                // Ensure it is on TOP of the new scene (WorldMap might have been reloaded)
                existingUI.GetParent()?.MoveChild(existingUI, -1);
                existingUI.Open();
            }
            else
            {
                GD.Print("No existing BattleSetupUI found. Instantiating new one...");
                var scene = GD.Load<PackedScene>("res://scenes/BattleSetupUI.tscn");
                var instance = scene.Instantiate() as BattleSetupUI;
                GetTree().Root.AddChild(instance);
                instance.Open(); // Explicitly open in case it was hidden or default invisible
            }
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
            ActionManager.Instance.EmitSignal(ActionManager.SignalName.MapStateChanged);
        }
    }


    private void EndDayCycle()
    {
        ProcessCityDecay();

        int currentDay = GetCurrentDay();
        ProcessDailyLogistics(currentDay);

        // Ronin Agency: Independent officers can join factions if they like them
        FactionAI.Instance.ProcessRoninTurns();

        var am = GetNode<ActionManager>("/root/ActionManager");
        am.EndDay();

        StartNewDay();
    }

    private void ProcessDailyLogistics(int day)
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();

            // 1. Weekly: Merit Review & "Top Contributor"
            if (day % 7 == 0)
            {
                ProcessWeeklyMeritReview(conn);
            }

            // 2. Monthly: Taxation & Salaries
            if (day % 30 == 0)
            {
                ProcessMonthlyEconomics(conn);
            }

            // 3. Quarterly: Harvest (Supplies) - July (Day 180ish) and others
            if (day % 90 == 0)
            {
                ProcessQuarterlyHarvest(conn);
            }
        }
    }

    private void ProcessWeeklyMeritReview(SqliteConnection conn)
    {
        GD.Print("[Logistics] Weekly Merit Review...");
        // Reward Top Contributor in each city or faction
        // For simplicity: Top 1 in entire world gets big bonus, top of each faction gets small bonus

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT officer_id, faction_id, merit_score, name 
            FROM officers 
            WHERE merit_score > 0 
			ORDER BY merit_score DESC";

        bool worldFirst = true;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                int oid = r.GetInt32(0);
                int fid = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                int merit = r.GetInt32(2);
                string name = r.GetString(3);

                int goldBonus = worldFirst ? 500 : 100;
                int repBonus = worldFirst ? 50 : 20;

                ExecuteSql(conn, $"UPDATE officers SET gold = gold + {goldBonus}, reputation = reputation + {repBonus} WHERE officer_id = {oid}");
                GD.Print($"[Weekly Review] {name} awarded {goldBonus} Gold and {repBonus} Rep for Merit ({merit})!");

                worldFirst = false; // Only first one gets the big prize
            }
        }

        // Reset merit for next week
        ExecuteSql(conn, "UPDATE officers SET merit_score = 0");
    }

    private void ProcessDailyRevoltCheck(SqliteConnection conn)
    {
        // Public Revolt: PO < 20. Public Unrest: PO < 40.
		// We'll use the public_order column directly in queries, but we can set 
		// a conceptual status if we had a column. Since we unified PO, we just check PO.
	}

	private void ProcessAutoDraft(SqliteConnection conn)
	{
		GD.Print("[Logistics] Processing Auto-Draft...");
		// Formula: Draft = (Population / 50) * (Public Order / 100)
		// Cities in Revolt (PO < 20) get 0.
		ExecuteSql(conn, @"
            UPDATE officers SET troops = MIN(max_troops, troops + (
                SELECT (c.draft_population / 50) * (c.public_order / 100.0)
                FROM cities c
                WHERE c.city_id = officers.location_id
                AND c.public_order >= 20
            ))
			WHERE faction_id > 0 AND troops < max_troops");
	}

	private void ProcessMonthlyEconomics(SqliteConnection conn)
	{
		GD.Print("[Logistics] Monthly Economics (Taxes & Salaries)...");

		// 1. Collect Taxes into Faction Treasury
		// Formula: City Commerce * (Public Order / 100.0)
		// Revolting cities (PO < 20) generate 0.
		ExecuteSql(conn, @"
            UPDATE factions SET gold_treasury = gold_treasury + (
                SELECT SUM(CASE WHEN c.public_order < 20 THEN 0 ELSE c.commerce * (c.public_order / 100.0) END)
                FROM cities c
                WHERE c.faction_id = factions.faction_id
            )
			WHERE faction_id IN (SELECT DISTINCT faction_id FROM cities)");

		// 2. Pay Salaries (Deduct from Treasury) - Scales by Rank Level
		// Rank Levels: 0=0, 1=50, 2=80, 3=120, 4=200, 5=300, 6=450, 7=600, 8=800, 9=1000, 10=1500
		// We can approximate this as: 50 * level^1.3 or just a hardcoded mapping in SQL if possible, 
		// but easier to do a procedural update or a CASE statement.
		ExecuteSql(conn, @"
            UPDATE factions SET gold_treasury = gold_treasury - (
                SELECT SUM(
                    CASE 
						WHEN o.rank = '" + GameConstants.RANK_VOLUNTEER + @"' THEN 0
						WHEN o.rank = '" + GameConstants.RANK_RECRUIT + @"' THEN 50
						WHEN o.rank = '" + GameConstants.RANK_SOLDIER + @"' THEN 80
						WHEN o.rank = '" + GameConstants.RANK_VETERAN + @"' THEN 120
						WHEN o.rank = '" + GameConstants.RANK_SERGEANT + @"' THEN 200
						WHEN o.rank = '" + GameConstants.RANK_LIEUTENANT + @"' THEN 300
						WHEN o.rank = '" + GameConstants.RANK_CAPTAIN + @"' THEN 450
						WHEN o.rank = '" + GameConstants.RANK_MAJOR + @"' THEN 600
						WHEN o.rank = '" + GameConstants.RANK_GENERAL + @"' THEN 800
						WHEN o.rank = '" + GameConstants.RANK_COMMANDER + @"' THEN 1000
						WHEN o.rank = '" + GameConstants.RANK_SOVEREIGN + @"' THEN 1500
                        ELSE 50 
                    END
                )
                FROM officers o 
                WHERE o.faction_id = factions.faction_id
			)");

		// Check for Bankruptcy: If gold_treasury < 0, satisfaction drops
		ExecuteSql(conn, @"
            UPDATE officers 
            SET satisfaction = satisfaction - 20 
			WHERE faction_id IN (SELECT faction_id FROM factions WHERE gold_treasury < 0)");

		// Monthly Auto-Draft
		ProcessAutoDraft(conn);
	}

	private void ProcessQuarterlyHarvest(SqliteConnection conn)
	{
		GD.Print("[Logistics] Quarterly Harvest (Supplies)...");
		// Formula: Agriculture * 10 * (Public Order / 100.0)
		ExecuteSql(conn, @"
            UPDATE factions SET supplies = supplies + (
                SELECT SUM(CASE WHEN c.public_order < 20 THEN 0 ELSE c.agriculture * 10 * (c.public_order / 100.0) END) 
                FROM cities c
                WHERE c.faction_id = factions.faction_id
            )
			WHERE faction_id IN (SELECT DISTINCT faction_id FROM cities)");
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
