using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class TaskPopout : Control
{
    [Export] public Label CityTaskLabel;
    [Export] public Label OfficerTaskLabel;
    [Export] public Button ToggleButton;
    [Export] public Control ContentPanel;

    private bool _isOpen = false;

    public override void _Ready()
    {
        if (ToggleButton != null)
        {
            ToggleButton.Pressed += OnTogglePressed;
        }
        if (ContentPanel != null)
        {
            ContentPanel.Visible = _isOpen;
        }
    }

    public void Refresh(int officerId, int cityId)
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();

            // 1. Get Officer Task
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_mission FROM officers WHERE officer_id = $oid";
            cmd.Parameters.AddWithValue("$oid", officerId);
            var mission = cmd.ExecuteScalar();
            if (OfficerTaskLabel != null)
            {
                OfficerTaskLabel.Text = (mission != null && mission != DBNull.Value) ? mission.ToString() : "None";
            }

            // 2. Get City Task (Assignment)
            // In our current system, cities don't have a single "task" but rather multiple officers working on things.
            // However, we can show the "Governor's assignment" or most prominent active mission.
            // For now, let's show the Governor's mission if they are there.
            var cmd2 = conn.CreateCommand();
            cmd2.CommandText = @"
				SELECT o.current_mission 
				FROM cities c 
				JOIN officers o ON c.governor_id = o.officer_id 
				WHERE c.city_id = $cid";
            cmd2.Parameters.AddWithValue("$cid", cityId);
            var cityMission = cmd2.ExecuteScalar();
            if (CityTaskLabel != null)
            {
                CityTaskLabel.Text = (cityMission != null && cityMission != DBNull.Value) ? cityMission.ToString() : "No Governor Task";
            }
        }
    }

    private void OnTogglePressed()
    {
        _isOpen = !_isOpen;
        if (ContentPanel != null)
        {
            ContentPanel.Visible = _isOpen;
        }
    }
}
