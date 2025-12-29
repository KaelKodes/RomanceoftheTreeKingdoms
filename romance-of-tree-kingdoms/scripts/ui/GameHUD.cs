using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class GameHUD : Control
{
	// UI References
	private Label _dateLabel;
	private Label _apLabel;
	private Button _endTurnButton;

	public override void _Ready()
	{
		// Adjust paths based on your scene tree
		_dateLabel = GetNode<Label>("MarginContainer/VBoxContainer/DateLabel");
		_apLabel = GetNode<Label>("MarginContainer/VBoxContainer/APLabel");

		// Attempt to find button (assuming it's in the VBox or similar, user can adjust if needed/or I use Export)
		// Ideally we use [Export] but since we are hardcoding paths based on user preference:
		_endTurnButton = GetNode<Button>("MarginContainer/VBoxContainer/EndDayButton");

		RefreshHUD();

		// Connect to ActionManager Signal
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.ActionPointsChanged += RefreshHUD;
		}

		// Connect to TurnManager Signals (and start system)
		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		if (turnMgr != null)
		{
			turnMgr.TurnStarted += OnTurnStarted;
			turnMgr.TurnEnded += OnTurnEnded;

			// Bootstrap the turn system if needed
			turnMgr.EnsureTurnSystemStarted();
		}
	}

	public override void _ExitTree()
	{
		var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
		if (actionMgr != null)
		{
			actionMgr.ActionPointsChanged -= RefreshHUD;
		}

		var turnMgr = GetNodeOrNull<TurnManager>("/root/TurnManager");
		if (turnMgr != null)
		{
			turnMgr.TurnStarted -= OnTurnStarted;
			turnMgr.TurnEnded -= OnTurnEnded;
		}
	}

	public void OnEndDayPressed()
	{
		// Old: actionMgr.EndDay();
		// New: Call TurnManager
		var turnMgr = GetNode<TurnManager>("/root/TurnManager");
		turnMgr.PlayerEndTurn();
		// RefreshHUD(); // Wait for signal?
	}

	private void OnTurnStarted(int factionId, bool isPlayer)
	{
		RefreshHUD();
		if (isPlayer)
		{
			_endTurnButton.Disabled = false;
			_endTurnButton.Text = "End Turn";
		}
		else
		{
			_endTurnButton.Disabled = true;
			_endTurnButton.Text = $"Faction {factionId} Moving...";
		}

		// Override text if it IS player turn
		if (isPlayer) _endTurnButton.Text = "End Turn (Player)";
	}

	private void OnTurnEnded()
	{
		_endTurnButton.Disabled = true;
	}

	public void RefreshHUD()
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();

			// 1. Get Player AP
			var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT current_action_points FROM officers WHERE is_player = 1";
			var ap = (long)cmd.ExecuteScalar();

			_apLabel.Text = $"AP: {ap}/3";

			// 2. Get Date
			var dateCmd = connection.CreateCommand();
			dateCmd.CommandText = "SELECT current_day FROM game_state LIMIT 1";
			var dayObj = dateCmd.ExecuteScalar();
			long totalDays = (dayObj != null && dayObj != DBNull.Value) ? (long)dayObj : 1;

			// Calculate Calendar
			// Assuming 30 days per month, 12 months per year
			long year = 1 + (totalDays - 1) / 360;
			long month = 1 + ((totalDays - 1) % 360) / 30;
			long day = 1 + ((totalDays - 1) % 30);

			_dateLabel.Text = $"Year {year}, Month {month}, Day {day}";
		}
	}
}
