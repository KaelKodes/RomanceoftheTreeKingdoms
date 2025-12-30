using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public class RouteData { public string From; public string To; public string Type; }

public partial class WorldMap : Control
{
	// Hardcoded coordinates for the "Hebei" prototype map
	// In a full game, these would be in the DB or a separate JSON layout file.
	[Export] public Control CityContainer { get; set; }
	[Export] public GameHUD HUD { get; set; }
	[Export] public CityMenu CityMenuDialog { get; set; }
	[Export] public OfficerCard OfficerCardDialog { get; set; }
	[Export] public RouteOverlay RouteOverlayNode { get; set; }

	// Keep track of buttons to update them later
	private Dictionary<string, Button> _cityButtons = new Dictionary<string, Button>();
	public Dictionary<string, Button> GetCityButtons() => _cityButtons;

	private class CityData { public string Name; public string FactionColor; public bool HasPlayer; public bool IsContested; }

	// Spacing & Alignment
	private Dictionary<string, Vector2> _initialPositions = new Dictionary<string, Vector2>();
	private Vector2 _mapCentroid;
	private Vector2 _panOffset;
	private float _currentSpread = 1.0f;
	private const float MIN_SPREAD = 0.5f;
	private const float MAX_SPREAD = 3.0f;

	private void AdjustSpacing(float amount)
	{
		_currentSpread += amount;
		_currentSpread = Mathf.Clamp(_currentSpread, MIN_SPREAD, MAX_SPREAD);
		ApplySpacing();
	}

	private void ApplySpacing()
	{
		foreach (var kvp in _cityButtons)
		{
			if (_initialPositions.ContainsKey(kvp.Key))
			{
				// Scale relative to centroid: NewPos = Centroid + (InitPos - Centroid) * Spread + PanOffset
				var direction = _initialPositions[kvp.Key] - _mapCentroid;
				kvp.Value.Position = _mapCentroid + (direction * _currentSpread) + _panOffset;
			}
		}

		// Redraw Routes
		if (RouteOverlayNode != null)
		{
			RouteOverlayNode.QueueRedraw();
		}
	}

	// Keep track of buttons to update them later


	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.Pressed && mb.ButtonIndex == MouseButton.Left)
			{
				// If background is clicked, hide menu
				if (CityMenuDialog != null && CityMenuDialog.Visible)
				{
					CityMenuDialog.Hide();
					UpdateCityVisuals(); // Refresh state
				}
			}
			// Spacing Spread
			else if (mb.ButtonIndex == MouseButton.WheelUp)
			{
				AdjustSpacing(0.1f);
				AcceptEvent();
			}
			else if (mb.ButtonIndex == MouseButton.WheelDown)
			{
				AdjustSpacing(-0.1f);
				AcceptEvent();
			}
			// Pan removed as we aren't using camera anymore
        }
        // Pan (Global Drag with L+R)
        else if (@event is InputEventMouseMotion mm)
        {
            // Check if BOTH Left and Right are pressed
            if ((mm.ButtonMask & MouseButtonMask.Left) != 0 && (mm.ButtonMask & MouseButtonMask.Right) != 0)
            {
                _panOffset += mm.Relative;
                ApplySpacing();
                AcceptEvent();
            }
        }
    }


    public override void _Ready()
    {
        // 1. HUD is in scene
        if (HUD == null) GD.PrintErr("HUD not linked in WorldMap!");

        // 2. Wire up Dialogs
        // 2. Wire up Dialogs
        if (CityMenuDialog != null)
        {
            CityMenuDialog.Hide(); // Ensure hidden on start
            CityMenuDialog.OfficerSelected += ShowOfficerCard;
            CityMenuDialog.InteractionEnded += OnMenuClosed;
        }

        if (OfficerCardDialog != null)
        {
            OfficerCardDialog.Hide();
        }

        // 2. Init Cities from Scene
        InitCityNodes();

        // 3. Listen for updates
        // 3. Listen for updates
        var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
        if (actionMgr != null)
        {
            actionMgr.PlayerLocationChanged += UpdateCityVisuals;
            actionMgr.NewDayStarted += OnNewDayStarted;
            actionMgr.MapStateChanged += UpdateCityVisuals;
        }

        var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
        if (turnMgr != null)
        {
            turnMgr.TurnStarted += (fid, isPlayer) => UpdateCityVisuals();
            turnMgr.TurnEnded += UpdateCityVisuals;
        }

        // 4. Capture Initial Positions and Calc Centroid
        Vector2 sumPos = Vector2.Zero;
        int count = 0;
        foreach (var kvp in _cityButtons)
        {
            _initialPositions[kvp.Key] = kvp.Value.Position;
            sumPos += kvp.Value.Position;
            count++;
        }
        if (count > 0) _mapCentroid = sumPos / count;


    }


    public override void _ExitTree()
    {
        var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
        if (actionMgr != null)
        {
            actionMgr.PlayerLocationChanged -= UpdateCityVisuals;
            actionMgr.NewDayStarted -= OnNewDayStarted;
            actionMgr.MapStateChanged -= UpdateCityVisuals;
        }

        var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
        if (turnMgr != null)
        {
			// Note: Lambdas aren't easy to disconnect, but since this is Control-level script
			// and ActionManager/TurnManager are singletons in Autoload, we should be okay
			// or use a dedicated method if we want to be safe.
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

	private void InitCityNodes()
	{
		if (CityContainer == null) return;
		_cityButtons.Clear();

		foreach (var node in CityContainer.GetChildren())
		{
			if (node is Button btn)
			{
				string key = btn.Text; // Use text as key because Node Name might vary
				_cityButtons[key] = btn;

				// Connect signal (check if already connected to avoid dupes if re-run)
				if (!btn.IsConnected(Button.SignalName.Pressed, new Callable(this, nameof(OnCityPressedWrapper))))
				{
					// We need a wrapper to pass the name, or just use lambda but lambda is tricky with unregistering.
					// Simple Lambda:
					btn.Pressed += () => OnCityPressed(key);
				}
			}
		}

		UpdateCityVisuals(); // Apply DB data

		if (RouteOverlayNode != null)
		{
			var routes = GetRoutesFromDB();
			RouteOverlayNode.Init(this, routes);
		}
	}

	private void OnCityPressedWrapper() { } // Dummy for signal check if needed, but lambda above is fine for this scope.

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
		if (CityMenuDialog == null) return;

		CityMenuDialog.Init(cityName);
		CityMenuDialog.Show();
		// Position centering is handled by Anchors in scene now
	}

	private void ShowOfficerCard(int officerId)
	{
		if (OfficerCardDialog == null) return;
		OfficerCardDialog.Init(officerId);
		OfficerCardDialog.Show();
	}

	private void OnMenuClosed()
	{
		UpdateCityVisuals();
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
