using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class GameHUD : Control
{
	// UI References
	private Label _dateLabel;
	private Label _apLabel;
	private Label _goldLabel; // New
	private Label _factionLabel;
	private Label _rankLabel;
	private Label _troopsLabel;
	private Label _recordLabel; // New
	private Button _endTurnButton;

	public override void _Ready()
	{
		// Adjust paths based on your scene tree
		_dateLabel = GetNode<Label>("MarginContainer/VBoxContainer/DateLabel");
		_apLabel = GetNode<Label>("MarginContainer/VBoxContainer/APLabel");

		// Attempt to get GoldLabel (Safe check in case scene not updated yet)
		_goldLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/GoldLabel");

		_factionLabel = GetNode<Label>("MarginContainer/VBoxContainer/FactionLabel");
		_rankLabel = GetNode<Label>("MarginContainer/VBoxContainer/RankLabel");
		_troopsLabel = GetNode<Label>("MarginContainer/VBoxContainer/TroopsLabel");
		// New RecordLabel
		_recordLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/RecordLabel");

		// Attempt to find button (assuming it's in the VBox or similar, user can adjust if needed/or I use Export)
        // Ideally we use [Export] but since we are hardcoding paths based on user preference:
        _endTurnButton = GetNode<Button>("MarginContainer/VBoxContainer/EndDayButton");

        RefreshHUD();

        // Connect to ActionManager Signal
        var actionMgr = GetNodeOrNull<ActionManager>("/root/ActionManager");
        if (actionMgr != null)
        {
            actionMgr.ActionPointsChanged += RefreshHUD;
            // Also refresh on day start for new promo checks
            actionMgr.NewDayStarted += RefreshHUD;
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
            actionMgr.NewDayStarted -= RefreshHUD;
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
        using (var connection = DatabaseHelper.GetConnection())
        {
            connection.Open();

            // 1. Get Player AP and Extended Info
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    o.current_action_points, 
                    o.rank, 
                    o.max_troops, 
                    o.reputation, 
                    o.battles_won, 
                    o.battles_lost,
                    o.gold,
                    f.name as faction_name,
                    o.max_action_points,
                    o.troops
                FROM officers o
                LEFT JOIN factions f ON o.faction_id = f.faction_id
				WHERE o.is_player = 1";

            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    long ap = r.GetInt64(0);
                    string rank = r.GetString(1);
                    int maxTroops = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    int rep = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                    int wins = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                    int losses = r.IsDBNull(5) ? 0 : r.GetInt32(5);
                    int gold = r.IsDBNull(6) ? 0 : r.GetInt32(6); // Gold
                    string factionName = r.IsDBNull(7) ? "Ronin" : r.GetString(7);
                    int maxAP = r.IsDBNull(8) ? 3 : r.GetInt32(8);
                    int currentTroops = r.IsDBNull(9) ? 0 : r.GetInt32(9);

                    GD.Print($"[HUD Refresh] Rank: {rank}, Troops: {currentTroops}/{maxTroops}, Gold: {gold}, Rep: {rep}");

                    _apLabel.Text = $"AP: {ap}/{maxAP}";
                    if (_goldLabel != null) _goldLabel.Text = $"Gold: {gold}";
                    _factionLabel.Text = $"Faction: {factionName}";
                    _rankLabel.Text = $"Rank: {rank} (Rep: {rep})";
                    _troopsLabel.Text = $"Command: {currentTroops}/{maxTroops}";
                    if (_recordLabel != null) _recordLabel.Text = $"Record: {wins}W - {losses}L";
                }
            }

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
