using Godot;
using System;
using System.Linq;
using System.Collections.Generic;

public partial class BattleSetupUI : Control
{
	private BattleManager _battleManager;

	// UI Referneces
	private Label _titleLabel;
	private Label _objectiveLabel;

	private Control _leftCol;
	private Label _leftFactionLabel;
	private Control _leftCardContainer;

	private Control _rightCol;
	private Label _rightFactionLabel;
	private Control _rightCardContainer;

	private Button _joinAttackersBtn;
	private Button _joinDefendersBtn;
	private Button _startButton;
	private Button _passButton;
	private CheckBox _autoResolveCheck;

	private bool _choseAttackers = false;
	private bool _hasChosen = false;

	public override void _Ready()
	{
		_battleManager = GetNodeOrNull<BattleManager>("/root/BattleManager");
		if (_battleManager == null)
		{
			GD.PrintErr("BattleSetupUI: BattleManager not found!");
			return;
		}

		// Wiring
		var main = GetNode("MainPanel/VBox");
		_titleLabel = main.GetNode<Label>("Header/TitleLabel");
		_objectiveLabel = main.GetNode<Label>("Header/ObjectiveLabel");

		var cols = main.GetNode("Columns");
		_leftCol = cols.GetNode<Control>("LeftCol");
		_leftFactionLabel = _leftCol.GetNode<Label>("HeaderBox/FactionLabel");
		_leftCardContainer = _leftCol.GetNode<Control>("Scroll/CardContainer");

		_rightCol = cols.GetNode<Control>("RightCol");
		_rightFactionLabel = _rightCol.GetNode<Label>("HeaderBox/FactionLabel");
		_rightCardContainer = _rightCol.GetNode<Control>("Scroll/CardContainer");

		var actions = main.GetNode("ActionBar");
		_joinAttackersBtn = actions.GetNode<Button>("JoinAttackerBtn");
		_joinDefendersBtn = actions.GetNode<Button>("JoinDefenderBtn");
		_startButton = actions.GetNode<Button>("StartButton");
		_passButton = actions.GetNode<Button>("PassButton");

		_autoResolveCheck = main.GetNode<CheckBox>("AutoResolveCheck");

		// Signals
		_joinAttackersBtn.Pressed += () => SelectSide(true);
		_joinDefendersBtn.Pressed += () => SelectSide(false);
		_startButton.Pressed += OnStartPressed;
		_passButton.Pressed += OnPassPressed;

		// Initial Check (if opened immediately on load)
		if (Visible) Open();
	}

	public void Open()
	{
		Show();
		_choseAttackers = false;
		_hasChosen = false;

		RefreshUI();

		// Check if player is already assigned (e.g. from existing context) and autoselect
		if (_battleManager?.CurrentContext != null)
		{
			var ctx = _battleManager.CurrentContext;
			var p = ctx.AllOfficers.FirstOrDefault(x => x.IsPlayer);
			if (p != null)
			{
				if (ctx.AttackerOfficers.Any(x => x.IsPlayer)) SelectSide(true);
				else if (ctx.DefenderOfficers.Any(x => x.IsPlayer)) SelectSide(false);
			}
		}
	}

	private void RefreshUI()
	{
		if (_battleManager?.CurrentContext == null) return;
		var ctx = _battleManager.CurrentContext;

		_titleLabel.Text = $"Battle for {ctx.CityName}";
		_objectiveLabel.Text = ctx.Objective.Description;

		// Faction Labels
		_leftFactionLabel.Text = _battleManager.GetFactionName(ctx.AttackerFactionId);
		_rightFactionLabel.Text = _battleManager.GetFactionName(ctx.DefenderFactionId);

		// Populate Lists
		PopulateList(_leftCardContainer, ctx.AttackerOfficers);
		PopulateList(_rightCardContainer, ctx.DefenderOfficers);

		// Update Buttons
		_startButton.Disabled = !_hasChosen;
		if (_hasChosen)
		{
			_startButton.Text = _choseAttackers ? "FIGHT FOR ATTACKERS" : "DEFEND THE CITY";

			// Visual Feedback
			_joinAttackersBtn.Modulate = _choseAttackers ? Colors.Green : Colors.White;
			_joinDefendersBtn.Modulate = !_choseAttackers ? Colors.Green : Colors.White;
		}
		else
		{
			_startButton.Text = "CHOOSE A SIDE";
			_joinAttackersBtn.Modulate = Colors.White;
			_joinDefendersBtn.Modulate = Colors.White;
		}
	}

	private void PopulateList(Control container, System.Collections.Generic.List<BattleOfficer> officers)
	{
		// Clear old
		foreach (Node child in container.GetChildren()) child.QueueFree();

		foreach (var off in officers)
		{
			var card = CreateOfficerRow(off);
			container.AddChild(card);
		}
	}

	private Control CreateOfficerRow(BattleOfficer off)
	{
		var panel = new PanelContainer();
		// Style
		var style = new StyleBoxFlat();
		style.BgColor = new Color(0.2f, 0.2f, 0.2f, 0.5f);
		style.CornerRadiusTopLeft = 5;
		style.CornerRadiusTopRight = 5;
		style.CornerRadiusBottomLeft = 5;
		style.CornerRadiusBottomRight = 5;
		if (off.IsPlayer)
		{
			style.BgColor = new Color(0.3f, 0.3f, 0.1f, 0.6f); // Gold tint
			style.BorderWidthBottom = 2;
			style.BorderColor = Colors.Gold;
		}
		panel.AddThemeStyleboxOverride("panel", style);

		var hbox = new HBoxContainer();
		hbox.CustomMinimumSize = new Vector2(0, 40); // Height
		panel.AddChild(hbox);

		// Spacer
		var spacer = new Control();
		spacer.CustomMinimumSize = new Vector2(10, 0);
		hbox.AddChild(spacer);

		// Name
		var nameLbl = new Label();
		nameLbl.Text = off.Name;
		nameLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		if (off.IsPlayer) nameLbl.Modulate = Colors.Gold;
		else if (off.Rank == "Sovereign" || off.Rank == "Commander") nameLbl.Modulate = new Color(1, 0.6f, 0.6f); // Reddish for commanders
		hbox.AddChild(nameLbl);

		// Stats
		var statsLbl = new Label();
		statsLbl.Text = $"Ldr:{off.Leadership} Str:{off.Strength}";
		statsLbl.Modulate = new Color(0.8f, 0.8f, 0.8f);
		statsLbl.CustomMinimumSize = new Vector2(120, 0);
		hbox.AddChild(statsLbl);

		// Troops
		var troopLbl = new Label();
		troopLbl.Text = $"{off.Troops} ({off.MainTroopType})";
		//troopLbl.Text = $"{off.Troops}";
		troopLbl.CustomMinimumSize = new Vector2(100, 0);
		troopLbl.HorizontalAlignment = HorizontalAlignment.Right;
		hbox.AddChild(troopLbl);

		// End Spacer
		var spacer2 = new Control();
		spacer2.CustomMinimumSize = new Vector2(10, 0);
		hbox.AddChild(spacer2);

		return panel;
	}

	private void SelectSide(bool attackers)
	{
		_hasChosen = true;
		_choseAttackers = attackers;

		if (_battleManager.CurrentContext != null)
		{
			var ctx = _battleManager.CurrentContext;
			var player = ctx.AllOfficers.FirstOrDefault(x => x.IsPlayer);

			if (player != null)
			{
				// Remove from both lists
				if (ctx.AttackerOfficers.Contains(player)) ctx.AttackerOfficers.Remove(player);
				if (ctx.DefenderOfficers.Contains(player)) ctx.DefenderOfficers.Remove(player);

				// Add to new side
				if (attackers) ctx.AttackerOfficers.Add(player);
				else ctx.DefenderOfficers.Add(player);

				// Update Objective text locally for feedback
				if (attackers) ctx.Objective.Description = "Assault the city and seize control!";
				else ctx.Objective.Description = "Repel the invaders and hold the city!";
			}
		}

		RefreshUI();
	}

	private void OnStartPressed()
	{
		Hide(); // Just hide, don't destroy

		if (_autoResolveCheck.ButtonPressed)
		{
			GD.Print("Battle Started (Auto-Resolve)! Simulating...");
			if (_battleManager != null && _battleManager.CurrentContext != null)
			{
				// Player is already in the lists (selected side), so just simulate
				_battleManager.SimulateBattle(_battleManager.CurrentContext.AttackerFactionId, _battleManager.CurrentContext.LocationId);
			}
		}
		else
		{
			GD.Print("Battle Started! Loading BattleMap...");
			GetTree().ChangeSceneToFile("res://scenes/BattleMap.tscn");
		}
	}

	private void OnPassPressed()
	{
		GD.Print("Player chose to PASS (Skip). Removing from lists and simulating...");
		Hide(); // Just hide, don't destroy

		if (_battleManager != null && _battleManager.CurrentContext != null)
		{
			var ctx = _battleManager.CurrentContext;
			var player = ctx.AllOfficers.FirstOrDefault(x => x.IsPlayer);
			if (player != null)
			{
				// Remove player from conflict entirely
				ctx.AllOfficers.Remove(player);
				ctx.AttackerOfficers.Remove(player);
				ctx.DefenderOfficers.Remove(player);
			}

			_battleManager.SimulateBattle(ctx.AttackerFactionId, ctx.LocationId);
		}
	}
}
