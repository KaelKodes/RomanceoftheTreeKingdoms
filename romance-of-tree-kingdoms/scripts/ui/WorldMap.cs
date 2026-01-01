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
	[Export] public OfficerCard OfficerCardDialog { get; set; }
	[Export] public RouteOverlay RouteOverlayNode { get; set; }
	[Export] public CouncilUI CouncilUIDialog { get; set; }

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

	private AttackOverlay _attackOverlay;

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
		// Redraw Attacks
		if (_attackOverlay != null)
		{
			_attackOverlay.QueueRedraw();
		}
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.Pressed && mb.ButtonIndex == MouseButton.Left)
			{
				// If background is clicked, clear selected city
				HUD?.ClearSelectedCity();
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
		}
		// Pan (Global Drag with L+R)
		else if (@event is InputEventMouseMotion mm)
		{
			// Check if Left, Right, or Middle is pressed
			if ((mm.ButtonMask & MouseButtonMask.Left) != 0 ||
				(mm.ButtonMask & MouseButtonMask.Right) != 0 ||
				(mm.ButtonMask & MouseButtonMask.Middle) != 0)
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
		else
		{
			HUD.OfficerClicked += ShowOfficerCard;
		}

		// 2. Wire up Dialogs

		if (OfficerCardDialog != null)
		{
			OfficerCardDialog.Hide();
		}

		// Setup Attack Overlay
		_attackOverlay = new AttackOverlay();
		AddChild(_attackOverlay);
		_attackOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
		_attackOverlay.Init(this);

		// 3. Init Cities from Scene
		InitCityNodes();

		// 4. Listen for updates
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
			turnMgr.TurnStarted += OnTurnStarted;
			turnMgr.TurnEnded += UpdateCityVisuals;
			turnMgr.CouncilTriggered += OnCouncilTriggered;
		}

		// 5. Capture Initial Positions and Calc Centroid
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

	private void OnTurnStarted(int factionId, bool isPlayer)
	{
		UpdateCityVisuals();
		if (!isPlayer)
		{
			// Close menus if AI turn starts (prevents race conditions)
			OfficerCardDialog?.Hide();
		}
	}

	private void OnCouncilTriggered(int factionId, int cityId, int cpAmount)
	{
		GD.Print($"[WorldMap] Council Triggered! Faction: {factionId}, City: {cityId}, CP: {cpAmount}");
		if (CouncilUIDialog != null)
		{
			CouncilUIDialog.Open(factionId, cityId, cpAmount);
		}
		else
		{
			GD.PrintErr("CouncilUIDialog not linked in WorldMap!");
		}
	}

	public override void _ExitTree()
	{
		if (HUD != null) HUD.OfficerClicked -= ShowOfficerCard;
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
			turnMgr.TurnStarted -= OnTurnStarted;
			turnMgr.TurnEnded -= UpdateCityVisuals;
		}
	}

	private void OnNewDayStarted()
	{
		CheckForTravelContinuation();
	}

	private void CheckForTravelContinuation()
	{
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
					int playerId = reader.GetInt32(2);
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
				string key = btn.Text;
				_cityButtons[key] = btn;
				if (!btn.IsConnected(Button.SignalName.Pressed, new Callable(this, nameof(OnCityPressedWrapper))))
				{
					btn.Pressed += () => OnCityPressed(key);
				}
			}
		}
		UpdateCityVisuals();
		if (RouteOverlayNode != null)
		{
			var routes = GetRoutesFromDB();
			RouteOverlayNode.Init(this, routes);
		}
	}

	private void OnCityPressedWrapper() { }

	private List<CityData> GetCitiesFromDB()
	{
		var list = new List<CityData>();
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
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
						IsContested = reader.GetInt64(3) > 0
					});
				}
			}
		}
		return list;
	}

	private void UpdateCityVisuals()
	{
		var cities = GetCitiesFromDB();
		foreach (var c in cities)
		{
			if (_cityButtons.ContainsKey(c.Name))
			{
				var btn = _cityButtons[c.Name];
				if (!IsInstanceValid(btn)) continue;
				if (c.HasPlayer)
				{
					btn.Text = $"[P] {c.Name}";
					btn.Modulate = Colors.Gold;
				}
				else
				{
					btn.Text = c.Name;
					if (c.FactionColor != null) btn.Modulate = new Color(c.FactionColor);
					else btn.Modulate = Colors.White;
				}
				if (c.IsContested) btn.Text += " ⚔️";
			}
		}
		if (_attackOverlay != null)
		{
			var battles = GetPendingBattlesFromDB();
			_attackOverlay.UpdateAttacks(battles);
		}
	}

	private void OnCityPressed(string cityName)
	{
		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		if (turnMgr != null && !turnMgr.IsPlayerTurnActive)
		{
			GD.Print("Cannot interact: Waiting for AI Turns...");
			return;
		}
		GD.Print($"City Clicked: {cityName}");
		OpenCityMenu(cityName);
	}

	private void OpenCityMenu(string cityName)
	{
		int cityId = GetCityId(cityName);
		if (cityId != -1 && HUD != null)
		{
			HUD.UpdateSelectedCity(cityId);
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

	private List<AttackOverlay.AttackVisual> GetPendingBattlesFromDB()
	{
		var list = new List<AttackOverlay.AttackVisual>();
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();
			var cmd = connection.CreateCommand();
			cmd.CommandText = @"
                SELECT c_target.name, c_source.name, f.color
                FROM pending_battles pb
                JOIN cities c_target ON pb.location_id = c_target.city_id
                LEFT JOIN cities c_source ON pb.source_location_id = c_source.city_id
				LEFT JOIN factions f ON pb.attacker_faction_id = f.faction_id";
			using (var r = cmd.ExecuteReader())
			{
				while (r.Read())
				{
					string to = r.GetString(0);
					string from = r.IsDBNull(1) ? null : r.GetString(1);
					string colorHex = r.IsDBNull(2) ? "#FF0000" : r.GetString(2);
					if (!string.IsNullOrEmpty(from))
					{
						list.Add(new AttackOverlay.AttackVisual
						{
							FromCity = from,
							ToCity = to,
							FactionColor = new Color(colorHex)
						});
					}
				}
			}
		}
		return list;
	}
}
