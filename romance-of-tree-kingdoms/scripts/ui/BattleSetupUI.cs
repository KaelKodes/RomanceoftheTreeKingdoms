using Godot;
using System;
using System.Linq;

public partial class BattleSetupUI : Control
{
	private BattleManager _battleManager;

	private Label _titleLabel;
	private Label _objectiveLabel;
	private Label _attackersList;
	private Label _defendersList;

	private Button _joinAttackersBtn;
	private Button _joinDefendersBtn;
	private Button _startButton;

	private bool _choseAttackers = false;

	public override void _Ready()
	{
		GD.Print("BattleSetupUI: _Ready started.");
		_battleManager = GetNode<BattleManager>("/root/BattleManager");
		GD.Print($"BattleMgr: {_battleManager != null}");

		// Setup UI reference (assuming basic structure for now)
		// Root -> Panel -> VBox -> [Title, Objective, SidesHBox, StartBtn]
		// SidesHBox -> [AttackersVBox, DefendersVBox]

		// For prototyping, we'll create the UI elements in code if not found, 
		// but ideally this is a .tscn. I'll write defensive code assuming nodes exist or need simple hookup.

		// Updated paths based on User's Latest MarginContainer Structure
		_titleLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/TitleLabel");
		_objectiveLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/ObjectiveLabel");

		_joinAttackersBtn = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/HBoxContainer/AttackersContainer/JoinButton");
		_joinDefendersBtn = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/HBoxContainer/DefendersContainer/JoinButton");

		_attackersList = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/HBoxContainer/AttackersContainer/ListLabel");
		_defendersList = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/HBoxContainer/DefendersContainer/ListLabel");

		_startButton = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/StartButton");

		// DEBUG PRINTS
		GD.Print($"TitleLbl: {_titleLabel != null}");
		GD.Print($"ObjectiveLbl: {_objectiveLabel != null}");
		GD.Print($"JoinAttBtn: {_joinAttackersBtn != null}");
		GD.Print($"JoinDefBtn: {_joinDefendersBtn != null}");
		GD.Print($"AttackersList: {_attackersList != null}");
		GD.Print($"DefendersList: {_defendersList != null}");
		GD.Print($"StartBtn: {_startButton != null}");

		if (_joinAttackersBtn != null) _joinAttackersBtn.Pressed += () => SelectSide(true);
		else GD.PrintErr("JoinAttackersBtn is NULL - Check Path!");

		if (_joinDefendersBtn != null) _joinDefendersBtn.Pressed += () => SelectSide(false);
		else GD.PrintErr("JoinDefendersBtn is NULL - Check Path!");

		if (_startButton != null)
		{
			_startButton.Pressed += OnStartPressed;
			_startButton.Disabled = true; // Must pick side first
		}
		else GD.PrintErr("StartButton is NULL - Check Path!");

		RefreshUI();
	}

	private void RefreshUI()
	{
		if (_battleManager == null)
		{
			GD.PrintErr("BattleSetupUI: _battleManager is NULL! Cannot refresh UI.");
			if (_objectiveLabel != null) _objectiveLabel.Text = "Error: Battle Manager not found.";
			return;
		}

		if (_battleManager.CurrentContext == null)
		{
			GD.PrintErr("BattleSetupUI: CurrentContext is NULL! Cannot refresh.");
			if (_objectiveLabel != null) _objectiveLabel.Text = "No Battle Context Found";
			return;
		}

		GD.Print("BattleSetupUI: Refreshing Data...");
		var ctx = _battleManager.CurrentContext;
		GD.Print($"CurrentContext found: {ctx != null}");

		if (_titleLabel != null) _titleLabel.Text = $"Battle for {ctx.CityName}";
		if (_objectiveLabel != null) _objectiveLabel.Text = $"Objective: {ctx.Objective.Description}";

		// List Officers
		if (_attackersList != null)
		{
			string txt = "Attackers:\n";
			foreach (var o in ctx.AttackerOfficers) txt += $"- {o.Name} (Ldr:{o.Leadership}, Cbt:{o.Combat})\n";
			_attackersList.Text = txt;
		}

		if (_defendersList != null)
		{
			string txt = "Defenders:\n";
			foreach (var o in ctx.DefenderOfficers) txt += $"- {o.Name} (Ldr:{o.Leadership}, Cbt:{o.Combat})\n";
			_defendersList.Text = txt;
		}
	}

	private void SelectSide(bool attackers)
	{
		_choseAttackers = attackers;
		GD.Print($"Player chose {(attackers ? "Attackers" : "Defenders")}");

		// Logic: Move Player to the chosen list
		if (_battleManager.CurrentContext != null)
		{
			var ctx = _battleManager.CurrentContext;
			// Find Player
			var playerObj = ctx.AllOfficers.FirstOrDefault(o => o.IsPlayer);
			if (playerObj != null)
			{
				// Remove from both lists first to avoid duplicates
				ctx.AttackerOfficers.Remove(playerObj);
				ctx.DefenderOfficers.Remove(playerObj);

				// Add to target list
				if (attackers) ctx.AttackerOfficers.Add(playerObj);
				else ctx.DefenderOfficers.Add(playerObj);

				// Update Objective Text based on side?
				// Simple toggle for now
				if (attackers) ctx.Objective.Description = "Capture the City! Defeat enemy commander.";
				else ctx.Objective.Description = "Defend the City! Repel invaders.";

				RefreshUI(); // Update the lists visually
			}
		}

		if (_startButton != null)
		{
			_startButton.Disabled = false;
			_startButton.Text = $"Confirm & Fight for {(attackers ? "Attackers" : "Defenders")}";
		}
	}

	private void OnStartPressed()
	{
		GD.Print("Battle Started! Loading BattleMap...");

		// 1. Mark Player as "In Battle" (maybe consume all AP?)
		// var am = GetNode<ActionManager>("/root/ActionManager");
		// am.ConsumeAllAP(); 

		// 2. Do NOT End Turn yet. The battle IS the turn (or part of it).
		// var turnMgr = GetNode<TurnManager>("/root/TurnManager");
		// turnMgr.PlayerEndTurn();

		// 3. Load the Battle Map
		GetTree().ChangeSceneToFile("res://scenes/BattleMap.tscn");

		QueueFree(); // Close the setup UI
	}
}
