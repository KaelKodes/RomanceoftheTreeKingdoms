using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class UnitController : Node2D
{
	// Visuals
	private Label _infoLabel;

	// Data
	public BattleOfficer OfficerData { get; private set; }
	public bool IsDefender { get; private set; }
	public bool IsAlly { get; private set; } // Is on player's side
	public Vector2I GridPosition { get; private set; }

	// Stats
	public int CurrentHP { get; private set; }
	public int MaxHP { get; private set; }
	public int Attack { get; private set; }

	// Real-Time Logic
	public enum UnitState { Idle, Moving, Attacking, Cooldown }
	public UnitState CurrentState { get; private set; } = UnitState.Idle;

	public Vector2I TargetGridPos { get; private set; }
	private Vector2 _visualTargetPos;
	private float _moveSpeed = 100.0f; // Pixels per second

	private float _attackCooldown = 0.0f;
	private float _attackSpeed = 1.0f; // Attacks per second

	public override void _Ready()
	{
		_infoLabel = GetNodeOrNull<Label>("Label");
		// Default target is self (stay put)
	}

	public override void _Draw()
	{
		// Procedural Drawing
		Color color = Colors.Red; // Default Enemy

		if (OfficerData != null)
		{
			if (OfficerData.IsPlayer) color = Colors.Green;
			else if (IsAlly) color = Colors.Blue;
		}

		if (CurrentHP <= 0) color = color.Darkened(0.5f);

		// Draw Circle (Radius 6 fits nicely in 16x16 tile)
		DrawCircle(Vector2.Zero, 6.0f, color);
	}

	public void Initialize(BattleOfficer officer, bool isDefender, bool isAlly)
	{
		OfficerData = officer;
		IsDefender = isDefender;
		IsAlly = isAlly;

		// Map Stats to Real-Time Stats
		MaxHP = 100 + (officer.Leadership * 2);
		CurrentHP = MaxHP;
		Attack = officer.Combat;

		// Higher Combat = Faster Attacks? Or Standardized?
		// Let's standardise for now: 1 attack / sec
		_attackSpeed = 1.0f + (officer.Combat / 200.0f); // 1.0 to 1.5 ish

		UpdateVisuals();
	}

	private void UpdateVisuals()
	{
		QueueRedraw(); // Triggers _Draw

		if (_infoLabel != null && OfficerData != null)
		{
			_infoLabel.Text = $"{OfficerData.Name}\n{CurrentHP}/{MaxHP}";
		}
	}

	public void SetGridPosition(Vector2I gridPos, Vector2 worldPos)
	{
		GridPosition = gridPos;
		TargetGridPos = gridPos;
		Position = worldPos;
		_visualTargetPos = worldPos;
		CurrentState = UnitState.Idle;
	}

	public void SetFocus(Vector2I targetPos)
	{
		TargetGridPos = targetPos;
		CurrentState = UnitState.Moving;
	}

	public void Tick(float delta, BattleGrid grid, List<UnitController> allUnits)
	{
		if (CurrentHP <= 0) return;

		// 1. Cooldown Handling
		if (_attackCooldown > 0)
		{
			_attackCooldown -= delta;
			if (CurrentState == UnitState.Cooldown && _attackCooldown <= 0)
				CurrentState = UnitState.Idle;
		}

		// 2. Logic Machine
		switch ((int)CurrentState)
		{
			case (int)UnitState.Idle:
			case (int)UnitState.Moving:
				HandleMovement(delta, grid, allUnits);
				break;
			case (int)UnitState.Attacking:
				// Animation logic would go here
				break;
		}

		// 3. Visual Smoothing
		if (Position.DistanceTo(_visualTargetPos) > 1.0f)
		{
			Position = Position.MoveToward(_visualTargetPos, _moveSpeed * delta);
		}
	}

	private void HandleMovement(float delta, BattleGrid grid, List<UnitController> allUnits)
	{
		// Are we at the visual target (Next Tile)?
		if (Position.DistanceTo(_visualTargetPos) <= 1.0f)
		{
			// We effectively arrived at the previous step.
			// Choose Next Step towards TargetGridPos

			if (GridPosition == TargetGridPos)
			{
				CurrentState = UnitState.Idle;
				return;
			}

			// Simple Pathfinding: Move 1 tile closer
			Vector2I direction = new Vector2I(0, 0);
			if (TargetGridPos.X > GridPosition.X) direction.X = 1;
			else if (TargetGridPos.X < GridPosition.X) direction.X = -1;
			else if (TargetGridPos.Y > GridPosition.Y) direction.Y = 1;
			else if (TargetGridPos.Y < GridPosition.Y) direction.Y = -1;

			Vector2I nextStep = GridPosition + direction;

			// Check Blockage
			var blocker = allUnits.FirstOrDefault(u => u.GridPosition == nextStep && u.CurrentHP > 0);
			if (blocker != null)
			{
				// Blocked!
				// If Enemy -> Attack!
				if (blocker.IsDefender != IsDefender)
				{
					TryAttack(blocker);
				}
				else
				{
					// Friendly Block -> Wait (Idle)
					CurrentState = UnitState.Idle;
				}
			}
			else if (grid.IsWalkable(nextStep))
			{
				// Move!
				GridPosition = nextStep;
				_visualTargetPos = grid.GridToWorld(nextStep);
				CurrentState = UnitState.Moving;
			}
		}
	}

	private void TryAttack(UnitController target)
	{
		if (_attackCooldown <= 0)
		{
			CurrentState = UnitState.Attacking;

			// Instant Hit for now
			GD.Print($"{OfficerData.Name} hits {target.OfficerData.Name}!");
			target.TakeDamage(Math.Max(1, Attack / 5)); // Lower dmg for realtime spam

			_attackCooldown = 1.0f / _attackSpeed;
			CurrentState = UnitState.Cooldown;
		}
	}

	public void TakeDamage(int amount)
	{
		CurrentHP -= amount;
		if (CurrentHP < 0) CurrentHP = 0;
		UpdateVisuals();

		if (CurrentHP == 0)
		{
			GD.Print($"{OfficerData.Name} has been defeated!");
			Modulate = new Color(0.5f, 0.5f, 0.5f, 0.5f); // Fade out
		}
	}
}
