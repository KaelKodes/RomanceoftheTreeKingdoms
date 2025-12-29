using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class CityMenu : Control
{
	private string _cityName;

	// UI References
	private Label _titleLabel;
	private Label _infoLabel;
	private Control _actionList;

	public override void _Ready()
	{
		// Wire up nodes based on your screenshot structure:
		// CityMenu (Root) -> MarginContainer -> VBoxContainer -> [TitleLabel, InfoLabel, ActionList]
		_titleLabel = GetNode<Label>("MarginContainer/VBoxContainer/TitleLabel");
		_infoLabel = GetNode<Label>("MarginContainer/VBoxContainer/InfoLabel");
		_actionList = GetNode<Control>("MarginContainer/VBoxContainer/ActionList");
	}

	public void Init(string cityName)
	{
		_cityName = cityName;
		// If _Ready hasn't run yet (e.g. just instantiated), defer the update
		CallDeferred(nameof(RefreshData));
	}

	public void RefreshData()
	{
		if (_titleLabel == null) return;

		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();

			// 1. Get City Info
			var cmd = connection.CreateCommand();
			cmd.CommandText = @"
                SELECT c.economic_value, c.strategic_value, c.defense_level, f.name,
				       (SELECT COUNT(DISTINCT o2.faction_id) FROM officers o2 WHERE o2.location_id = c.city_id AND o2.faction_id IS NOT NULL) as faction_count
                FROM cities c
                LEFT JOIN factions f ON c.faction_id = f.faction_id
				WHERE c.name = $name";
			cmd.Parameters.AddWithValue("$name", _cityName);

			using (var reader = cmd.ExecuteReader())
			{
				if (reader.Read())
				{
					long econ = reader.GetInt64(0);
					long strat = reader.GetInt64(1);
					long def = reader.GetInt64(2);
					string faction = reader.IsDBNull(3) ? "Neutral" : reader.GetString(3);
					bool isContested = reader.GetInt64(4) > 1;

					// Update UI Labels
					_titleLabel.Text = $"{_cityName} ({faction})";
					if (isContested) _titleLabel.Text += " [WARZONE]";

					_infoLabel.Text = $"Economy: {econ}\nStrategy: {strat}\nDefense: {def}";

					SetupActions(isContested);
				}
			}
		}
	}

	private void SetupActions(bool isContested)
	{
		// Clear previous buttons
		foreach (Node child in _actionList.GetChildren()) child.QueueFree();

		// Check Action Manager for context
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr == null)
		{
			GD.PrintErr("ActionManager Autoload not found! Cannot determine context.");
			AddActionButton("Close", nameof(OnClosePressed));
			return;
		}

		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);
		long currentPlayerLoc = actionMgr.GetPlayerLocation(playerId);

		// Context Logic
		if (currentPlayerLoc == cityId)
		{
			// Check DB for Pending Battle
			bool isBattlePending = CheckPendingBattle(cityId);

			if (isBattlePending)
			{
				var label = new Label();
				label.Text = "Battle Declared (Pending End of Turn)";
				label.Modulate = Colors.Orange;
				_actionList.AddChild(label);
			}
			else
			{
				// Check for potential conflict
				int playerFaction = GetPlayerFaction(playerId);
				// Is there an enemy faction here?
				bool hasEnemy = CheckEnemyPresence(cityId, playerFaction);

				if (playerFaction > 0 && hasEnemy)
				{
					// Connectivity Check managed by "TryDeclareAttack" which calls AM.DeclareAttack
					// But we should visually cue it here if possible. 
					// Let's just try to Declare Attack on click and let AM handle error printing for now to keep UI simple.
					// Or perform a pre-check.
					AddActionButton("Declare Attack!", nameof(OnDeclareAttackPressed));
				}

				AddActionButton("Assist City (-1 AP)", nameof(OnAssistPressed));
				// AddActionButton("Visit Officers", nameof(OnVisitPressed));
			}
		}
		else
		{
			AddActionButton("Travel Here (-1 AP)", nameof(OnTravelPressed));
		}

		AddActionButton("Close", nameof(OnClosePressed));
	}

	private bool CheckPendingBattle(int cityId)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM pending_battles WHERE location_id = $id";
			cmd.Parameters.AddWithValue("$id", cityId);
			return (long)cmd.ExecuteScalar() > 0;
		}
	}

	private int GetPlayerFaction(int playerId)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT faction_id FROM officers WHERE officer_id = $id";
			cmd.Parameters.AddWithValue("$id", playerId);
			var res = cmd.ExecuteScalar();
			return (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;
		}
	}

	private bool CheckEnemyPresence(int cityId, int playerFaction)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			// Anyone NOT in my faction (and faction_id > 0) OR Neutral City itself if I want to conquer?
			// Actually, if City Faction != My Faction, I can attack logic.

			// 1. Is the City owned by someone else?
			cmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
			cmd.Parameters.AddWithValue("$cid", cityId);
			var res = cmd.ExecuteScalar();
			int cityOwner = (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;

			if (cityOwner != playerFaction) return true;

			// 2. Are there enemy officers squatting here?
			var cmd2 = connection.CreateCommand();
			cmd2.CommandText = "SELECT COUNT(*) FROM officers WHERE location_id = $cid AND faction_id != $pf AND faction_id IS NOT NULL";
			cmd2.Parameters.AddWithValue("$cid", cityId);
			cmd2.Parameters.AddWithValue("$pf", playerFaction);
			return (long)cmd2.ExecuteScalar() > 0;
		}
	}

	public void OnDeclareAttackPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);

		// Call AM
		actionMgr.DeclareAttack(playerId, cityId);
		RefreshData();
	}

	private void AddActionButton(string text, string methodName)
	{
		var btn = new Button();
		btn.Text = text;
		btn.Pressed += () => Call(methodName);
		_actionList.AddChild(btn);
	}

	// Button Handlers
	public void OnPrepareBattlePressed()
	{
		GD.Print("Preparing for Battle...");
		int cityId = GetCityId(_cityName);

		var bm = GetNode<BattleManager>("/root/BattleManager");
		bm.CreateContext(cityId);

		// Close this menu to make room for SETUP UI
		QueueFree();

		// Load BattleSetupUI
		var setupScene = GD.Load<PackedScene>("res://scenes/BattleSetupUI.tscn");
		if (setupScene != null)
		{
			GetTree().Root.AddChild(setupScene.Instantiate());
		}
		else
		{
			GD.PrintErr("BattleSetupUI.tscn not found!");
		}
	}

	public void OnAssistPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);

		actionMgr.PerformAssist(playerId, cityId);
		RefreshData();
	}

	public void OnTravelPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);

		actionMgr.PerformTravel(playerId, cityId);
		QueueFree(); // Close menu after travel
	}

	public void OnClosePressed()
	{
		QueueFree();
	}

	// Database Helpers
	private int GetPlayerId()
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var conn = new SqliteConnection($"Data Source={dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT officer_id FROM officers WHERE is_player = 1 LIMIT 1";
			var res = cmd.ExecuteScalar();
			return res != null ? (int)(long)res : -1;
		}
	}

	private int GetCityId(string name)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var conn = new SqliteConnection($"Data Source={dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT city_id FROM cities WHERE name = $name";
			cmd.Parameters.AddWithValue("$name", name);
			var res = cmd.ExecuteScalar();
			return res != null ? (int)(long)res : -1;
		}
	}
}
