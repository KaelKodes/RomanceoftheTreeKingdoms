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
    public int CurrentHP { get; private set; } // Troop Count
    public int MaxHP { get; private set; }

    public int OfficerHP { get; private set; }
    public int MaxOfficerHP { get; private set; }
    public int OfficerArmor { get; private set; }
    public int TroopArmor { get; private set; }
    public string CombatPosition { get; private set; }
    public int CurrentMorale { get; private set; }
    public TroopType OfficerType { get; private set; }

    // Real-Time Logic
    public enum UnitRole { Officer, Squad }
    public UnitRole Role { get; private set; } = UnitRole.Officer;
    public UnitController ParentOfficer { get; private set; } // Reference to leader if Squad

    public enum UnitState { Idle, Moving, Attacking, Cooldown, Siege, Retreat, Charging, Looping }
    public UnitState CurrentState { get; private set; } = UnitState.Idle;

    public Vector2I TargetGridPos { get; private set; }
    private Vector2 _visualTargetPos;
    private float _moveSpeed = 100.0f; // Pixels per second
    private List<Vector2I> _currentPath = new List<Vector2I>();
    private Vector2 _chargeExitPos;

    private float _attackCooldown = 0.0f;
    private float _attackSpeed = 1.0f; // Attacks per second
    private float _attackRange = 40.0f; // Basic melee (pixels)
    private float _minRange = 0.0f;

    private float _passageTimer = 0.0f;
	private const float PASSAGE_TICK_RATE = 3.0f; // Seconds per 'day' effect

    private SquadVisualizer _squadVisualizer;

    public override void _Ready()
    {
        _infoLabel = GetNodeOrNull<Label>("Label");
        _squadVisualizer = new SquadVisualizer();
        AddChild(_squadVisualizer);

        if (_infoLabel != null)
        {
            // Move it to be drawn LAST (on top of dots)
            MoveChild(_infoLabel, GetChildCount() - 1);
            _infoLabel.ZIndex = 10; // Explicitly on top layer

            // Reposition physically above the squad block (~64px height box)
            _infoLabel.Position = new Vector2(-100, -120); // Higher offset since pivot is now at center
            _infoLabel.Size = new Vector2(200, 40);
            _infoLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _infoLabel.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    public override void _Draw()
    {
        // Debug circle only
        if (CurrentHP <= 0 && Role == UnitRole.Squad) return;

        if (Role == UnitRole.Officer)
        {
            // Draw Hero Icon (Big Circle for now)
            DrawCircle(Vector2.Zero, 12.0f, IsAlly ? Colors.Cyan : Colors.Salmon);
            DrawArc(Vector2.Zero, 12.0f, 0, Mathf.Pi * 2, 16, Colors.White, 2.0f);
        }
        else
        {
            DrawCircle(Vector2.Zero, 2.0f, Colors.White); // Tiny pivot dot
        }
    }

    public void Initialize(BattleOfficer officer, bool isDefender, bool isAlly, UnitRole role = UnitRole.Officer, UnitController parent = null)
    {
        OfficerData = officer;
        IsDefender = isDefender;
        IsAlly = isAlly;
        Role = role;
        ParentOfficer = parent;

        // Initialize New Stats
        MaxOfficerHP = officer.MaxOfficerHP;
        OfficerHP = officer.OfficerHP;
        OfficerArmor = officer.OfficerArmor;
        TroopArmor = officer.TroopArmor;
        CombatPosition = officer.CombatPosition;
        CurrentMorale = officer.Morale;
        OfficerType = officer.OfficerType;

        MaxHP = officer.Troops;
        if (MaxHP <= 0) MaxHP = 100;
        CurrentHP = MaxHP;

        _attackSpeed = 1.0f + (officer.Strength / 200.0f);

        ApplyTroopTypeStats(officer.MainTroopType);

        _squadVisualizer?.Initialize(officer);

        // Hide Visualizer if Officer (Hero Mode)
        if (Role == UnitRole.Officer && _squadVisualizer != null)
        {
            _squadVisualizer.Visible = false;
        }

        UpdateVisuals();
    }

    private void ApplyTroopTypeStats(TroopType type)
    {
        switch (type)
        {
            case TroopType.Cavalry:
                _moveSpeed = 150.0f;
                break;
            case TroopType.Archer:
                _moveSpeed = 80.0f;
                _attackRange = 180.0f; // Ranged
                _minRange = 60.0f; // Penalty if too close
                break;
            case TroopType.Siege:
                _moveSpeed = 60.0f;
                _attackRange = 240.0f; // Long range bombardment
                _minRange = 80.0f;
                break;
            default:
                _moveSpeed = 100.0f;
                _attackRange = 40.0f;
                break;
        }

        // Officer Type overrides Positioning/Flavor
        if (OfficerType == TroopType.Archer)
        {
            CombatPosition = "Rear"; // Archer officers hang back
            if (_attackRange < 120.0f) _attackRange = 120.0f; // Even if leading infantry, he has a bow
        }
    }

    private void UpdateVisuals()
    {
        // QueueRedraw(); 
        if (_squadVisualizer != null)
        {
            _squadVisualizer.UpdateTroops(CurrentHP);
            // Selection updated in Tick
        }

        if (_infoLabel != null && OfficerData != null)
        {
            // Show Rank/Name and split HP
            string officerState = OfficerHP > 0 ? $"[{OfficerHP} HP]" : "[DEFEATED]";
            // Morale Icon/String
            string moraleStr = CurrentMorale > 70 ? "High" : (CurrentMorale < 30 ? "Low" : "Normal");
            _infoLabel.Text = $"{OfficerData.Name} {officerState}\nTroops: {CurrentHP} | Mor: {moraleStr}";

            // Color modulation based on side
            if (OfficerData.IsPlayer) _infoLabel.Modulate = Colors.Green;
            else if (IsAlly) _infoLabel.Modulate = Colors.Cyan;
            else _infoLabel.Modulate = Colors.Salmon;
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

    public void SetSquadStats(int troops)
    {
        MaxHP = troops;
        CurrentHP = troops;
        UpdateVisuals();
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
        // Visual Update (Cheap)
        bool isRouted = CurrentMorale <= 0 || OfficerHP <= 0;
        _squadVisualizer?.UpdateSelectionVisuals(IsAlly, this == BattleController.Instance?.SelectedUnit, OfficerData?.IsPlayer ?? false, isRouted);

        if (CurrentHP <= 0) return;

        // 0. Cavalry Special Logic
        if (OfficerData.MainTroopType == TroopType.Cavalry && CurrentState == UnitState.Moving)
        {
            ProcessCavalryLogic(allUnits);
        }

        // 1. Cooldown Handling
        if (_attackCooldown > 0)
        {
            _attackCooldown -= delta;
            if (CurrentState == UnitState.Cooldown && _attackCooldown <= 0)
                CurrentState = UnitState.Idle;
        }

        // 2. Passage of Time (Morale/Desertion)
        _passageTimer += delta;
        if (_passageTimer >= PASSAGE_TICK_RATE)
        {
            _passageTimer = 0;
            ProcessPassageEffects();
        }

        // 2b. Ranged Auto-Attack (Fire at Will)
        if (_attackCooldown <= 0 && (CurrentState == UnitState.Idle || CurrentState == UnitState.Moving))
        {
            ProcessRangedLogic(allUnits, allCPs);
        }

        // 3. Logic Machine
        switch ((int)CurrentState)
        {
            case (int)UnitState.Idle:
            case (int)UnitState.Moving:
            case (int)UnitState.Retreat:
            case (int)UnitState.Charging:
            case (int)UnitState.Looping:
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
            float activeSpeed = _moveSpeed;
            if (CurrentState == UnitState.Charging) activeSpeed *= 2.5f;
            else if (CurrentState == UnitState.Looping) activeSpeed *= 1.5f;

            Vector2 dir = (_visualTargetPos - Position).Normalized();
            Position = Position.MoveToward(_visualTargetPos, activeSpeed * delta);

            // Turn Squad
            _squadVisualizer?.SetFacing(dir);
        }

        // 4. Force Retreat/Rout Pathing
        if (CurrentMorale <= 0 || OfficerHP <= 0)
        {
            CurrentState = UnitState.Retreat;
            TargetGridPos = new Vector2I(IsDefender ? 0 : 30, 0); // Flee to edge
        }
    }

    private void ProcessPassageEffects()
    {
        if (CurrentHP <= 0 || OfficerHP <= 0) return;

        // Auto Morale Gain (slow)
        if (CurrentMorale > 0 && CurrentMorale < 100)
        {
            CurrentMorale += 1;
        }

        // Desertion
        if (CurrentMorale < 30)
        {
            int desertCount = (30 - CurrentMorale) * 2;
            GD.Print($"{OfficerData.Name} is suffering desertion! Soldiers fleeing: {desertCount}");
            CurrentHP -= desertCount;
            if (CurrentHP < 0) CurrentHP = 0;
            UpdateVisuals();
        }
    }
    private void HandleMovement(float delta, BattleGrid grid, List<UnitController> allUnits, List<ControlPoint> allCPs)
    {
        // Speed Multiplier for Charging
        float speedMult = (CurrentState == UnitState.Charging) ? 2.5f : 1.0f;

        // Are we at the visual target (Next Tile)?
        if (Position.DistanceTo(_visualTargetPos) <= 5.0f)
        {
            if (CurrentState == UnitState.Charging)
            {
                // Finished charging through! Now Loop out.
                CurrentState = UnitState.Looping;
                Vector2 moveAway = (Position - _visualTargetPos).Normalized() * 150.0f; // Continue momentum
                _visualTargetPos = Position + moveAway;
                return;
            }
            if (CurrentState == UnitState.Looping)
            {
                CurrentState = UnitState.Idle;
                _currentPath.Clear();
                return;
            }

            // Standard Pathing...
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

			// 2. Check Units (excluding self)
			var blocker = allUnits.FirstOrDefault(u => u.GridPosition == nextStep && u.CurrentHP > 0 && u != this);
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
		float dist = Position.DistanceTo(target.Position);
		if (dist > _attackRange) return;

		if (_attackCooldown <= 0)
		{
			CurrentState = UnitState.Attacking;

			// 1. Efficiency (Officer-Unit Synergy)
			float efficiency = GetEfficiencyMultiplier();

			// 2. Combat Math: RTK8 Style (Leadership Weighted)
			float moraleMult = CurrentMorale > 70 ? 1.2f : (CurrentMorale < 30 ? 0.5f : 1.0f);
			int baseAtk = (int)((OfficerData.Leadership + (CurrentHP / 200)) * moraleMult * efficiency);

			// 3. RPS Multiplier
			float rpsMult = GetRPSMultiplier(target);

			// 3b. Visual Projectile (Volley)
			bool isRanged = OfficerData.MainTroopType == TroopType.Archer || OfficerData.MainTroopType == TroopType.Siege || OfficerType == TroopType.Archer;
			if (isRanged && dist > 50.0f)
			{
				Color arrowColor = IsAlly ? Colors.DeepSkyBlue : Colors.IndianRed;

				if (OfficerData.MainTroopType == TroopType.Siege)
				{
					// Siege: 1-3 Heavy Bolts
					ProjectileManager.Instance?.LaunchVolley(Position, target.Position, arrowColor, 3, 10.0f);
				}
				else
				{
					// Archer Volley (Scale with troops)
					int volleyCount = Math.Clamp(CurrentHP / 40, 5, 25);
					ProjectileManager.Instance?.LaunchVolley(Position, target.Position, arrowColor, volleyCount, 30.0f);
				}
			}

			// 4. Ranged Penalty (Minimum Range)
			float rangeMult = 1.0f;
			if (dist < _minRange)
			{
				rangeMult = 0.4f; // Archers/Siege suffer in melee
				GD.Print($"{OfficerData.Name} is too close! Damage reduced.");
			}

			int finalAtk = (int)(baseAtk * rpsMult * rangeMult);

			// Damage = Atk - (Target DEF / 2)
			int targetDef = target.OfficerData.Leadership + (target.CurrentHP / 400);
			int damage = Math.Max(1, finalAtk - (targetDef / 2));

			GD.Print($"{OfficerData.Name} ({OfficerData.MainTroopType}) deals {damage} damage to {target.OfficerData.Name} ({target.OfficerData.MainTroopType})! [RPS: {rpsMult}]");

			bool wasAlive = target.OfficerHP > 0;
			target.TakeDamage(damage);

			if (wasAlive && target.OfficerHP <= 0)
			{
				int goldReward = target.OfficerData.Rank == "Minion" ? 25 : 200;
				BattleManager.Instance?.AwardGold(OfficerData.OfficerId, goldReward);
			}

			_attackCooldown = 1.0f / _attackSpeed;
			CurrentState = UnitState.Cooldown;
		}
	}

	private void ProcessCavalryLogic(List<UnitController> allUnits)
	{
		if (CurrentState != UnitState.Moving) return;

		// Look for enemy units to charge
		foreach (var unit in allUnits)
		{
			if (unit.IsAlly == this.IsAlly || unit.CurrentHP <= 0) continue;

			float dist = Position.DistanceTo(unit.Position);
			if (dist < 120.0f && dist > 40.0f)
			{
				// CHARGE!
				CurrentState = UnitState.Charging;
				GD.Print($"{OfficerData.Name} initiates a CAVALRY CHARGE!");

				// Target a point behind them
				Vector2 dir = (unit.Position - Position).Normalized();
				_visualTargetPos = unit.Position + (dir * 80.0f); // Overshoot them

				// Do a small initial impact
				unit.TakeDamage((int)(OfficerData.Leadership / 3));
				return;
			}
		}
	}

	private void ProcessRangedLogic(List<UnitController> allUnits, List<ControlPoint> allCPs)
	{
		bool isRanged = OfficerData.MainTroopType == TroopType.Archer || OfficerData.MainTroopType == TroopType.Siege || OfficerType == TroopType.Archer;
		if (!isRanged) return;

		// 1. Check for nearest enemy unit
		UnitController bestTarget = null;
		float bestDist = _attackRange;

		foreach (var u in allUnits)
		{
			if (u.IsAlly == this.IsAlly || u.CurrentHP <= 0) continue;
			float d = Position.DistanceTo(u.Position);
			if (d < bestDist)
			{
				bestDist = d;
				bestTarget = u;
			}
		}

		if (bestTarget != null)
		{
			TryAttack(bestTarget);
			return;
		}

		// 2. If no units, check for enemy structures (Gates/HQs)
		if (allCPs != null)
		{
			ControlPoint bestCP = null;
			bestDist = _attackRange + 20; // Structural buffer

			foreach (var cp in allCPs)
			{
				// ONLY ATTACK GATES. All other CPs are capture-only.
				if (cp.IsDestroyed || cp.Type != ControlPoint.CPType.Gate) continue;

				// Attack if it's an enemy structure
                bool isEnemy = (IsDefender && cp.OwnerFactionId != OfficerData.FactionId) || (!IsDefender && cp.OwnerFactionId != 0);
                if (!isEnemy) continue;

                float d = Position.DistanceTo(((Vector2)cp.GridPosition) * 64.0f + new Vector2(32, 32));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestCP = cp;
                }
            }

            if (bestCP != null)
            {
                TryAttack(bestCP);
            }
        }
    }

    private float GetRPSMultiplier(UnitController target)
    {
        TroopType myType = OfficerData.MainTroopType;
        TroopType theirType = target.OfficerData.MainTroopType;

        // Advantage Matrix: 1.3x damage
        if (myType == TroopType.Cavalry && theirType == TroopType.Infantry) return 1.3f;
        if (myType == TroopType.Infantry && theirType == TroopType.Archer) return 1.3f;
        if (myType == TroopType.Archer && theirType == TroopType.Cavalry) return 1.3f;

        // Siege Disadvantage vs Units
        if (myType == TroopType.Siege) return 0.5f;

        return 1.0f;
    }

    private float GetEfficiencyMultiplier()
    {
        // Simple Requirement Logic
        // Cavalry: Needs STR 60
        // Archer: Needs INT 60
        // Elite: Needs LEA 80
        // Siege: Needs INT 50

        float efficiency = 1.0f;
        switch (OfficerData.MainTroopType)
        {
            case TroopType.Cavalry:
                if (OfficerData.Strength < 60) efficiency = 0.6f + (OfficerData.Strength / 150.0f); // ~0.7-0.9
                break;
            case TroopType.Archer:
                if (OfficerData.Intelligence < 60) efficiency = 0.6f + (OfficerData.Intelligence / 150.0f);
                break;
            case TroopType.Elite:
                if (OfficerData.Leadership < 80) efficiency = 0.5f + (OfficerData.Leadership / 160.0f);
                break;
        }
        return Math.Min(1.0f, efficiency);
    }

    private void TryAttack(ControlPoint target)
    {
        Vector2 targetPos = new Vector2(target.GridPosition.X, target.GridPosition.Y) * 64.0f + new Vector2(32, 32);
        float dist = Position.DistanceTo(targetPos);
        if (dist > _attackRange + 20) return; // Small buffer for structures

        if (_attackCooldown <= 0)
        {
            CurrentState = UnitState.Siege;

            GD.Print($"{OfficerData.Name} bashes the Gate!");
            int damage = Math.Max(1, OfficerData.Leadership / 5);

            // Siege Multiplier vs Structures
            if (OfficerData.MainTroopType == TroopType.Siege) damage *= 4;

            target.TakeDamage(damage);

            // Visual Projectile for Siege
            bool isRanged = OfficerData.MainTroopType == TroopType.Siege || OfficerData.MainTroopType == TroopType.Archer || OfficerType == TroopType.Archer;
            if (isRanged && dist > 50.0f)
            {
                Vector2 targetPosCenter = new Vector2(target.GridPosition.X, target.GridPosition.Y) * 64.0f + new Vector2(32, 32);
                Color arrowColor = IsAlly ? Colors.DeepSkyBlue : Colors.IndianRed;

                // Siege Volley
                ProjectileManager.Instance?.LaunchVolley(Position, targetPosCenter, arrowColor, 5, 15.0f);
            }

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
        // 1. Armor Reduction
        float reduction = 1.0f + (CurrentHP > 0 ? TroopArmor : OfficerArmor) / 50.0f;
        int realDamage = (int)Math.Max(1, amount / reduction);

        // 2. Siege Weakness vs General Units
        if (OfficerData.MainTroopType == TroopType.Siege) realDamage = (int)(realDamage * 1.5f);

        // 3. Damage Splitting
        // 90% to Troops, 10% to Officer (while troops exist)
        if (CurrentHP > 0)
        {
            int troopDamage = (int)(realDamage * 0.9f);
            int officerDamage = Math.Max(1, realDamage - troopDamage);

            CurrentHP -= troopDamage;
            OfficerHP -= officerDamage;
        }
        else
        {
            // Direct hit to officer
            OfficerHP -= realDamage;
        }

        if (CurrentHP < 0) CurrentHP = 0;
        if (OfficerHP < 0) OfficerHP = 0;

        // Morale Impact
        CurrentMorale -= (int)(realDamage / 5);
        if (CurrentMorale < 0) CurrentMorale = 0;

        UpdateVisuals();

        if (OfficerHP <= 0 || CurrentMorale <= 0)
        {
            if (CurrentMorale <= 0) GD.Print($"{OfficerData.Name} has ROUTED!");
            else GD.Print($"{OfficerData.Name} has been defeated!");

            Modulate = new Color(0.5f, 0.5f, 0.5f, 0.5f); // Fade out
                                                          // Handle formal retreat or removal?
        }
    }
}
