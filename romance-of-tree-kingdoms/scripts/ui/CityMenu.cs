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
			SELECT o.officer_id, o.name, o.troops, f.color, f.name, o.rank, 
			       (CASE WHEN f.leader_id = o.officer_id THEN 1000 
			             WHEN c.governor_id = o.officer_id THEN 500 
						 ELSE 0 END) + 
				   (CASE o.rank WHEN 'Sovereign' THEN 5 WHEN 'General' THEN 4 WHEN 'Captain' THEN 3 WHEN 'Officer' THEN 2 WHEN 'Regular' THEN 1 ELSE 0 END) as hierarchy_score
			FROM officers o
			LEFT JOIN factions f ON o.faction_id = f.faction_id
			LEFT JOIN cities c ON o.location_id = c.city_id
			WHERE o.location_id = $cid
			ORDER BY hierarchy_score DESC, o.name ASC";
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
				int hScore = r.GetInt32(6);
				bool isLeader = hScore >= 1000;
				bool isGov = hScore >= 500 && hScore < 1000;

				var btn = new Button();
				string prefix = "";
				if (isLeader) prefix = "[Leader] ";
				else if (isGov) prefix = "[Gov] ";
				// Direct string usage, or title case if needed
				btn.Text = $"{prefix}{name} ({rank}, {troops})";
				btn.Alignment = HorizontalAlignment.Center;

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
		int playerFaction = GetPlayerFaction(playerId);

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
				label.HorizontalAlignment = HorizontalAlignment.Center;
				ActionList.AddChild(label);
			}
			else
			{
				AddActionButton("Develop Commerce (Pol)", nameof(OnCommercePressed));
				AddActionButton("Cultivate Land (Pol)", nameof(OnAgriculturePressed));
				AddActionButton("Bolster Defense (Lea)", nameof(OnDefensePressed));
				AddActionButton("Patrol / Order (Str)", nameof(OnPublicOrderPressed));
				AddActionButton("Research (Int)", nameof(OnTechnologyPressed));

				// Wisdom / Personal Actions
				AddActionButton("---------------", null); // Divider
				AddActionButton("Rest (Heal Army)", nameof(OnRestPressed));
				AddActionButton("Train Stats", nameof(OnTrainPressed));

				if (playerFaction > 0)
				{
					AddActionButton("Resign", nameof(OnResignPressed));
				}
				else // Ronin
				{
					if (CheckCityNeutral(cityId))
					{
						int playerRankLevel = GetPlayerRankLevel(playerId);
						if (playerRankLevel >= 3)
						{
							AddActionButton("Rise Up (Found Faction)", nameof(OnRiseUpPressed));
						}
						else
						{
							// Optional: Show disabled or informative label? 
							// User: "should only display if you can do it" -> so we hide it if rank is too low.
							// But we could show a hint if nearby. For now, strict as requested.
						}
					}
				}

				AddActionButton("Close", nameof(OnClosePressed));
			}
		}
		else
		{
			// Outside City (Adjacent or Distant)

			// 1. Check for Adjacent Hostile City -> Declare Attack
			playerFaction = GetPlayerFaction(playerId);
			int targetCityFaction = GetCityFaction(cityId);

			if (playerFaction > 0 && playerFaction != targetCityFaction)
			{
				if (IsAdjacent(currentPlayerLoc, cityId))
				{
					AddActionButton("Declare Attack!", nameof(OnDeclareAttackPressed));
				}
			}

			AddActionButton("Travel Here (-1 AP)", nameof(OnTravelPressed));


			AddActionButton("Close", nameof(OnClosePressed));
		}
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

	// Simple Train Toggle for now - Ideally a popup
	private bool _showingTrain = false;
	public void OnTrainPressed()
	{
		if (_showingTrain) { RefreshData(); _showingTrain = false; return; }

		_showingTrain = true;
		// Clear actions and show train options
		foreach (Node child in ActionList.GetChildren()) child.QueueFree();

		AddActionButton("Train Leadership", nameof(OnTrainLea));
		AddActionButton("Train Intelligence", nameof(OnTrainInt));
		AddActionButton("Train Strength", nameof(OnTrainStr));
		AddActionButton("Train Politics", nameof(OnTrainPol));
		AddActionButton("Train Charisma", nameof(OnTrainCha));
		AddActionButton("Back", nameof(RefreshData));
	}

	public void OnTrainLea() { DoTrain("Leadership"); }
	public void OnTrainInt() { DoTrain("Intelligence"); }
	public void OnTrainStr() { DoTrain("Strength"); }
	public void OnTrainPol() { DoTrain("Politics"); }
	public void OnTrainCha() { DoTrain("Charisma"); }

	private void DoTrain(string stat)
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();

		var mentors = actionMgr.GetPotentialMentors(playerId, stat);

		// Clear Actions to show Mentors
		foreach (Node child in ActionList.GetChildren()) child.QueueFree();

		var header = new Label();
		header.Text = $"Select a Mentor for {stat}:";
		header.HorizontalAlignment = HorizontalAlignment.Center;
		ActionList.AddChild(header);

		if (mentors.Count == 0)
		{
			var lbl = new Label();
			lbl.Text = "No capable mentors found in this city.";
			lbl.Modulate = Colors.Red;
			ActionList.AddChild(lbl);
		}
		else
		{
			foreach (var m in mentors)
			{
				// (int OfficerId, string Name, int StatValue, int Cost, int Relation)
				var btn = new Button();
				btn.Text = $"{m.Name} (Stat: {m.StatValue}, Cost: {m.Cost}g)";

				// Optional: Color code based on Relation?
				if (m.Relation >= 80) btn.Modulate = Colors.Green;
				else if (m.Relation <= 20) btn.Modulate = Colors.Salmon;

				// Capture variables for closure
				int mId = m.OfficerId;
				string statName = stat;

				btn.Pressed += () => ExecuteTrain(mId, statName);
				ActionList.AddChild(btn);
			}
		}

		AddActionButton("Back", nameof(OnTrainPressed));
	}

	private void ExecuteTrain(int mentorId, string stat)
	{
		var actionMgr = GetNode<ActionManager>("/root/ActionManager");
		int playerId = GetPlayerId();
		actionMgr.PerformTrain(playerId, mentorId, stat);

		// Refresh whole menu or go back to study?
		// Let's go back to Study menu to see updated stats maybe? Or just refresh data.
		RefreshData();
		_showingTrain = false; // Reset toggle so next click opens menu fresh
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

	private int GetCityFaction(int cityId)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT faction_id FROM cities WHERE city_id = $cid";
			cmd.Parameters.AddWithValue("$cid", cityId);
			var res = cmd.ExecuteScalar();
			return (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;
		}
	}

	private bool IsAdjacent(long fromCity, int toCity)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM routes WHERE (start_city_id = $c1 AND end_city_id = $c2) OR (start_city_id = $c2 AND end_city_id = $c1)";
			cmd.Parameters.AddWithValue("$c1", fromCity);
			cmd.Parameters.AddWithValue("$c2", toCity);
			return (long)cmd.ExecuteScalar() > 0;
		}
	}


	private int GetPlayerRankLevel(int officerId)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var conn = new SqliteConnection($"Data Source={dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT rank FROM officers WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", officerId);
			var res = cmd.ExecuteScalar();
			if (res != null)
			{
				return GameConstants.GetRankLevel((string)res);
			}
			return 0;
		}
	}

}
