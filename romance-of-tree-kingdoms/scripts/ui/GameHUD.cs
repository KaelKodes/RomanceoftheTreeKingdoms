using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class GameHUD : Control
{
	// UI References
	private Label _dateLabel;
	private Label _apLabel;
	private Label _goldLabel; // New
	private Label _factionLabel;
	private Label _rankLabel;
	private Label _troopsLabel;
	private Label _recordLabel; // New
	private Button _endTurnButton;

	// Stat Allocation UI
	private SpinBox _strSpin, _leaSpin, _intSpin, _polSpin, _chaSpin;
	private Label _availableLabel;
	private Label _rankUpLabel;
	private Button _submitButton;

	private int _availablePoints = 0;
	private int _initialPoints = 0;
	private bool _isRefreshing = false;

	// Local tracking for stats before submission
	private System.Collections.Generic.Dictionary<string, int> _initialStats = new System.Collections.Generic.Dictionary<string, int>();
	private System.Collections.Generic.Dictionary<string, int> _tempStats = new System.Collections.Generic.Dictionary<string, int>();
	private System.Collections.Generic.Dictionary<string, Label> _statLabels = new System.Collections.Generic.Dictionary<string, Label>();

	public override void _Ready()
	{
		// Adjust paths based on your scene tree
		_dateLabel = GetNode<Label>("MarginContainer/VBoxContainer/DateLabel");
		_apLabel = GetNode<Label>("MarginContainer/VBoxContainer/APLabel");

		// Attempt to get GoldLabel (Safe check in case scene not updated yet)
		_goldLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/GoldLabel");

		_factionLabel = GetNode<Label>("MarginContainer/VBoxContainer/FactionLabel");
		_rankLabel = GetNode<Label>("MarginContainer/VBoxContainer/RankLabel");
		_troopsLabel = GetNode<Label>("MarginContainer/VBoxContainer/TroopsLabel");
		// New RecordLabel
		_recordLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/RecordLabel");

		// Stat Allocation UI
		_strSpin = GetNode<SpinBox>("PlayerHud/PlayerStats/STRSpinBox");
		_leaSpin = GetNode<SpinBox>("PlayerHud/PlayerStats/LEASpinBox");
		_intSpin = GetNode<SpinBox>("PlayerHud/PlayerStats/INTSpinBox");
		_polSpin = GetNode<SpinBox>("PlayerHud/PlayerStats/POLSpinBox");
		_chaSpin = GetNode<SpinBox>("PlayerHud/PlayerStats/CHASpinBox");
		_availableLabel = GetNode<Label>("RankUpContainer/AVAILABLELabel");

		_strSpin.ValueChanged += (val) => OnStatChanged(val, "strength");
		_leaSpin.ValueChanged += (val) => OnStatChanged(val, "leadership");
		_intSpin.ValueChanged += (val) => OnStatChanged(val, "intelligence");
		_polSpin.ValueChanged += (val) => OnStatChanged(val, "politics");
		_chaSpin.ValueChanged += (val) => OnStatChanged(val, "charisma");

		// Attempt to find button (assuming it's in the VBox or similar, user can adjust if needed/or I use Export)
		// Ideally we use [Export] but since we are hardcoding paths based on user preference:
		_endTurnButton = GetNode<Button>("MarginContainer/VBoxContainer/EndDayButton");

		_rankUpLabel = GetNode<Label>("RankUpContainer/RankUpLabel");
		_submitButton = GetNode<Button>("RankUpContainer/SubmitButton");
		_submitButton.Pressed += OnSubmitPressed;
		_submitButton.Hide();
		_rankUpLabel.Hide();

		// Track labels for highlighting
		_statLabels["strength"] = GetNode<Label>("PlayerHud/PlayerStats/Label");
		_statLabels["leadership"] = GetNode<Label>("PlayerHud/PlayerStats/Label2");
		_statLabels["intelligence"] = GetNode<Label>("PlayerHud/PlayerStats/Label3");
		_statLabels["politics"] = GetNodeOrNull<Label>("PlayerHud/PlayerStats/Label4"); // May be missing in user diff
		_statLabels["charisma"] = GetNode<Label>("PlayerHud/PlayerStats/Label5");

		RefreshHUD();

		// Connect to ActionManager Signal
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.ActionPointsChanged += RefreshHUD;
			// Also refresh on day start for new promo checks
			actionMgr.NewDayStarted += RefreshHUD;
			actionMgr.PlayerStatsChanged += RefreshHUD;
		}

		// Connect to TurnManager Signals (and start system)
		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		if (turnMgr != null)
		{
			turnMgr.TurnStarted += OnTurnStarted;
			turnMgr.TurnEnded += OnTurnEnded;

			// Bootstrap the turn system if needed
			turnMgr.EnsureTurnSystemStarted();
		}
	}

	public override void _ExitTree()
	{
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.ActionPointsChanged -= RefreshHUD;
			actionMgr.NewDayStarted -= RefreshHUD;
			actionMgr.PlayerStatsChanged -= RefreshHUD;
		}

		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		if (turnMgr != null)
		{
			turnMgr.TurnStarted -= OnTurnStarted;
			turnMgr.TurnEnded -= OnTurnEnded;
		}
	}

	public void OnEndDayPressed()
	{
		// Old: actionMgr.EndDay();
		// New: Call TurnManager
		var turnMgr = GetNode<TurnManager>("/root/TurnManager");
		turnMgr.PlayerEndTurn();
		// RefreshHUD(); // Wait for signal?
	}

	private void OnTurnStarted(int factionId, bool isPlayer)
	{
		RefreshHUD();
		if (isPlayer)
		{
			_endTurnButton.Disabled = false;
			_endTurnButton.Text = "End Turn";
		}
		else
		{
			_endTurnButton.Disabled = true;
			_endTurnButton.Text = $"Faction {factionId} Moving...";
		}

		// Override text if it IS player turn
		if (isPlayer) _endTurnButton.Text = "End Turn (Player)";
	}

	private void OnTurnEnded()
	{
		_endTurnButton.Disabled = true;
	}

	public void RefreshHUD()
	{
		_isRefreshing = true;
		using (var connection = DatabaseHelper.GetConnection())
		{
			connection.Open();

			// 1. Get Player AP and Extended Info
			var cmd = connection.CreateCommand();
			cmd.CommandText = @"
                SELECT 
                    o.current_action_points, 
                    o.rank, 
                    o.max_troops, 
                    o.reputation, 
                    o.battles_won, 
                    o.battles_lost,
                    o.gold,
                    f.name as faction_name,
                    o.max_action_points,
                    o.troops,
                    o.name, -- Added player name
                    o.stat_points,
                    o.strength, o.base_strength,
                    o.leadership, o.base_leadership,
                    o.intelligence, o.base_intelligence,
                    o.politics, o.base_politics,
                    o.charisma, o.base_charisma
                FROM officers o
                LEFT JOIN factions f ON o.faction_id = f.faction_id
				WHERE o.is_player = 1";

			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					// Use named ordinals to prevent index shift bugs
					int colAP = r.GetOrdinal("current_action_points");
					int colRank = r.GetOrdinal("rank");
					int colMaxT = r.GetOrdinal("max_troops");
					int colRep = r.GetOrdinal("reputation");
					int colWins = r.GetOrdinal("battles_won");
					int colLoss = r.GetOrdinal("battles_lost");
					int colGold = r.GetOrdinal("gold");
					int colFact = r.GetOrdinal("faction_name");
					int colMaxAP = r.GetOrdinal("max_action_points");
					int colTroops = r.GetOrdinal("troops");
					int colName = r.GetOrdinal("name");
					int colPts = r.GetOrdinal("stat_points");

					int colStr = r.GetOrdinal("strength");
					int colBStr = r.GetOrdinal("base_strength");
					int colLea = r.GetOrdinal("leadership");
					int colBLea = r.GetOrdinal("base_leadership");
					int colInt = r.GetOrdinal("intelligence");
					int colBInt = r.GetOrdinal("base_intelligence");
					int colPol = r.GetOrdinal("politics");
					int colBPol = r.GetOrdinal("base_politics");
					int colCha = r.GetOrdinal("charisma");
					int colBCha = r.GetOrdinal("base_charisma");

					long ap = r.GetInt64(colAP);
					string rank = r.GetString(colRank);
					int maxTroops = r.IsDBNull(colMaxT) ? 0 : r.GetInt32(colMaxT);
					int rep = r.IsDBNull(colRep) ? 0 : r.GetInt32(colRep);
					int wins = r.IsDBNull(colWins) ? 0 : r.GetInt32(colWins);
					int losses = r.IsDBNull(colLoss) ? 0 : r.GetInt32(colLoss);
					int gold = r.IsDBNull(colGold) ? 0 : r.GetInt32(colGold);
					string factionName = r.IsDBNull(colFact) ? "Ronin" : r.GetString(colFact);
					int maxAP = r.IsDBNull(colMaxAP) ? 3 : r.GetInt32(colMaxAP);
					int currentTroops = r.IsDBNull(colTroops) ? 0 : r.GetInt32(colTroops);
					string playerName = r.GetString(colName);

					// Stats
					_availablePoints = r.GetInt32(colPts);
					_initialPoints = _availablePoints;
					int str = r.GetInt32(colStr); int bStr = r.GetInt32(colBStr);
					int lea = r.GetInt32(colLea); int bLea = r.GetInt32(colBLea);
					int intel = r.GetInt32(colInt); int bIntel = r.GetInt32(colBInt);
					int pol = r.GetInt32(colPol); int bPol = r.GetInt32(colBPol);
					int cha = r.GetInt32(colCha); int bCha = r.GetInt32(colBCha);

					// Initialize local stats
					_initialStats["strength"] = str;
					_initialStats["leadership"] = lea;
					_initialStats["intelligence"] = intel;
					_initialStats["politics"] = pol;
					_initialStats["charisma"] = cha;

					foreach (var kvp in _initialStats) _tempStats[kvp.Key] = kvp.Value;

					// Emergency Baseline Correction
					if (bStr > str || bLea > lea || bIntel > intel || bPol > pol || bCha > cha)
					{
						GD.PrintErr($"[HUD] Baseline mismatch detected for {playerName}. Current stats are lower than base. Syncing...");
						using (var upConn = DatabaseHelper.GetConnection())
						{
							upConn.Open();
							var fCmd = upConn.CreateCommand();
							fCmd.CommandText = "UPDATE officers SET base_strength = strength, base_leadership = leadership, base_intelligence = intelligence, base_politics = politics, base_charisma = charisma WHERE is_player = 1";
							fCmd.ExecuteNonQuery();
						}
						// Use the real stats as baselines for this frame to avoid clamping
						bStr = Math.Min(str, bStr); bLea = Math.Min(lea, bLea); bIntel = Math.Min(intel, bIntel);
						bPol = Math.Min(pol, bPol); bCha = Math.Min(cha, bCha);
					}

					GD.Print($"[HUD Refresh] {playerName} Stat Check: STR={str}/Base={bStr}, Pts={_availablePoints}");

					_apLabel.Text = $"AP: {ap}/{maxAP}";
					if (_goldLabel != null) _goldLabel.Text = $"Gold: {gold}";
					_factionLabel.Text = $"{factionName}";
					_rankLabel.Text = $"{rank} (Rep: {rep})";
					_troopsLabel.Text = $"Command: {currentTroops}/{maxTroops}";
					if (_recordLabel != null) _recordLabel.Text = $"Record: {wins}W - {losses}L";

					_rankUpLabel.Visible = _availablePoints > 0;
					_submitButton.Hide();

					// Update SpinBoxes
					UpdateSpin(_strSpin, str, bStr);
					UpdateSpin(_leaSpin, lea, bLea);
					UpdateSpin(_intSpin, intel, bIntel);
					UpdateSpin(_polSpin, pol, bPol);
					UpdateSpin(_chaSpin, cha, bCha);

					_strSpin.Editable = _availablePoints > 0;
					_leaSpin.Editable = _availablePoints > 0;
					_intSpin.Editable = _availablePoints > 0;
					_polSpin.Editable = _availablePoints > 0;
					_chaSpin.Editable = _availablePoints > 0;

					_availableLabel.Text = _availablePoints.ToString();
					_availableLabel.Visible = _availablePoints > 0;

					// Reset highlights
					ResetStatColors();
				}
			}

			// 2. Get Date
			var dateCmd = connection.CreateCommand();
			dateCmd.CommandText = "SELECT current_day FROM game_state LIMIT 1";
			var dayObj = dateCmd.ExecuteScalar();
			long totalDays = (dayObj != null && dayObj != DBNull.Value) ? (long)dayObj : 1;

			// Calculate Calendar
			// Assuming 30 days per month, 12 months per year
			long year = 1 + (totalDays - 1) / 360;
			long month = 1 + ((totalDays - 1) % 360) / 30;
			long day = 1 + ((totalDays - 1) % 30);

			_dateLabel.Text = $"Year {year}, Month {month}, Day {day}";
		}
		_isRefreshing = false;
	}

	private void UpdateSpin(SpinBox spin, int current, int baseline)
	{
		// Use the lower of the two as MinValue to prevent clamping a low stat (like 32) up to a stale baseline (like 50)
		spin.MinValue = Math.Min(current, baseline);
		spin.MaxValue = 100; // soft cap
		spin.Value = current;

		// Clear font override initially
		spin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
	}

	private void ResetStatColors()
	{
		foreach (var label in _statLabels.Values) if (label != null) label.Modulate = Colors.White;
		_strSpin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_leaSpin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_intSpin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_polSpin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_chaSpin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
	}

	private void OnStatChanged(double value, string statName)
	{
		if (_isRefreshing) return;

		int newValue = (int)value;
		int oldValue = _tempStats[statName];
		int delta = newValue - oldValue;

		// Validation: Can't spend more than we have
		int totalSpent = 0;
		foreach (var key in _initialStats.Keys) totalSpent += (_tempStats[key] - _initialStats[key]);

		if (delta > 0 && (_availablePoints <= 0))
		{
			GD.Print("No stat points available!");
			RefreshStatUI(statName); // Revert UI
			return;
		}

		if (delta == 0) return;

		// Update local state
		_tempStats[statName] = newValue;
		_availablePoints -= delta;

		// Update UI
		RefreshStatUI(statName);
		_availableLabel.Text = _availablePoints.ToString();
		_availableLabel.Visible = _availablePoints > 0;

		// Show Submit button if anything changed
		bool hasChanges = false;
		foreach (var key in _initialStats.Keys) if (_tempStats[key] != _initialStats[key]) hasChanges = true;
		_submitButton.Visible = hasChanges;

		// If no points left, hide level up label (unless user wants it to stay until submit)
		// User said: "If no points are left to spend, the RankUpLabel and Submit button go back to being hidden"
		// But they also said: "once the player puts a point in a stat we have SubmitButton become visible... locked in"
		// Interpretation: RankUpLabel hides when 0 points. SubmitButton hides after click.
		if (_availablePoints <= 0) _rankUpLabel.Hide();
		else _rankUpLabel.Show();
	}

	private void RefreshStatUI(string statName)
	{
		SpinBox spin = null;
		switch (statName)
		{
			case "strength": spin = _strSpin; break;
			case "leadership": spin = _leaSpin; break;
			case "intelligence": spin = _intSpin; break;
			case "politics": spin = _polSpin; break;
			case "charisma": spin = _chaSpin; break;
		}

		if (spin == null) return;

		int current = _tempStats[statName];
		int initial = _initialStats[statName];

		_isRefreshing = true;
		spin.Value = current;
		_isRefreshing = false;

		bool modified = current > initial;
		Color highlight = modified ? Colors.Green : Colors.White;

		spin.GetLineEdit().AddThemeColorOverride("font_color", highlight);
		if (_statLabels.ContainsKey(statName) && _statLabels[statName] != null)
		{
			_statLabels[statName].Modulate = highlight;
		}
	}

	private void OnSubmitPressed()
	{
		using (var connection = DatabaseHelper.GetConnection())
		{
			connection.Open();
			using (var transaction = connection.BeginTransaction())
			{
				try
				{
					var cmd = connection.CreateCommand();
					cmd.Transaction = transaction;
					cmd.CommandText = @"
						UPDATE officers 
						SET strength = $str, 
							leadership = $lea, 
							intelligence = $int, 
							politics = $pol, 
							charisma = $cha,
							stat_points = $pts
						WHERE is_player = 1";

					cmd.Parameters.AddWithValue("$str", _tempStats["strength"]);
					cmd.Parameters.AddWithValue("$lea", _tempStats["leadership"]);
					cmd.Parameters.AddWithValue("$int", _tempStats["intelligence"]);
					cmd.Parameters.AddWithValue("$pol", _tempStats["politics"]);
					cmd.Parameters.AddWithValue("$cha", _tempStats["charisma"]);
					cmd.Parameters.AddWithValue("$pts", _availablePoints);

					cmd.ExecuteNonQuery();
					transaction.Commit();

					GD.Print("[HUD] Stats saved successfully.");
				}
				catch (Exception ex)
				{
					transaction.Rollback();
					GD.PrintErr($"[HUD] Failed to save stats: {ex.Message}");
				}
			}
		}

		RefreshHUD();
	}
}
