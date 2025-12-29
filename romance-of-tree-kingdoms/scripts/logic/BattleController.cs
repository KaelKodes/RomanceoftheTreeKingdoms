using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class BattleController : Node2D
{
	private BattleManager _battleManager;
	private BattleGrid _grid;

	private List<UnitController> _units = new List<UnitController>();
	private List<ControlPoint> _controlPoints = new List<ControlPoint>();
	private UnitController _playerUnit; // Quick Ref to player
	private UnitController _selectedUnit; // For UI selection

	private PackedScene _unitScene;

	// Real-Time Control
	[Export] public float TimeScale = 1.0f;
	private bool _isPaused = false;
	private bool _battleEnded = false;
	private double _aiTimer = 0;

	public override void _Ready()
	{
		_battleManager = GetNodeOrNull<BattleManager>("/root/BattleManager");
		_grid = GetNodeOrNull<BattleGrid>("BattleGrid");
		_unitScene = GD.Load<PackedScene>("res://scenes/Unit.tscn");

		if (_battleManager == null || _grid == null || _unitScene == null)
		{
			GD.PrintErr("BattleController dependencies missing!");
			return;
		}

		// Initialize HUD
		var hud = new BattleHUD();
		AddChild(hud);
		hud.Init(this);

		StartBattle();
	}

	public override void _Process(double delta)
	{
		if (_isPaused || _battleEnded) return;

		float dt = (float)delta * TimeScale;

		if (dt <= 0) return;

		// Update all units
		foreach (var unit in _units)
		{
			unit.Tick(dt, _grid, _units, _controlPoints);
		}

		UpdateCPControl(dt);

		// AI Cycle (Every 1 second)
		_aiTimer += dt;
		if (_aiTimer >= 1.0f)
		{
			UpdateAITargets();
			_aiTimer = 0;
		}

		CheckWinCondition();
	}

	private void UpdateAITargets()
	{
		foreach (var unit in _units)
		{
			// Skip Player (Player controls their own focus)
			if (unit.OfficerData.IsPlayer) continue;

			// Skip dead units
			if (unit.CurrentHP <= 0) continue;

			// Find nearest active enemy
			var nearestEnemy = _units
				.Where(u => u.IsDefender != unit.IsDefender && u.CurrentHP > 0)
				.OrderBy(u => u.Position.DistanceSquaredTo(unit.Position))
				.FirstOrDefault();

			if (nearestEnemy != null)
			{
				// Update Focus (This makes them chase/attack)
				unit.SetFocus(nearestEnemy.GridPosition, _grid);
			}
		}
	}

	private void CheckWinCondition()
	{
		int attackersAlive = _units.Count(u => !u.IsDefender && u.CurrentHP > 0);
		int defendersAlive = _units.Count(u => u.IsDefender && u.CurrentHP > 0);

		if (attackersAlive == 0)
		{
			EndBattle(false); // Defenders Win
		}
		else if (defendersAlive == 0)
		{
			EndBattle(true); // Attackers Win
		}
	}

	private async void EndBattle(bool attackersWon)
	{
		_battleEnded = true;
		string winner = attackersWon ? "Attackers" : "Defenders";
		GD.Print($"Battle Over! {winner} Victory!");

		// Show Result UI (Placeholder)
		var label = new Label();
		label.Text = $"{winner} VICTORY!";
		label.Position = new Vector2(500, 300);
		label.Scale = new Vector2(3, 3);
		AddChild(label);

		// Wait 3 seconds
		await ToSignal(GetTree().CreateTimer(3.0f), SceneTreeTimer.SignalName.Timeout);

		// Resolve in Manager
		if (_battleManager != null)
		{
			_battleManager.ResolveBattle(attackersWon);
		}

		// Return to World
		GetTree().ChangeSceneToFile("res://scenes/WorldMap.tscn");
	}

	// Public API for HUD
	public void TogglePause()
	{
		_isPaused = !_isPaused;
		GD.Print($"Paused: {_isPaused}");
	}

	public void SetSpeed(float speed)
	{
		TimeScale = speed;
		GD.Print($"Speed set to: {TimeScale}x");
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey k && k.Pressed)
		{
			if (k.Keycode == Key.Space)
			{
				TogglePause();
				GetViewport().SetInputAsHandled();
			}
			else if (k.Keycode == Key.Equal) // +
			{
				SetSpeed(TimeScale + 0.5f);
			}
			else if (k.Keycode == Key.Minus) // -
			{
				SetSpeed(Math.Max(0.1f, TimeScale - 0.5f));
			}
		}

		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (_grid == null) return;
			Vector2I gridPos = _grid.WorldToGrid(GetGlobalMousePosition());

			if (mb.ButtonIndex == MouseButton.Left)
			{
				// Select Unit
				var clicked = _units.FirstOrDefault(u => u.GridPosition == gridPos);
				if (clicked != null)
				{
					_selectedUnit = clicked;
					GD.Print($"Selected: {clicked.OfficerData.Name}");
				}
			}
			else if (mb.ButtonIndex == MouseButton.Right)
			{
				// Order Player Unit (if selected or default?)
				// User wants to order "Their Character".
				if (_playerUnit != null)
				{
					GD.Print($"Order: Player -> {gridPos}");
					_playerUnit.SetFocus(gridPos, _grid);

					// Visual Feedback (e.g., spawn a flag)
				}
			}
		}
	}

	private void StartBattle()
	{
		var ctx = _battleManager.CurrentContext;
		if (ctx == null) return;

		GD.Print($"Starting Real-Time Battle for {ctx.CityName}!");

		// Generate CP Map
		_controlPoints = _grid.GenerateMapWithCPs(ctx.DefenderFactionId, ctx.AttackerFactionId);

		// Refresh CP Visuals based on Player Context (so Blue points show as Blue)
		// Assuming Player Faction is known or we deduce from Attacker/Defender roles
		// If Player is Attacker, Attacker Points (AttackerFactionId) should be Blue.
		bool playerIsAttacker = ctx.AttackerOfficers.Any(o => o.IsPlayer);
		int allyFaction = playerIsAttacker ? ctx.AttackerFactionId : ctx.DefenderFactionId;

		foreach (var cp in _controlPoints)
		{
			if (cp.OwnerFactionId == allyFaction) cp.UpdateVisuals(1); // Ally
			else if (cp.OwnerFactionId != 0) cp.UpdateVisuals(-1); // Enemy
			else cp.UpdateVisuals(0);
		}

		SpawnUnits(ctx, _controlPoints);

		// Initial Orders (Dumb AI)
		foreach (var u in _units)
		{
			if (u.OfficerData.IsPlayer)
			{
				_playerUnit = u;
				continue; // Player waits for input
			}

			// Simple AI: Charge the other side (For now, just Focus on Enemy HQ)
			var enemyHQ = _controlPoints.FirstOrDefault(c => c.Type == ControlPoint.CPType.HQ && c.OwnerFactionId != (u.IsDefender ? ctx.DefenderFactionId : ctx.AttackerFactionId));

			if (enemyHQ != null)
				u.SetFocus(enemyHQ.GridPosition, _grid);
		}
	}

	private void SpawnUnits(BattleContext ctx, List<ControlPoint> cps)
	{
		_units.Clear();
		if (_unitScene == null) return;

		// Determine Player Side
		bool playerIsAttacker = ctx.AttackerOfficers.Any(o => o.IsPlayer);
		bool playerIsDefender = ctx.DefenderOfficers.Any(o => o.IsPlayer);

		// Find HQs
		var defHQ = cps.FirstOrDefault(c => c.Type == ControlPoint.CPType.HQ && c.OwnerFactionId == ctx.DefenderFactionId);
		var attHQ = cps.FirstOrDefault(c => c.Type == ControlPoint.CPType.HQ && c.OwnerFactionId == ctx.AttackerFactionId);

		var rng = new Random();

		// Spawn Defenders
		foreach (var def in ctx.DefenderOfficers)
		{
			Vector2I spawnPos = defHQ != null ? GetRandomSpawnPos(defHQ.GridPosition, rng) : new Vector2I(2, 5);
			SpawnUnit(def, spawnPos, true, playerIsDefender);
		}

		// Spawn Attackers
		foreach (var att in ctx.AttackerOfficers)
		{
			Vector2I spawnPos = attHQ != null ? GetRandomSpawnPos(attHQ.GridPosition, rng) : new Vector2I(17, 5);
			SpawnUnit(att, spawnPos, false, playerIsAttacker);
		}
	}

	private Vector2I GetRandomSpawnPos(Vector2I center, Random rng)
	{
		// Try to find a walkable spot near HQ
		for (int i = 0; i < 10; i++)
		{
			int x = center.X + rng.Next(-2, 3);
			int y = center.Y + rng.Next(-2, 3);
			Vector2I candidate = new Vector2I(x, y);
			if (_grid.IsWalkable(candidate)) return candidate;
		}
		return center; // Fallback
	}

	private void SpawnUnit(BattleOfficer officer, Vector2I gridPos, bool isDefender, bool isAlly)
	{
		var unitNode = _unitScene.Instantiate<UnitController>();
		AddChild(unitNode);
		unitNode.Initialize(officer, isDefender, isAlly);
		unitNode.SetGridPosition(gridPos, _grid.GridToWorld(gridPos)); // Initialize Pos



		_units.Add(unitNode);

		_units.Add(unitNode);
	}

	private void HandleCPCapture(UnitController unit, ControlPoint cp)
	{
		// Update Visuals
		cp.UpdateVisuals(unit.IsAlly ? 1 : -1);

		// Decision Logic
		if (unit.OfficerData.IsPlayer)
		{
			ShowCPTypeDialog(cp);
		}
		else
		{
			// AI Random Choice
			var types = new[] { ControlPoint.CPType.SupplyDepot, ControlPoint.CPType.Outpost };
			var choice = types[new Random().Next(types.Length)];
			cp.SetType(choice);
		}
	}

	private ConfirmationDialog _activeDialog;

	private void ShowCPTypeDialog(ControlPoint cp)
	{
		if (_activeDialog != null) return; // Prevent multiple dialogs

		_isPaused = true; // Pause Game Loop

		_activeDialog = new ConfirmationDialog();
		_activeDialog.Title = "Control Point Captured!";
		_activeDialog.DialogText = "Choose how to utilize this location:\n\nOK = Supply Point (Restores Resources)\nCancel = Outpost (Defensive Bonus)";
		_activeDialog.OkButtonText = "Supply Point";
		_activeDialog.CancelButtonText = "Outpost";

		_activeDialog.Confirmed += () =>
		{
			cp.SetType(ControlPoint.CPType.SupplyDepot);
			_isPaused = false;
			_activeDialog.QueueFree();
			_activeDialog = null;
		};
		_activeDialog.Canceled += () =>
		{
			cp.SetType(ControlPoint.CPType.Outpost);
			_isPaused = false;
			_activeDialog.QueueFree();
			_activeDialog = null;
		};

		AddChild(_activeDialog);
		_activeDialog.PopupCentered();
	}
	private void UpdateCPControl(float dt)
	{
		// "King of the Hill" Logic
		// Each CP checks who has more units nearby.

		foreach (var cp in _controlPoints)
		{
			if (cp.IsDestroyed) continue;

			Dictionary<int, int> factionCounts = new Dictionary<int, int>();
			List<UnitController> capturers = new List<UnitController>();

			foreach (var unit in _units)
			{
				if (unit.CurrentHP <= 0) continue;

				int distManhattan = Math.Abs(cp.GridPosition.X - unit.GridPosition.X) + Math.Abs(cp.GridPosition.Y - unit.GridPosition.Y);
				if (distManhattan <= 1)
				{
					if (!factionCounts.ContainsKey(unit.OfficerData.FactionId))
						factionCounts[unit.OfficerData.FactionId] = 0;

					factionCounts[unit.OfficerData.FactionId]++;
					capturers.Add(unit);
				}
			}

			if (factionCounts.Count == 0) continue; // No one here

			// Find Winner
			var sorted = factionCounts.OrderByDescending(kv => kv.Value).ToList();
			int winnerFaction = sorted[0].Key;
			int winnerCount = sorted[0].Value;

			// Check for Tie (if more than 1 faction present)
			if (sorted.Count > 1 && sorted[1].Value == winnerCount)
			{
				// Tie! No change. Or Contested status?
				// For now, simple "Defender Holds" or "No Change".
				continue;
			}

			// Capture?
			if (cp.OwnerFactionId != winnerFaction)
			{
				// Valid Capture
				cp.SetOwner(winnerFaction);
				GD.Print($"Faction {winnerFaction} took control of {cp.Type} (Strength: {winnerCount})");

				// Find a representative unit for the dialogue logic
				var rep = capturers.First(u => u.OfficerData.FactionId == winnerFaction);
				HandleCPCapture(rep, cp);
			}
		}
	}
}
