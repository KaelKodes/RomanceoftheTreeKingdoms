using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class CharacterCreation : Control
{
	// UI References - Assign in Inspector
	[Export] public LineEdit NameInput;

	[Export] public SpinBox LeadershipSpinBox;
	[Export] public SpinBox IntelligenceSpinBox;
	[Export] public SpinBox StrengthSpinBox;
	[Export] public SpinBox PoliticsSpinBox;
	[Export] public SpinBox CharismaSpinBox;

	[Export] public SpinBox PointsLeftSpinBox; // Used as a display

	private int _totalPoints = 10; // Extra points to allocate
	private int _baseStat = 30; // Minimum for each stat

	private System.Collections.Generic.Dictionary<SpinBox, double> _prevValues = new System.Collections.Generic.Dictionary<SpinBox, double>();

	public override void _Ready()
	{
		// Initialize SpinBoxes
		if (LeadershipSpinBox != null) LeadershipSpinBox.Value = _baseStat;
		if (IntelligenceSpinBox != null) IntelligenceSpinBox.Value = _baseStat;
		if (StrengthSpinBox != null) StrengthSpinBox.Value = _baseStat;
		if (PoliticsSpinBox != null) PoliticsSpinBox.Value = _baseStat;
		if (CharismaSpinBox != null) CharismaSpinBox.Value = _baseStat;

		// Store initial values
		if (LeadershipSpinBox != null) _prevValues[LeadershipSpinBox] = _baseStat;
		if (IntelligenceSpinBox != null) _prevValues[IntelligenceSpinBox] = _baseStat;
		if (StrengthSpinBox != null) _prevValues[StrengthSpinBox] = _baseStat;
		if (PoliticsSpinBox != null) _prevValues[PoliticsSpinBox] = _baseStat;
		if (CharismaSpinBox != null) _prevValues[CharismaSpinBox] = _baseStat;

		// Connect ValueChanged signals
		if (LeadershipSpinBox != null) LeadershipSpinBox.ValueChanged += (val) => UpdatePoints(LeadershipSpinBox);
		if (IntelligenceSpinBox != null) IntelligenceSpinBox.ValueChanged += (val) => UpdatePoints(IntelligenceSpinBox);
		if (StrengthSpinBox != null) StrengthSpinBox.ValueChanged += (val) => UpdatePoints(StrengthSpinBox);
		if (PoliticsSpinBox != null) PoliticsSpinBox.ValueChanged += (val) => UpdatePoints(PoliticsSpinBox);
		if (CharismaSpinBox != null) CharismaSpinBox.ValueChanged += (val) => UpdatePoints(CharismaSpinBox);

		UpdatePoints(null);
	}

	private void UpdatePoints(SpinBox changedBox)
	{
		// Calculate current usage
		int currentL = (int)LeadershipSpinBox.Value;
		int currentI = (int)IntelligenceSpinBox.Value;
		int currentS = (int)StrengthSpinBox.Value;
		int currentP = (int)PoliticsSpinBox.Value;
		int currentCh = (int)CharismaSpinBox.Value;

		int usedPoints = (currentL - _baseStat) + (currentI - _baseStat) + (currentS - _baseStat) + (currentP - _baseStat) + (currentCh - _baseStat);
		int remaining = _totalPoints - usedPoints;

		if (remaining < 0 && changedBox != null)
		{
			// Revert!
			changedBox.Value = _prevValues[changedBox];
			// Recalculate remaining (it should be 0 now)
			remaining = 0;
		}
		else
		{
			// Update previous values for next time
			if (LeadershipSpinBox != null) _prevValues[LeadershipSpinBox] = LeadershipSpinBox.Value;
			if (IntelligenceSpinBox != null) _prevValues[IntelligenceSpinBox] = IntelligenceSpinBox.Value;
			if (StrengthSpinBox != null) _prevValues[StrengthSpinBox] = StrengthSpinBox.Value;
			if (PoliticsSpinBox != null) _prevValues[PoliticsSpinBox] = PoliticsSpinBox.Value;
			if (CharismaSpinBox != null) _prevValues[CharismaSpinBox] = CharismaSpinBox.Value;
		}

		// Display remaining
		if (PointsLeftSpinBox != null) PointsLeftSpinBox.Value = remaining;
	}

	public void OnConfirmPressed()
	{
		// Check if valid
		if (PointsLeftSpinBox != null && PointsLeftSpinBox.Value < 0)
		{
			GD.Print("Too many points spent!");
			return;
		}

		string name = "Player";
		if (NameInput != null && !string.IsNullOrWhiteSpace(NameInput.Text))
			name = NameInput.Text;

		CreatePlayerOfficer(name);

		GD.Print("Character Created! Starting Game Loop...");
		GetTree().ChangeSceneToFile("res://scenes/WorldMap.tscn");
	}

	// ... (Keep the DB logic mostly the same)
	private void CreatePlayerOfficer(string name)
	{
		// 1. Generate World NPCs first
		// Instantiate standard generator script
		var wg = new WorldGenerator();
		wg._Ready(); // Manually init path since not in tree
		wg.GenerateNewWorld();
		// wg.QueueFree(); // Not in tree, just GC

		// string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var connection = DatabaseHelper.GetConnection())
		{
			connection.Open();

			// 2. Insert new Player (Clean old player if any, WorldGen wipes is_player=0 only)
			var cleanCmd = connection.CreateCommand();
			cleanCmd.CommandText = @"
				DELETE FROM game_state;
				DELETE FROM officers WHERE is_player = 1;
			";
			cleanCmd.ExecuteNonQuery();

			// 3. Insert Player
			var command = connection.CreateCommand();
			command.CommandText =
            @"
                INSERT INTO officers (name, leadership, intelligence, strength, politics, charisma, faction_id, location_id, is_player, rank, reputation, current_action_points, max_action_points, troops, max_troops)
                VALUES ($name, $lea, $int, $str, $pol, $cha, NULL, 1, 1, 'Volunteer', 0, 3, 3, 100, 100);
                
                INSERT INTO game_state (current_day, player_id) VALUES (1, last_insert_rowid());
			";
			command.Parameters.AddWithValue("$name", name);
			command.Parameters.AddWithValue("$lea", (int)LeadershipSpinBox.Value);
			command.Parameters.AddWithValue("$int", (int)IntelligenceSpinBox.Value);
			command.Parameters.AddWithValue("$str", (int)StrengthSpinBox.Value);
			command.Parameters.AddWithValue("$pol", (int)PoliticsSpinBox.Value);
			command.Parameters.AddWithValue("$cha", (int)CharismaSpinBox.Value);

			command.ExecuteNonQuery();

			// Re-initialize Diplomacy (Since we wiped it)
			// We use CallDeferred to ensure safe execution or just direct call if Autoload is ready
			var dipMgr = GetNodeOrNull<DiplomacyManager>("/root/DiplomacyManager");
			if (dipMgr != null)
			{
				dipMgr.InitializeRelationsIfNeeded();
			}
		}
	}
}
