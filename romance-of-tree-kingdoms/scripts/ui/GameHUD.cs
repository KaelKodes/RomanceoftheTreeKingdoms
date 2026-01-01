using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class GameHUD : Control
{
	[Signal] public delegate void OfficerClickedEventHandler(int officerId);

	[Export] public TextureRect PortraitRect;
	[Export] public Label NameLabel;
	[Export] public Label DateLabel;
	[Export] public Label APLabel;
	[Export] public Label GoldLabel;
	[Export] public Button EndDayButton;

	[Export] public TaskPopout TaskPopoutNode;
	[Export] public CityInfoPanel CurrentCityPanel;
	[Export] public VBoxContainer CurrentCityOfficers;
	[Export] public VBoxContainer CurrentCityActions;

	[Export] public CityInfoPanel SelectedCityPanel;
	[Export] public VBoxContainer SelectedCityOfficers;
	[Export] public VBoxContainer SelectedCityActions;

	// Stat Allocation UI
	private SpinBox _strSpin, _leaSpin, _intSpin, _polSpin, _chaSpin;
	private Label _availableLabel;
	private Label _rankUpLabel;
	private Button _submitButton;

	private int _availablePoints = 0;
	private int _initialPoints = 0;
	private bool _isRefreshing = false;

	// Local tracking for stats before submission
	private Dictionary<string, int> _initialStats = new Dictionary<string, int>();
	private Dictionary<string, int> _tempStats = new Dictionary<string, int>();
	private Dictionary<string, Label> _statLabels = new Dictionary<string, Label>();

	public override void _Ready()
	{
		// Stat Allocation UI (These paths might need adjustment in the new scene)
		_strSpin = GetNodeOrNull<SpinBox>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/STRSpinBox");
		_leaSpin = GetNodeOrNull<SpinBox>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/LEASpinBox");
		_intSpin = GetNodeOrNull<SpinBox>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/INTSpinBox");
		_polSpin = GetNodeOrNull<SpinBox>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/POLSpinBox");
		_chaSpin = GetNodeOrNull<SpinBox>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/CHASpinBox");
		_availableLabel = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/RankUpContainer/AVAILABLELabel");

		if (_strSpin != null) _strSpin.ValueChanged += (val) => OnStatChanged(val, "strength");
		if (_leaSpin != null) _leaSpin.ValueChanged += (val) => OnStatChanged(val, "leadership");
		if (_intSpin != null) _intSpin.ValueChanged += (val) => OnStatChanged(val, "intelligence");
		if (_polSpin != null) _polSpin.ValueChanged += (val) => OnStatChanged(val, "politics");
		if (_chaSpin != null) _chaSpin.ValueChanged += (val) => OnStatChanged(val, "charisma");

		_rankUpLabel = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/RankUpContainer/RankUpLabel");
		_submitButton = GetNodeOrNull<Button>("BottomBar/PlayerHud/HBoxContainer/RankUpContainer/SubmitButton");
		if (_submitButton != null) _submitButton.Pressed += OnSubmitPressed;

		_rankUpLabel?.Hide();
		_submitButton?.Hide();

		// Track labels for highlighting
		_statLabels["strength"] = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/Label");
		_statLabels["leadership"] = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/Label2");
		_statLabels["intelligence"] = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/Label3");
		_statLabels["politics"] = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/Label4");
		_statLabels["charisma"] = GetNodeOrNull<Label>("BottomBar/PlayerHud/HBoxContainer/PlayerStats/Label5");

		RefreshHUD();

		// Connect to ActionManager Signal
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.ActionPointsChanged += RefreshHUD;
			actionMgr.NewDayStarted += RefreshHUD;
			actionMgr.PlayerStatsChanged += RefreshHUD;
		}

		// Connect to TurnManager Signals
		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		if (turnMgr != null)
		{
			turnMgr.TurnStarted += OnTurnStarted;
			turnMgr.TurnEnded += OnTurnEnded;
			turnMgr.EnsureTurnSystemStarted();
		}

		if (EndDayButton != null) EndDayButton.Pressed += OnEndDayPressed;
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
		var turnMgr = GetNode<TurnManager>("/root/TurnManager");
		turnMgr.PlayerEndTurn();
	}

	private void OnTurnStarted(int factionId, bool isPlayer)
	{
		RefreshHUD();
		if (EndDayButton != null)
		{
			EndDayButton.Disabled = !isPlayer;
			EndDayButton.Text = isPlayer ? "End Day" : $"Faction {factionId} Moving...";
		}
	}

	private void OnTurnEnded()
	{
		if (EndDayButton != null) EndDayButton.Disabled = true;
	}

	public void RefreshHUD()
	{
		_isRefreshing = true;
		using (var connection = DatabaseHelper.GetConnection())
		{
			connection.Open();

			var cmd = connection.CreateCommand();
			cmd.CommandText = @"
				SELECT 
					o.current_action_points, o.max_action_points,
					o.gold, o.name, o.location_id, o.officer_id,
					o.stat_points,
					o.strength, o.base_strength,
					o.leadership, o.base_leadership,
					o.intelligence, o.base_intelligence,
					o.politics, o.base_politics,
					o.charisma, o.base_charisma
				FROM officers o
				WHERE o.is_player = 1";

			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					long ap = r.GetInt64(0);
					long maxAP = r.GetInt64(1);
					int gold = r.GetInt32(2);
					string playerName = r.GetString(3);
					int playerLoc = r.GetInt32(4);
					int officerId = r.GetInt32(5);
					_availablePoints = r.GetInt32(6);
					_initialPoints = _availablePoints;

					// Stats
					int str = r.GetInt32(7); int bStr = r.GetInt32(8);
					int lea = r.GetInt32(9); int bLea = r.GetInt32(10);
					int intel = r.GetInt32(11); int bIntel = r.GetInt32(12);
					int pol = r.GetInt32(13); int bPol = r.GetInt32(14);
					int cha = r.GetInt32(15); int bCha = r.GetInt32(16);

					// Local tracking
					_initialStats["strength"] = str; _initialStats["leadership"] = lea;
					_initialStats["intelligence"] = intel; _initialStats["politics"] = pol;
					_initialStats["charisma"] = cha;
					foreach (var kvp in _initialStats) _tempStats[kvp.Key] = kvp.Value;

					// Populate Header
					if (DateLabel != null) DateLabel.Text = GetDateString();
					if (NameLabel != null) NameLabel.Text = playerName;
					if (APLabel != null) APLabel.Text = $"AP: {ap}/{maxAP}";
					if (GoldLabel != null) GoldLabel.Text = $"Gold: {gold}";

					// TaskPopout
					if (TaskPopoutNode != null) TaskPopoutNode.Refresh(officerId, playerLoc);

					// Current City
					if (CurrentCityPanel != null)
					{
						CurrentCityPanel.Refresh(playerLoc);
						PopulateCityLists(playerLoc, CurrentCityOfficers, CurrentCityActions, true);
					}

					// Stat Allocation UI
					if (_availableLabel != null)
					{
						_availableLabel.Text = _availablePoints.ToString();
						_availableLabel.Visible = _availablePoints > 0;
					}
					if (_rankUpLabel != null) _rankUpLabel.Visible = _availablePoints > 0;

					UpdateSpin(_strSpin, str, bStr);
					UpdateSpin(_leaSpin, lea, bLea);
					UpdateSpin(_intSpin, intel, bIntel);
					UpdateSpin(_polSpin, pol, bPol);
					UpdateSpin(_chaSpin, cha, bCha);

					if (_strSpin != null) _strSpin.Editable = _availablePoints > 0;
					if (_leaSpin != null) _leaSpin.Editable = _availablePoints > 0;
					if (_intSpin != null) _intSpin.Editable = _availablePoints > 0;
					if (_polSpin != null) _polSpin.Editable = _availablePoints > 0;
					if (_chaSpin != null) _chaSpin.Editable = _availablePoints > 0;

					ResetStatColors();
				}
			}
		}
		_isRefreshing = false;
	}

	public void UpdateSelectedCity(int cityId)
	{
		if (SelectedCityPanel == null) return;
		SelectedCityPanel.Visible = true;
		SelectedCityPanel.Refresh(cityId);
		PopulateCityLists(cityId, SelectedCityOfficers, SelectedCityActions, false);
	}

	public void ClearSelectedCity()
	{
		if (SelectedCityPanel != null) SelectedCityPanel.Visible = false;
		if (SelectedCityOfficers != null) foreach (Node n in SelectedCityOfficers.GetChildren()) n.QueueFree();
		if (SelectedCityActions != null) foreach (Node n in SelectedCityActions.GetChildren()) n.QueueFree();
	}

	private string GetDateString()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT current_day FROM game_state LIMIT 1";
			var res = cmd.ExecuteScalar();
			long totalDays = (res != null) ? Convert.ToInt64(res) : 1;
			long year = 1 + (totalDays - 1) / 360;
			long month = 1 + ((totalDays - 1) % 360) / 30;
			long day = 1 + ((totalDays - 1) % 30);
			return $"Year {year}, Month {month}, Day {day}";
		}
	}

	private void PopulateCityLists(int cityId, VBoxContainer officerList, VBoxContainer actionList, bool isCurrentCity)
	{
		if (officerList == null || actionList == null) return;

		// Clear
		foreach (Node c in officerList.GetChildren()) c.QueueFree();
		foreach (Node c in actionList.GetChildren()) c.QueueFree();

		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();

			var cmd = conn.CreateCommand();
			cmd.CommandText = @"
				SELECT o.name, o.rank, o.troops, f.color, o.officer_id 
				FROM officers o
				LEFT JOIN factions f ON o.faction_id = f.faction_id
				WHERE o.location_id = $cid
				ORDER BY o.rank DESC, o.name ASC";
			cmd.Parameters.AddWithValue("$cid", cityId);
			using (var r = cmd.ExecuteReader())
			{
				while (r.Read())
				{
					string name = r.GetString(0);
					string rank = r.GetString(1);
					int troops = r.GetInt32(2);
					string color = r.IsDBNull(3) ? "#FFFFFF" : r.GetString(3);
					int oid = r.GetInt32(4);

					var btn = new Button();
					btn.Text = $"{name} ({rank}, {troops})";
					btn.Flat = true; // Make it look like a label but interactable
					btn.Alignment = HorizontalAlignment.Center;
					btn.Modulate = new Color(color);
					btn.MouseDefaultCursorShape = CursorShape.PointingHand;
					btn.Pressed += () => EmitSignal(SignalName.OfficerClicked, oid);
					officerList.AddChild(btn);
				}
			}

			// 2. Actions
			if (isCurrentCity)
			{
				AddActionButton(actionList, "Develop Commerce", OnCommercePressed);
				AddActionButton(actionList, "Cultivate Land", OnAgriculturePressed);
				AddActionButton(actionList, "Bolster Defense", OnDefensePressed);
				AddActionButton(actionList, "Enforce Order", OnOrderPressed);
			}
			else
			{
				AddActionButton(actionList, "Travel To", () => OnTravelToSelected(cityId));
			}
		}
	}

	private void AddActionButton(VBoxContainer container, string text, Action onPress)
	{
		var btn = new Button();
		btn.Text = text;
		btn.Pressed += onPress;
		container.AddChild(btn);
	}

	private void UpdateSpin(SpinBox spin, int current, int baseline)
	{
		if (spin == null) return;
		spin.MinValue = Math.Min(current, baseline);
		spin.MaxValue = 100;
		spin.Value = current;
		spin.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
	}

	private void ResetStatColors()
	{
		foreach (var label in _statLabels.Values) if (label != null) label.Modulate = Colors.White;
		_strSpin?.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_leaSpin?.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_intSpin?.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_polSpin?.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
		_chaSpin?.GetLineEdit().AddThemeColorOverride("font_color", Colors.White);
	}

	private void OnStatChanged(double value, string statName)
	{
		if (_isRefreshing) return;
		int newValue = (int)value;
		int oldValue = _tempStats[statName];
		int delta = newValue - oldValue;
		if (delta > 0 && (_availablePoints <= 0))
		{
			RefreshStatUI(statName);
			return;
		}
		if (delta == 0) return;
		_tempStats[statName] = newValue;
		_availablePoints -= delta;
		RefreshStatUI(statName);
		if (_availableLabel != null) _availableLabel.Text = _availablePoints.ToString();
		bool hasChanges = false;
		foreach (var key in _initialStats.Keys) if (_tempStats[key] != _initialStats[key]) hasChanges = true;
		if (_submitButton != null) _submitButton.Visible = hasChanges;
		if (_availablePoints <= 0) _rankUpLabel?.Hide();
		else _rankUpLabel?.Show();
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
		Color highlight = (current > initial) ? Colors.Green : Colors.White;
		spin.GetLineEdit().AddThemeColorOverride("font_color", highlight);
		if (_statLabels.ContainsKey(statName) && _statLabels[statName] != null)
			_statLabels[statName].Modulate = highlight;
	}

	private void OnSubmitPressed()
	{
		using (var conn = DatabaseHelper.GetConnection())
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE officers SET strength=$s, leadership=$l, intelligence=$i, politics=$p, charisma=$c, stat_points=$pts WHERE is_player=1";
			cmd.Parameters.AddWithValue("$s", _tempStats["strength"]);
			cmd.Parameters.AddWithValue("$l", _tempStats["leadership"]);
			cmd.Parameters.AddWithValue("$i", _tempStats["intelligence"]);
			cmd.Parameters.AddWithValue("$p", _tempStats["politics"]);
			cmd.Parameters.AddWithValue("$c", _tempStats["charisma"]);
			cmd.Parameters.AddWithValue("$pts", _availablePoints);
			cmd.ExecuteNonQuery();
		}
		RefreshHUD();
	}

	// Action Stubs
	private void OnCommercePressed() { var am = GetNode<ActionManager>("/root/ActionManager"); am.PerformDomesticAction(GetPlayerId(), GetPlayerLoc(), ActionManager.DomesticType.Commerce); RefreshHUD(); }
	private void OnAgriculturePressed() { var am = GetNode<ActionManager>("/root/ActionManager"); am.PerformDomesticAction(GetPlayerId(), GetPlayerLoc(), ActionManager.DomesticType.Agriculture); RefreshHUD(); }
	private void OnDefensePressed() { var am = GetNode<ActionManager>("/root/ActionManager"); am.PerformDomesticAction(GetPlayerId(), GetPlayerLoc(), ActionManager.DomesticType.Defense); RefreshHUD(); }
	private void OnOrderPressed() { var am = GetNode<ActionManager>("/root/ActionManager"); am.PerformDomesticAction(GetPlayerId(), GetPlayerLoc(), ActionManager.DomesticType.PublicOrder); RefreshHUD(); }
	private void OnTravelToSelected(int destId) { var am = GetNode<ActionManager>("/root/ActionManager"); am.PerformTravel(GetPlayerId(), destId); RefreshHUD(); }

	private int GetPlayerId() { using (var c = DatabaseHelper.GetConnection()) { c.Open(); var cmd = c.CreateCommand(); cmd.CommandText = "SELECT officer_id FROM officers WHERE is_player=1"; return Convert.ToInt32(cmd.ExecuteScalar()); } }
	private int GetPlayerLoc() { using (var c = DatabaseHelper.GetConnection()) { c.Open(); var cmd = c.CreateCommand(); cmd.CommandText = "SELECT location_id FROM officers WHERE is_player=1"; return Convert.ToInt32(cmd.ExecuteScalar()); } }
}
