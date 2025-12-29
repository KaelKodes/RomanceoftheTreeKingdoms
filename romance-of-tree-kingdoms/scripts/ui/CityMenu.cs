using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class CityMenu : Control
{
	[Signal] public delegate void OfficerSelectedEventHandler(int officerId);
	[Signal] public delegate void InteractionEndedEventHandler();

	private string _cityName;

	// UI References
	[Export] public Label TitleLabel { get; set; }
	[Export] public Label InfoLabel { get; set; }
	[Export] public Label OfficersHeader { get; set; }
	[Export] public ScrollContainer OfficerScroll { get; set; }
	[Export] public VBoxContainer OfficerContainer { get; set; }
	[Export] public Label ActionsHeader { get; set; }
	[Export] public Control ActionList { get; set; }

	public override void _Ready()
	{
		// UI is now wired via [Export] / Scene
	}

	public void Init(string cityName)
	{
		_cityName = cityName;
		// If _Ready hasn't run yet (e.g. just instantiated), defer the update
        CallDeferred(nameof(RefreshData));
    }

    public void RefreshData()
    {
        if (TitleLabel == null) return;

        string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();

            // 1. Get City Info
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT c.commerce, c.agriculture, c.defense_level, f.name,
				       (SELECT COUNT(DISTINCT o2.faction_id) FROM officers o2 WHERE o2.location_id = c.city_id AND o2.faction_id IS NOT NULL) as faction_count,
					   c.city_id,
					   c.technology, c.public_order,
					   f.color
                FROM cities c
                LEFT JOIN factions f ON c.faction_id = f.faction_id
				WHERE c.name = $name";
            cmd.Parameters.AddWithValue("$name", _cityName);

            int cityId = -1;

            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    long econ = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    long agric = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    long def = reader.GetInt64(2);
                    string faction = reader.IsDBNull(3) ? "Neutral" : reader.GetString(3);
                    bool isContested = reader.GetInt64(4) > 1;
                    cityId = reader.GetInt32(5);
                    long tech = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                    long order = reader.IsDBNull(7) ? 50 : reader.GetInt64(7);
                    string fColor = reader.IsDBNull(8) ? "#FFFFFF" : reader.GetString(8);

                    // Update UI Labels
                    TitleLabel.Text = $"{_cityName} ({faction})";
                    TitleLabel.AddThemeColorOverride("font_color", new Color(fColor));
                    if (isContested) TitleLabel.Text += " [WARZONE]";

                    InfoLabel.Text = $"Merchant: {econ} | Farm: {agric} | Def: {def}\nTech: {tech} | Order: {order}";

                    SetupActions(isContested);
                }
            }

            if (cityId != -1)
            {
                PopulateOfficerList(connection, cityId);
            }
        }
    }

    private void PopulateOfficerList(SqliteConnection conn, int cityId)
    {
        // Clear existing
        foreach (Node child in OfficerContainer.GetChildren())
        {
            child.QueueFree();
        }

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
			SELECT o.officer_id, o.name, o.troops, f.color, f.name, o.rank, o.is_commander
			FROM officers o
			LEFT JOIN factions f ON o.faction_id = f.faction_id
			WHERE o.location_id = $cid
			ORDER BY o.is_commander DESC, o.rank DESC"; // Commanders first, then highest rank
        cmd.Parameters.AddWithValue("$cid", cityId);

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                int id = r.GetInt32(0);
                string name = r.GetString(1);
                int troops = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                string hexColor = r.IsDBNull(3) ? "#CCCCCC" : r.GetString(3);
                string factionName = r.IsDBNull(4) ? "Free" : r.GetString(4);
                string rank = r.IsDBNull(5) ? "Volunteer" : r.GetString(5);
                bool isGov = !r.IsDBNull(6) && r.GetBoolean(6);

                var btn = new Button();
                string prefix = isGov ? "[Gov] " : "";
                // Direct string usage, or title case if needed
                btn.Text = $"{prefix}{name} ({rank}, {troops})";
                btn.Alignment = HorizontalAlignment.Left;

                // Apply Color to Text
                btn.AddThemeColorOverride("font_color", new Color(hexColor));

                btn.Pressed += () => OnOfficerPressed(id);
                OfficerContainer.AddChild(btn);
            }
        }
    }

    private void OnOfficerPressed(int officerId)
    {
        GD.Print($"Clicked Officer {officerId}. Emitting Signal...");
        EmitSignal(SignalName.OfficerSelected, officerId);
    }

    private void SetupActions(bool isContested)
    {
        // Clear previous buttons
        foreach (Node child in ActionList.GetChildren()) child.QueueFree();

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
                label.Text = "Battle Declared (End of Turn)";
                label.Modulate = Colors.Orange;
                ActionList.AddChild(label);
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

				AddActionButton("Develop Commerce (Pol)", nameof(OnCommercePressed));
				AddActionButton("Cultivate Land (Pol)", nameof(OnAgriculturePressed));
				AddActionButton("Bolster Defense (Lea)", nameof(OnDefensePressed));
				AddActionButton("Patrol / Order (Str)", nameof(OnPublicOrderPressed));
				AddActionButton("Research (Int)", nameof(OnTechnologyPressed));

				// Wisdom / Personal Actions
				AddActionButton("---------------", null); // Divider
				AddActionButton("Rest (Heal Army)", nameof(OnRestPressed));
				AddActionButton("Study Stats", nameof(OnStudyPressed));

				if (playerFaction > 0) // And not sovereign? Checked in AM
				{
					AddActionButton("Resign", nameof(OnResignPressed));
				}
			}
		}
		else
		{
			AddActionButton("Travel Here (-1 AP)", nameof(OnTravelPressed));

			// If Ronin and Neutral City -> Rise Up
			int playerFaction = GetPlayerFaction(playerId);
			if (playerFaction == 0)
			{
				bool isNeutral = CheckCityNeutral(cityId);
				if (isNeutral)
				{
					AddActionButton("Rise Up (Found Faction)", nameof(OnRiseUpPressed));
				}
			}
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
		ActionList.AddChild(btn);
	}

	// Button Handlers
	public void OnPrepareBattlePressed()
	{
		GD.Print("Preparing for Battle...");
		int cityId = GetCityId(_cityName);

		// Check BattleManager context?
		var bm = GetNode<BattleManager>("/root/BattleManager");
		bm.CreateContext(cityId);

		// Hide this menu to make room for SETUP UI
		Hide();
		EmitSignal(SignalName.InteractionEnded);

		// Load BattleSetupUI - logic remains similar as BattleSetup might still be dynamic for now?
		// Or should we ask WorldMap to handle this too? 
		// For now, let's keep BattleSetup dynamic as user didn't request it yet.
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

	public void OnCommercePressed() { PerformDomestic(ActionManager.DomesticType.Commerce); }
	public void OnAgriculturePressed() { PerformDomestic(ActionManager.DomesticType.Agriculture); }
	public void OnDefensePressed() { PerformDomestic(ActionManager.DomesticType.Defense); }
	public void OnPublicOrderPressed() { PerformDomestic(ActionManager.DomesticType.PublicOrder); }
	public void OnTechnologyPressed() { PerformDomestic(ActionManager.DomesticType.Technology); }

	private void PerformDomestic(ActionManager.DomesticType type)
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);
		actionMgr.PerformDomesticAction(playerId, cityId, type);
		RefreshData();
	}

	public void OnTravelPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);

		actionMgr.PerformTravel(playerId, cityId);
		Hide(); // Close menu after travel
		EmitSignal(SignalName.InteractionEnded);
	}

	public void OnClosePressed()
	{
		Hide();
		EmitSignal(SignalName.InteractionEnded);
	}

	public void OnRestPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		actionMgr.PerformRest(playerId);
		RefreshData();
	}

	public void OnResignPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		actionMgr.PerformResign(playerId);
		RefreshData();
	}

	public void OnRiseUpPressed()
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		int cityId = GetCityId(_cityName);
		actionMgr.PerformRiseUp(playerId, cityId);
		RefreshData();
	}

	// Simple Study Toggle for now - Ideally a popup
	private bool _showingStudy = false;
	public void OnStudyPressed()
	{
		if (_showingStudy) { RefreshData(); _showingStudy = false; return; }

		_showingStudy = true;
		// Clear actions and show study options
		foreach (Node child in ActionList.GetChildren()) child.QueueFree();

		AddActionButton("Study Leadership", nameof(OnStudyLea));
		AddActionButton("Study Intelligence", nameof(OnStudyInt));
		AddActionButton("Study Strength", nameof(OnStudyStr));
		AddActionButton("Study Politics", nameof(OnStudyPol));
		AddActionButton("Study Charisma", nameof(OnStudyCha));
		AddActionButton("Back", nameof(RefreshData));
	}

	public void OnStudyLea() { DoStudy("Leadership"); }
	public void OnStudyInt() { DoStudy("Intelligence"); }
	public void OnStudyStr() { DoStudy("Strength"); }
	public void OnStudyPol() { DoStudy("Politics"); }
	public void OnStudyCha() { DoStudy("Charisma"); }

	private void DoStudy(string stat)
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		actionMgr.PerformStudy(playerId, stat);
		OnStudyPressed(); // Refresh study menu 
	}

	private bool CheckCityNeutral(int cityId)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
			cmd.Parameters.AddWithValue("$cid", cityId);
			var res = cmd.ExecuteScalar();
			return (res == null || res == DBNull.Value || (long)res == 0);
		}
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
