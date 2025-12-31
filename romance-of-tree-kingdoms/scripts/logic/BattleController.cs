using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class BattleController : Node2D
{
	public static BattleController Instance { get; private set; }

	private BattleManager _battleManager;
	private BattleGrid _grid;

	public UnitController SelectedUnit => _selectedUnit;

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
		Instance = this;
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

		// Initialize Environment Layer
		var pm = new ProjectileManager();
		AddChild(pm);

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

		// 1. Sync HP back to Troops for literal persistence
		foreach (var unit in _units)
		{
			if (unit.OfficerData != null)
			{
				unit.OfficerData.Troops = Math.Max(0, unit.CurrentHP);
				GD.Print($"[Battle] {unit.OfficerData.Name} ends with {unit.OfficerData.Troops} troops.");
			}
		}

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

		GD.Print($"Starting Real-Time Battle for {ctx.CityName} (Def: {ctx.CityDefense})!");

		// Generate CP Map (Siege Mode)
		// Check if Siege: City Battle (Location > 0) is inherently a siege in this engine for now
		// We can check if CityDefense > 0 or just always use SiegeMap for consistency in City Battles
		_controlPoints = _grid.GenerateSiegeMap(ctx.DefenderFactionId, ctx.AttackerFactionId, ctx.CityDefense);

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

		// --- NEW AI ORDERS ---
		var gate = _controlPoints.FirstOrDefault(c => c.Type == ControlPoint.CPType.Gate);
		var attHQ = _controlPoints.FirstOrDefault(c => c.Type == ControlPoint.CPType.HQ && c.OwnerFactionId == ctx.AttackerFactionId);
		var defHQ = _controlPoints.FirstOrDefault(c => c.Type == ControlPoint.CPType.HQ && c.OwnerFactionId == ctx.DefenderFactionId);

		foreach (var u in _units)
		{
			if (u.OfficerData.IsPlayer)
			{
				_playerUnit = u;
				continue; // Player waits for input
			}

			if (u.IsDefender)
			{
				// DEFENDER LOGIC: Forward Defense
				// Position in front of the Gate (towards Attacker HQ)
				if (gate != null && !gate.IsDestroyed)
				{
					// Move to Gate Position + Offset towards Attackers
					// Attacker HQ is at X ~ 27, Gate at X ~ 10. Defenders should be at X ~ 12-14.
					// Simple logic: Target Gate, but stop short? Or Target point in front?
					// Let's set target to Gate for now, but UnitAI will need to know to "Defend" it.
					// For now, let's target the Attacker HQ but stop at the Gate?
					// BETTER: Target the GATE. UnitController "Defend" state will handle positioning.
					u.SetFocus(gate.GridPosition, _grid);
				}
				else
				{
					// Fallback: Charge
					if (attHQ != null) u.SetFocus(attHQ.GridPosition, _grid);
				}
			}
			else
			{
				// ATTACKER LOGIC: Siege Breaker
				// Target Gate first
				if (gate != null && !gate.IsDestroyed)
				{
					u.SetFocus(gate.GridPosition, _grid);
				}
				else
				{
					// Target HQ
					if (defHQ != null) u.SetFocus(defHQ.GridPosition, _grid);
				}
			}
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
			if (_grid.IsWalkable(candidate) && !_units.Any(u => u.GridPosition == candidate)) return candidate;
		}
		return center; // Fallback
	}

	private void SpawnUnit(BattleOfficer officer, Vector2I gridPos, bool isDefender, bool isAlly)
	{
		// 1. Spawn Officer (Hero)
		var officerUnit = _unitScene.Instantiate<UnitController>();
		AddChild(officerUnit);
		officerUnit.Initialize(officer, isDefender, isAlly, UnitController.UnitRole.Officer, null);
		officerUnit.SetGridPosition(gridPos, _grid.GridToWorld(gridPos));
		officerUnit.SetSquadStats(1); // Officer is 1 hero unit
		_units.Add(officerUnit);

		// 2. Spawn Squads (Troops)
		int totalTroops = officer.Troops;
		if (totalTroops <= 0) return;

		// 1 Squad per 150 troops, Max 6
		int squadCount = Math.Clamp(totalTroops / 150, 1, 6);
		int troopsPerSquad = totalTroops / squadCount;

		var rng = new Random();
		for (int i = 0; i < squadCount; i++)
		{
			// Find spot near Officer
			Vector2I squadPos = GetRandomSpawnPos(gridPos, rng);

			var squadUnit = _unitScene.Instantiate<UnitController>();
			AddChild(squadUnit);
			squadUnit.Initialize(officer, isDefender, isAlly, UnitController.UnitRole.Squad, officerUnit);
			squadUnit.SetGridPosition(squadPos, _grid.GridToWorld(squadPos));
			squadUnit.SetSquadStats(troopsPerSquad);

			_units.Add(squadUnit);
		}
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
				cp.Capture(winnerFaction); // Use new method with visuals
				GD.Print($"Faction {winnerFaction} took control of {cp.Type} (Strength: {winnerCount})");

				// Find a representative unit for the dialogue logic
				var rep = capturers.First(u => u.OfficerData.FactionId == winnerFaction);
				HandleCPCapture(rep, cp);
			}
		}
	}
}
