using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class WorldMap : Control
{
	// Hardcoded coordinates for the "Hebei" prototype map
	// In a full game, these would be in the DB or a separate JSON layout file.
	private Dictionary<string, Vector2> _cityPositions = new Dictionary<string, Vector2>()
	{
		{ "Sun Capital", new Vector2(600, 300) },
		{ "Ironhold", new Vector2(600, 100) },
		{ "Eldershade", new Vector2(600, 500) },

		{ "Tiger Gate", new Vector2(600, 200) },
		{ "River Port", new Vector2(700, 400) },
		{ "Twin Peaks", new Vector2(500, 150) },

		{ "Central Plains", new Vector2(400, 300) },
		{ "West Hills", new Vector2(250, 300) },
		{ "Eastern Bay", new Vector2(900, 300) },
		{ "Mistwood", new Vector2(500, 450) },
		{ "South Fields", new Vector2(700, 550) } // Added South Fields
	};

	// Hardcoded coordinates for the "Hebei" prototype map
	// ... (Dictionary)

	private class RouteData { public string From; public string To; public string Type; }
	private class CityData { public string Name; public string FactionColor; public bool HasPlayer; public bool IsContested; }

	public override void _Draw()
	{
		// 1. Draw Routes (Lines)
		var routes = GetRoutesFromDB();
		foreach (var r in routes)
		{
			if (_cityPositions.ContainsKey(r.From) && _cityPositions.ContainsKey(r.To))
			{
				DrawLine(_cityPositions[r.From], _cityPositions[r.To], Colors.Gray, 5.0f);
			}
		}
	}

	// Keep track of buttons to update them later
	private Dictionary<string, Button> _cityButtons = new Dictionary<string, Button>();

	public override void _Ready()
	{
		// 1. Load HUD
		var hudScene = GD.Load<PackedScene>("res://scenes/GameHUD.tscn");
		if (hudScene != null)
		{
			var hud = hudScene.Instantiate();
			AddChild(hud);
		}

		// 2. Draw Cities (Initial)
		DrawCityNodes();

		// 3. Listen for updates
		// 3. Listen for updates
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.PlayerLocationChanged += UpdateCityVisuals;
			actionMgr.NewDayStarted += OnNewDayStarted;
		}
	}

	public override void _ExitTree()
	{
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.PlayerLocationChanged -= UpdateCityVisuals;
			actionMgr.NewDayStarted -= OnNewDayStarted;
		}
	}

	private void OnNewDayStarted()
	{
		// Check if player has a pending destination
		CheckForTravelContinuation();
	}

	private void CheckForTravelContinuation()
	{
		// We'll query DB for officer's destination
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();

			cmd.CommandText = @"
				SELECT c.name, o.destination_city_id, o.officer_id 
				FROM officers o 
				JOIN cities c ON o.destination_city_id = c.city_id
				WHERE o.is_player = 1 AND o.destination_city_id IS NOT NULL";

			using (var reader = cmd.ExecuteReader())
			{
				if (reader.Read())
				{
					string destName = reader.GetString(0);
					// destination is index 1
					int playerId = reader.GetInt32(2); // offset 2 is officer_id
					ShowTravelPopup(destName, playerId);
				}
			}
		}
	}

	private void ShowTravelPopup(string destName, int playerId)
	{
		var confirmation = new ConfirmationDialog();
		confirmation.Title = "Resume Travel";
		confirmation.DialogText = $"You are en route to {destName}.\nContinue traveling?";
		confirmation.Confirmed += () =>
		{
			var am = GetNode<ActionManager>("/root/ActionManager");
			am.ContinueTravel(playerId);
			confirmation.QueueFree();
		};
		confirmation.Canceled += () =>
		{
			var am = GetNode<ActionManager>("/root/ActionManager");
			am.CancelTravel(playerId);
			confirmation.QueueFree();
		};

		AddChild(confirmation);
		confirmation.PopupCentered();
	}

	private void DrawCityNodes()
	{
		var cities = GetCitiesFromDB();

		foreach (var c in cities)
		{
			if (!_cityPositions.ContainsKey(c.Name)) continue;

			// Create a simple Button for the city
			Button btn = new Button();
			btn.Text = c.Name;
			btn.Position = _cityPositions[c.Name] - new Vector2(40, 20); // Centerish
			btn.TooltipText = c.HasPlayer ? "You are here!" : "";

			// Color code by faction
			if (c.FactionColor != null && c.FactionColor.StartsWith("#"))
			{
				btn.Modulate = new Color(c.FactionColor);
			}

			// Connect signal
			btn.Pressed += () => OnCityPressed(c.Name);

			AddChild(btn);
			_cityButtons[c.Name] = btn; // Store reference
		}

		UpdateCityVisuals(); // Apply player tags
	}

	private List<CityData> GetCitiesFromDB()
	{
		var list = new List<CityData>();
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");

		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			// Check for player presence and CONTESTED status
			cmd.CommandText = @"
                SELECT c.name, f.color, 
                       (SELECT COUNT(*) FROM officers o WHERE o.location_id = c.city_id AND o.is_player = 1) as has_player,
                       (SELECT COUNT(*) FROM pending_battles pb WHERE pb.location_id = c.city_id) as is_pending
                FROM cities c
				LEFT JOIN factions f ON c.faction_id = f.faction_id";

			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					list.Add(new CityData
					{
						Name = reader.GetString(0),
						FactionColor = reader.IsDBNull(1) ? null : reader.GetString(1),
						HasPlayer = reader.GetInt64(2) > 0,
						IsContested = reader.GetInt64(3) > 0 // IsPending
					});
				}
			}
		}
		return list;
	}

	private void UpdateCityVisuals()
	{
		// Re-fetch data to catch new player location
		var cities = GetCitiesFromDB();

		foreach (var c in cities)
		{
			if (_cityButtons.ContainsKey(c.Name))
			{
				var btn = _cityButtons[c.Name];
				// Update text
				if (c.HasPlayer)
				{
					btn.Text = $"[P] {c.Name}";
					btn.Modulate = Colors.Gold;
				}
				else
				{
					btn.Text = c.Name;
					// Reset Color
					if (c.FactionColor != null) btn.Modulate = new Color(c.FactionColor);
					else btn.Modulate = Colors.White;
				}

				// Add Swords if Contested (Pending Battle)
				if (c.IsContested)
				{
					btn.Text += " ⚔️"; // Simple text append for now
				}
			}
		}
	}
	private void OnCityPressed(string cityName)
	{
		GD.Print($"City Clicked: {cityName}");
		OpenCityMenu(cityName);
	}

	private void OpenCityMenu(string cityName)
	{
		// Load the CityMenu Scene
		// Note: You must check the path matches your actual scene file
		var scene = GD.Load<PackedScene>("res://scenes/CityMenu.tscn");
		if (scene == null)
		{
			GD.PrintErr("Could not find res://scenes/CityMenu.tscn!");
			return;
		}

		var menu = scene.Instantiate() as CityMenu;
		if (menu != null)
		{
			menu.Init(cityName);

			// Center it on screen (approximate, usually UI logic handles this via anchors)
			menu.Position = new Vector2(400, 200);

			AddChild(menu);
		}
	}

	private List<RouteData> GetRoutesFromDB()
	{
		var list = new List<RouteData>();
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");

		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = @"
                SELECT c1.name as from_name, c2.name as to_name, r.route_type
                FROM routes r
                JOIN cities c1 ON r.start_city_id = c1.city_id
				JOIN cities c2 ON r.end_city_id = c2.city_id";

			using (var reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					list.Add(new RouteData
					{
						From = reader.GetString(0),
						To = reader.GetString(1),
						Type = reader.GetString(2)
					});
				}
			}
		}
		return list;
	}
}
