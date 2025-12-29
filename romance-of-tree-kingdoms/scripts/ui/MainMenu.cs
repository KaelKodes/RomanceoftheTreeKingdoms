using Godot;
using System;
using System.Diagnostics;

public partial class MainMenu : Control
{
	// Signals (or just direct calls if we link via Editor)

	public override void _Ready()
	{
		// Hook up buttons if they exist
		// var newGameBtn = GetNode<Button>("VBoxContainer/NewGameButton");
		// newGameBtn.Pressed += OnNewGamePressed;
	}

	public void OnNewGamePressed()
	{
		GD.Print("New Game Pressed! Resetting World...");

		// 1. Reset the World Data (Call the Python Seed Script)
		// Note: For a shipped game, we'd rewrite the generator in C#.
		// For prototype, calling Python is faster.
		ResetWorldData();

		// 2. Transition to Character Creation Scene
		// Ensure you have created this scene file!
		GetTree().ChangeSceneToFile("res://scenes/CharacterCreation.tscn");
		GD.Print("World Reset. Transitioning to Character Creation.");
	}

	public void OnQuitPressed()
	{
		GetTree().Quit();
	}

	private void ResetWorldData()
	{
		// Execute tools/seed_db.py
		string projectPath = ProjectSettings.GlobalizePath("res://");
		string scriptPath = System.IO.Path.Combine(projectPath, "../tools/seed_db.py");
		string pythonExe = "python"; // Assuming in PATH

		ProcessStartInfo startInfo = new ProcessStartInfo();
		startInfo.FileName = pythonExe;
		startInfo.Arguments = $"\"{scriptPath}\"";
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;

		using (Process process = Process.Start(startInfo))
		{
			using (System.IO.StreamReader reader = process.StandardOutput)
			{
				string result = reader.ReadToEnd();
				GD.Print($"[Seed Output]: {result}");
			}
			process.WaitForExit();
		}
	}
}
