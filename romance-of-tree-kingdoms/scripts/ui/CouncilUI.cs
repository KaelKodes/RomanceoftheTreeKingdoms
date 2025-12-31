using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public partial class CouncilUI : Control
{
    [Export] public NodePath TreasuryLabelPath;
    [Export] public NodePath TaxSliderPath;
    [Export] public NodePath OfficerContainerPath;
    [Export] public NodePath TopContributorLabelPath;

    private Label TreasuryLabel => GetNode<Label>(TreasuryLabelPath);
    private Slider TaxSlider => GetNode<Slider>(TaxSliderPath);
    private VBoxContainer OfficerContainer => GetNode<VBoxContainer>(OfficerContainerPath);
    private Label TopContributorLabel => GetNode<Label>(TopContributorLabelPath);

    private int _factionId;
    private int _cityId;
    private int _currentCP;

    public void Open(int factionId, int cityId, int cpAmount)
    {
        _factionId = factionId;
        _cityId = cityId;
        _currentCP = cpAmount;

        Show();
        LoadData();
    }

    private void LoadData()
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();

            // 1. Load Treasury
            var factionCmd = conn.CreateCommand();
            factionCmd.CommandText = "SELECT gold_treasury, supplies, tax_rate FROM factions WHERE faction_id = $fid";
            factionCmd.Parameters.AddWithValue("$fid", _factionId);
            using (var r = factionCmd.ExecuteReader())
            {
                if (r.Read())
                {
                    long gold = r.GetInt64(0);
                    long supplies = r.GetInt64(1);
                    double tax = r.GetDouble(2);
                    TreasuryLabel.Text = $"Treasury: {gold} Gold | {supplies} Supplies";
                    TaxSlider.Value = tax * 100;
                }
            }

            // 2. Load Top Contributor
            var topCmd = conn.CreateCommand();
            topCmd.CommandText = "SELECT name, merit_score FROM officers WHERE faction_id = $fid ORDER BY merit_score DESC LIMIT 1";
            topCmd.Parameters.AddWithValue("$fid", _factionId);
            var topRes = topCmd.ExecuteScalar();
            if (topRes != null)
            {
                TopContributorLabel.Text = $"Top Contributor: {topRes}";
            }

            // 3. Populate Officers for Assignment
            PopulateOfficers(conn);
        }
    }

    private void PopulateOfficers(SqliteConnection conn)
    {
        foreach (Node child in OfficerContainer.GetChildren()) child.QueueFree();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT officer_id, name, current_mission FROM officers WHERE location_id = $cid AND faction_id = $fid";
        cmd.Parameters.AddWithValue("$cid", _cityId);
        cmd.Parameters.AddWithValue("$fid", _factionId);

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                int oid = r.GetInt32(0);
                string name = r.GetString(1);
                string mission = r.IsDBNull(2) ? "None" : r.GetString(2);

                AddOfficerRow(oid, name, mission);
            }
        }
    }

    private void AddOfficerRow(int oid, string name, string mission)
    {
        var row = new HBoxContainer();
        var nameLabel = new Label { Text = name, CustomMinimumSize = new Vector2(120, 0) };
        row.AddChild(nameLabel);

        var missionBtn = new OptionButton();
        missionBtn.AddItem("None");
        missionBtn.AddItem("Commerce");
        missionBtn.AddItem("Farming");
        missionBtn.AddItem("Science");
        missionBtn.AddItem("Defense");
        missionBtn.AddItem("Order");

        // Select current
        for (int i = 0; i < missionBtn.ItemCount; i++)
        {
            if (missionBtn.GetItemText(i) == mission) missionBtn.Selected = i;
        }

        missionBtn.ItemSelected += (index) => OnMissionSelected(oid, missionBtn.GetItemText((int)index));
        row.AddChild(missionBtn);

        OfficerContainer.AddChild(row);
    }

    private void OnMissionSelected(int oid, string missionName)
    {
        if (_currentCP <= 0)
        {
            GD.Print("No CP left!");
            return;
        }

        _currentCP--;
        GD.Print($"Assigned {missionName} to officer {oid}. Remaining CP: {_currentCP}");

        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE officers SET current_mission = $miss WHERE officer_id = $oid";
            cmd.Parameters.AddWithValue("$miss", missionName == "None" ? DBNull.Value : missionName);
            cmd.Parameters.AddWithValue("$oid", oid);
            cmd.ExecuteNonQuery();
        }
    }

    private void OnTaxRateChanged(float value)
    {
        double rate = value / 100.0;
        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE factions SET tax_rate = $rate WHERE faction_id = $fid";
            cmd.Parameters.AddWithValue("$rate", rate);
            cmd.Parameters.AddWithValue("$fid", _factionId);
            cmd.ExecuteNonQuery();
        }
        GD.Print($"Tax Rate updated to {value}%");
    }

    private void OnClosePressed()
    {
        Hide();
        // Resume Game Turn logic in WorldMap
    }
}
