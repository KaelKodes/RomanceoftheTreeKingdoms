using Godot;
using Microsoft.Data.Sqlite;
using System;

public partial class OfficerCard : Window
{
	private int _officerId;
	private int _playerId;

	// UI Refs
	private Label _titleLabel;
	private Label _nameLabel;
	private Label _rankLabel;
	private Label _factionLabel;
	private Label _reputationLabel;
	private Label _winsLabel;
	private Label _relLabel;
	private Label _statsLabel;
	private VBoxContainer _actionContainer;
	private TextureRect _portrait;

	public override void _Ready()
	{
		// Wire up UI
		var root = GetNode("Panel/MarginContainer/VBoxContainer");
		_portrait = root.GetNode<TextureRect>("Portrait");
		_titleLabel = root.GetNode<Label>("Title1Label");
		_nameLabel = root.GetNode<Label>("NameLabel");
		_rankLabel = root.GetNode<Label>("RankLabel");
		_factionLabel = root.GetNode<Label>("FactionLabel");
		_reputationLabel = root.GetNode<Label>("ReputationLabel");
		_winsLabel = root.GetNode<Label>("WinsLabel");
		_relLabel = root.GetNode<Label>("RelationLabel");
		_statsLabel = root.GetNode<Label>("StatsLabel");
		_actionContainer = root.GetNode<VBoxContainer>("ActionContainer");

		// Window Events
		// Window Events
		CloseRequested += Hide;
	}

	public void Init(int officerId)
	{
		_officerId = officerId;
		// Get Player ID
		var am = GetNodeOrNull<ActionManager>("/root/ActionManager");
		// We can just query DB for player ID
		_playerId = GetPlayerIdFromDB();

		Refresh();
		Popup(); // Show window
	}

	private void Refresh()
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var conn = new SqliteConnection($"Data Source={dbPath}"))
		{
			conn.Open();

			// 1. Fetch Officer Data
			var cmd = conn.CreateCommand();
			cmd.CommandText = @"
                SELECT o.name, f.name, o.leadership, o.intelligence, o.strength, o.politics, f.leader_id, o.faction_id, o.rank, o.reputation, o.battles_won, o.charisma, 
                       (SELECT COUNT(*) FROM cities WHERE governor_id = o.officer_id AND city_id = o.location_id) as is_local_gov,
                       o.portrait_source_id, o.portrait_coords
                FROM officers o
                LEFT JOIN factions f ON o.faction_id = f.faction_id
				WHERE o.officer_id = $oid";
			cmd.Parameters.AddWithValue("$oid", _officerId);

			using (var r = cmd.ExecuteReader())
			{
				if (r.Read())
				{
					string name = r.GetString(0);
					string faction = r.IsDBNull(1) ? "Free Officer" : r.GetString(1);
					int ldr = r.GetInt32(2);
					int intl = r.GetInt32(3);
					int str = r.GetInt32(4);
					int pol = r.GetInt32(5);
					int factionLeaderId = r.IsDBNull(6) ? 0 : r.GetInt32(6);
					bool isLeader = (factionLeaderId == _officerId);
					int factionId = r.IsDBNull(7) ? 0 : r.GetInt32(7);
					string rank = r.IsDBNull(8) ? GameConstants.RANK_VOLUNTEER : r.GetString(8);
					int rep = r.IsDBNull(9) ? 0 : r.GetInt32(9);
					int wins = r.IsDBNull(10) ? 0 : r.GetInt32(10);
					int cha = r.IsDBNull(11) ? 50 : r.GetInt32(11);
					bool isGov = r.GetInt32(12) > 0;
					int pSrc = r.IsDBNull(13) ? 0 : r.GetInt32(13);
					string pCoords = r.IsDBNull(14) ? "0,0" : r.GetString(14);

					// Load Portrait
					// Load Atlas
					var atlas = GD.Load<TileSet>("res://assets/Portraits/CustomOfficers.tres");
					if (atlas != null)
					{
						// Parse coords
						var parts = pCoords.Split(',');
						int x = int.Parse(parts[0]);
						int y = int.Parse(parts[1]);
						var region = atlas.GetSource(pSrc) as TileSetAtlasSource;
						if (region != null)
						{
							// Create AtlasTexture
							var tex = new AtlasTexture();
							tex.Atlas = region.Texture;
							tex.Region = region.GetTileTextureRegion(new Vector2I(x, y));
							_portrait.Texture = tex;
							_portrait.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize; // Use actual size? Or Keep Aspect Centered
							_portrait.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
						}
					}

					// Build Title
					var titles = new System.Collections.Generic.List<string>();
					if (isLeader) titles.Add("Faction Leader");
					if (isGov) titles.Add("Governor");

					if (titles.Count > 0)
					{
						_titleLabel.Text = string.Join(", ", titles);
						_titleLabel.Visible = true;
					}
					else
					{
						_titleLabel.Visible = false;
					}

					_nameLabel.Text = name;
					_rankLabel.Text = $"Rank: {rank}";
					_factionLabel.Text = faction;
					_reputationLabel.Text = $"Reputation: {rep}";
					_winsLabel.Text = $"Wins: {wins}";

					// Get Relation
					int rel = GetRelationship(_playerId, _officerId);
					_relLabel.Text = $"Relationship: {rel}";
					if (rel > 50) _relLabel.Modulate = Colors.Green;
					else if (rel < -20) _relLabel.Modulate = Colors.Red;
					else _relLabel.Modulate = Colors.White;

					// Mask Stats?
					// Logic: If Rel < 10, show range or ???.
					// For now: Mask completely if Rel < 10.
					if (rel >= 10 || _officerId == _playerId) // Always know self
					{
						_statsLabel.Text = $"Leadership: {ldr}\nInt: {intl} | Str: {str}\nPol: {pol} | Cha: {cha}";
					}
					else
					{
						_statsLabel.Text = "Stats: ??? (Get closer to reveal)";
					}

					SetupActions(rel, isLeader, factionId, isGov);
				}
			}
		}
	}

	private void SetupActions(int rel, bool isTargetLeader, int targetFactionId, bool isTargetGovernor)
	{
		// Clear
		foreach (Node c in _actionContainer.GetChildren()) c.QueueFree();

		if (_officerId == _playerId) return; // No actions on self

		// Talk
		AddButton("Talk (1 AP)", () => OnTalkPressed());

		// Wine and Dine
		AddButton("Wine & Dine (1 AP + Gold)", () =>
		{
			var am = GetNode<ActionManager>("/root/ActionManager");
			am.PerformWineAndDine(GetPlayerIdFromDB(), _officerId);
			Refresh();
		});

		// Recruit / Join Logic
		// If they are Free Officer -> Recruit
		if (targetFactionId == 0)
		{
			AddButton("Recruit to Faction (1 AP)", () => OnRecruitPressed());
		}
		else
		{
			// They are in a faction.
			// If they are Leader, and Rel > 50 -> "Request to Join" (if player is Free?)
			// We need to know if Player is Free.
			if ((isTargetLeader || isTargetGovernor) && rel >= 50)
			{
				AddButton("Request to Join Faction (1 AP)", () => OnJoinPressed());
			}
		}
	}

	private void AddButton(string text, Action onClick)
	{
		var btn = new Button();
		btn.Text = text;
		btn.Pressed += onClick;
		_actionContainer.AddChild(btn);
	}

	private void OnTalkPressed()
	{
		// Logic delegated to ActionManager
		var am = GetNode<ActionManager>("/root/ActionManager");
		am.PerformTalk(_playerId, _officerId);

		// Refresh UI
		Refresh();
	}

	private void OnRecruitPressed()
	{
		var am = GetNode<ActionManager>("/root/ActionManager");
		am.PerformRecruit(_playerId, _officerId);
		Refresh();
	}

	private void OnJoinPressed()
	{
		// Logic to join faction
		var am = GetNode<ActionManager>("/root/ActionManager");
		am.PerformJoinFaction(_playerId, _officerId);
		Refresh();
	}

	private int GetPlayerIdFromDB()
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var conn = new SqliteConnection($"Data Source={dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT officer_id FROM officers WHERE is_player = 1 LIMIT 1";
			var res = cmd.ExecuteScalar();
			return res != null ? (int)(long)res : -1;
		}
	}

	private int GetRelationship(int pId, int tId)
	{
		var rm = GetNode<RelationshipManager>("/root/RelationshipManager");
		return rm.GetRelation(pId, tId);
	}
}
