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
    public enum UnitState { Idle, Moving, Attacking, Cooldown, Siege, Retreat }
    public UnitState CurrentState { get; private set; } = UnitState.Idle;

    public Vector2I TargetGridPos { get; private set; }
    private Vector2 _visualTargetPos;
    private float _moveSpeed = 100.0f; // Pixels per second
    private List<Vector2I> _currentPath = new List<Vector2I>();

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
        // FIX: Use actual Troop Count as HP
        MaxHP = officer.Troops;
		// Fallback for safety if Troops is 0 (shouldn't happen but...)
		if (MaxHP <= 0) MaxHP = 100;

		CurrentHP = MaxHP;
		Attack = officer.Strength;

		// Higher Strength = Faster Attacks? Or Standardized?
		// Let's standardise for now: 1 attack / sec
        _attackSpeed = 1.0f + (officer.Strength / 200.0f); // 1.0 to 1.5 ish

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

    public void SetFocus(Vector2I targetPos, BattleGrid grid = null)
    {
        TargetGridPos = targetPos;
        CurrentState = UnitState.Moving;

        // Calculate Path if grid is available
        if (grid != null)
        {
            _currentPath = grid.GetPath(GridPosition, TargetGridPos);
        }
    }

    // Events
    public event Action<UnitController, ControlPoint> OnCPCaptured;

    public void Tick(float delta, BattleGrid grid, List<UnitController> allUnits, List<ControlPoint> allCPs = null)
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
            case (int)UnitState.Retreat:
                HandleMovement(delta, grid, allUnits, allCPs);
                break;
            case (int)UnitState.Attacking:
            case (int)UnitState.Siege:
                // Animation logic would go here
                break;
        }

        // 3. Visual Smoothing
        if (Position.DistanceTo(_visualTargetPos) > 1.0f)
        {
            Position = Position.MoveToward(_visualTargetPos, _moveSpeed * delta);
        }

        // 4. Retreat Logic
        if (CurrentState != UnitState.Retreat && CurrentHP < MaxHP * 0.2f && CurrentHP > 0)
        {
            // Check morale/surroundings
            // Simple: If low HP, retreat to HQ
            // Find Friendly HQ
            if (allCPs != null)
            {
                var hq = allCPs.FirstOrDefault(c => c.Type == ControlPoint.CPType.HQ && c.OwnerFactionId == (IsDefender ? OfficerData.FactionId : -999));
                // Note: Attacker Faction ID handling might be tricky here, assume FactionId is correct or check IsDefender
                // Simplified: Defender -> Defender HQ. Attacker -> Attacker HQ?
				// Actually passing the faction ID context is better, but for now let's assume retreat to "Start"

				// For simplicity: If Critical, Retreat away from enemies? Or just to start pos?
				// Let's mark state RETREAT and set target to 0,0 or map edge?
                CurrentState = UnitState.Retreat;
                // Determine retreat target in HandleMovement or set here if we had access to HQ list easily
            }
        }
    }
    private void HandleMovement(float delta, BattleGrid grid, List<UnitController> allUnits, List<ControlPoint> allCPs)
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

            // Pathfinding Logic
            Vector2I nextStep = GridPosition;

            // 1. Check if we have a path
            if (_currentPath != null && _currentPath.Count > 0)
            {
                nextStep = _currentPath[0];
            }
            else
            {
                // Fallback to simple request if path is missing (should verify with SetFocus)
                // Or try to recalc?
                _currentPath = grid.GetPath(GridPosition, TargetGridPos);
                if (_currentPath != null && _currentPath.Count > 0)
                    nextStep = _currentPath[0];
            }

            // If still no path or nextStep is same (stuck?), try simple direction (fallback)
            if (nextStep == GridPosition)
            {
                Vector2I direction = new Vector2I(0, 0);
                if (TargetGridPos.X > GridPosition.X) direction.X = 1;
                else if (TargetGridPos.X < GridPosition.X) direction.X = -1;
                else if (TargetGridPos.Y > GridPosition.Y) direction.Y = 1;
                else if (TargetGridPos.Y < GridPosition.Y) direction.Y = -1;
                nextStep = GridPosition + direction;
            }

            // Check Blockage (Units & Walls)
            // 1. Check Siege Walls (Gates)
            ControlPoint wall = null;
            if (allCPs != null)
            {
                wall = allCPs.FirstOrDefault(c => c.GridPosition == nextStep && c.Type == ControlPoint.CPType.Gate && !c.IsDestroyed);
            }

            if (wall != null)
            {
                // Wall Block!
                // If Enemy Wall -> Siege!
                bool isEnemyWall = (IsDefender && wall.OwnerFactionId != OfficerData.FactionId) || (!IsDefender && wall.OwnerFactionId != 0); // Simplified
                                                                                                                                              // Better: Compare OwnerFactionId. If I am Defender (Faction A), Wall is Faction A -> Friendly.

				// Siege Logic: Attack if it's not mine
				if (wall.OwnerFactionId != OfficerData.FactionId)
				{
					TryAttack(wall);
				}
				else
				{
					CurrentState = UnitState.Idle; // Wait behind friendly gate
				}
				return;
			}

			// 2. Check Units
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

				// Consume Path Step
				if (_currentPath != null && _currentPath.Count > 0 && _currentPath[0] == nextStep)
				{
					_currentPath.RemoveAt(0);
				}
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
			int damage = Math.Max(1, Attack / 5);

			bool wasAlive = target.CurrentHP > 0;
			target.TakeDamage(damage); // Lower dmg for realtime spam

			if (wasAlive && target.CurrentHP <= 0)
			{
				// Target Killed!
				int goldReward = target.OfficerData.Rank == "Minion" ? 25 : 200;
				BattleManager.Instance?.AwardGold(OfficerData.OfficerId, goldReward);
			}

			_attackCooldown = 1.0f / _attackSpeed;
			CurrentState = UnitState.Cooldown;
		}
	}

	private void TryAttack(ControlPoint target)
	{
		if (_attackCooldown <= 0)
		{
			CurrentState = UnitState.Siege;

			GD.Print($"{OfficerData.Name} bashes the Gate!");
			int damage = Math.Max(1, Attack / 5);

			target.TakeDamage(damage);

			if (target.IsDestroyed)
			{
				GD.Print("The Gate has been breached!");
				CurrentState = UnitState.Idle;
			}

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
