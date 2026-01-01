using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class CityInfoPanel : Control
{
    [Export] public Label NameLabel;
    [Export] public Button XButton;
    [Export] public Button YButton;

    [Export] public Label FactionLabel;
    [Export] public Label GovLabel;
    [Export] public Label TactLabel;
    [Export] public Label OfficersLabel;
    [Export] public Label RoninLabel;

    [Export] public Label GoldLabel;
    [Export] public Label SuppliesLabel;
    [Export] public Label TroopsLabel;
    [Export] public Label PopLabel;

    [Export] public Control DevPanel; // Collapsible
    [Export] public Button DevToggleButton; // The triangle
    [Export] public RichTextLabel DevStatsLabel; // Com, Tech, Def, Safe with Min/Max

    private int _cityId = -1;
    private bool _isDevPanelVisible = false;

    public override void _Ready()
    {
        if (DevToggleButton != null)
        {
            DevToggleButton.Pressed += OnDevTogglePressed;
        }
        if (DevPanel != null)
        {
            DevPanel.Visible = _isDevPanelVisible;
        }
    }

    public void Refresh(int cityId)
    {
        _cityId = cityId;
        if (_cityId == -1)
        {
            Visible = false;
            return;
        }
        Visible = true;

        using (var conn = DatabaseHelper.GetConnection())
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
				SELECT c.name, f.name as faction_name, 
					   o_gov.name as gov_name, o_tact.name as tact_name,
					   (SELECT COUNT(*) FROM officers WHERE location_id = c.city_id AND faction_id IS NOT NULL) as officer_count,
					   (SELECT COUNT(*) FROM officers WHERE location_id = c.city_id AND faction_id IS NULL) as ronin_count,
					   f.gold_treasury, f.supplies, (SELECT COALESCE(SUM(troops), 0) FROM officers WHERE location_id = c.city_id) as total_troops, c.draft_population,
					   c.commerce, c.max_stats,
					   c.technology, c.max_stats,
					   c.defense_level, c.max_stats,
					   c.public_order,
					   f.color
				FROM cities c
				LEFT JOIN factions f ON c.faction_id = f.faction_id
				LEFT JOIN officers o_gov ON c.governor_id = o_gov.officer_id
				LEFT JOIN officers o_tact ON f.tactician_id = o_tact.officer_id
				WHERE c.city_id = $cid";
            cmd.Parameters.AddWithValue("$cid", _cityId);

            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    string cityName = r.GetString(0);
                    string factionName = r.IsDBNull(1) ? "Neutral" : r.GetString(1);
                    string govName = r.IsDBNull(2) ? "None" : r.GetString(2);
                    string tactName = r.IsDBNull(3) ? "None" : r.GetString(3);
                    int offCount = r.GetInt32(4);
                    int ronCount = r.GetInt32(5);
                    int gold = r.IsDBNull(6) ? 0 : r.GetInt32(6);
                    int supplies = r.IsDBNull(7) ? 0 : r.GetInt32(7);
                    int troops = r.GetInt32(8);
                    int pop = r.GetInt32(9);

                    int com = r.GetInt32(10); int maxCom = r.GetInt32(11);
                    int tech = r.GetInt32(12); int maxTech = r.GetInt32(13);
                    int def = r.GetInt32(14); int maxDef = r.GetInt32(15);
                    int po = r.GetInt32(16);
                    string fColor = r.IsDBNull(17) ? "#FFFFFF" : r.GetString(17);

                    if (NameLabel != null)
                    {
                        NameLabel.Text = cityName;
                        NameLabel.Modulate = new Color(fColor);
                    }

                    if (FactionLabel != null) FactionLabel.Text = $"Faction: {factionName}";
                    if (GovLabel != null) GovLabel.Text = $"Gov: {govName}";
                    if (TactLabel != null) TactLabel.Text = $"Tactician: {tactName}";
                    if (OfficersLabel != null) OfficersLabel.Text = $"Officers: {offCount}";
                    if (RoninLabel != null) RoninLabel.Text = $"Ronin: {ronCount}";

                    if (GoldLabel != null) GoldLabel.Text = $"Gold: {gold}";
                    if (SuppliesLabel != null) SuppliesLabel.Text = $"Supplies: {supplies}";
                    if (TroopsLabel != null) TroopsLabel.Text = $"Troops: {troops}";
                    if (PopLabel != null) PopLabel.Text = $"Population: {pop}";

                    if (DevStatsLabel != null)
                    {
                        DevStatsLabel.Text = $"[center]" +
                            $"[hint=Commerce]Com[/hint]: {com}/{maxCom}\n" +
                            $"[hint=Technology]Tech[/hint]: {tech}/{maxTech}\n" +
                            $"[hint=Defense]Def[/hint]: {def}/{maxDef}\n" +
                            $"[hint=Public Order]Safe[/hint]: {po}/1000" +
                            $"[/center]";
                    }
                }
            }
        }
    }

    private void OnDevTogglePressed()
    {
        _isDevPanelVisible = !_isDevPanelVisible;
        if (DevPanel != null)
        {
            DevPanel.Visible = _isDevPanelVisible;
        }
        if (DevToggleButton != null)
        {
            DevToggleButton.RotationDegrees = _isDevPanelVisible ? 0 : -90;
        }
    }
}
