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
	private RichTextLabel _statsLabel;
	private RichTextLabel _skillsLabel;
	private Label _formationLabel;
	private Label _troopLabel;
	private Label _battleLabel;
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
		_statsLabel = root.GetNode<RichTextLabel>("StatsLabel");
		_skillsLabel = root.GetNode<RichTextLabel>("SkillsLabel");
		_formationLabel = root.GetNode<Label>("FormationLabel");
		_troopLabel = root.GetNode<Label>("TroopLabel");
		_battleLabel = root.GetNode<Label>("BattleLabel");
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
                        o.portrait_source_id, o.portrait_coords, o.formation_type, o.location_id, o.main_troop_type, o.officer_type,
                        o.farming, o.business, o.inventing, o.fortification, o.governance, o.public_attitude
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
					int targetLoc = r.GetInt32(16);

					// Get Player Loc
					int playerLoc = -1;
					using (var pLocCmd = conn.CreateCommand())
					{
						pLocCmd.CommandText = "SELECT location_id FROM officers WHERE officer_id = $pid";
						pLocCmd.Parameters.AddWithValue("$pid", _playerId);
						var pRes = pLocCmd.ExecuteScalar();
						if (pRes != null) playerLoc = Convert.ToInt32(pRes);
					}

					bool isLocal = (playerLoc == targetLoc);

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
							Vector2I coords = new Vector2I(x, y);
							if (region.HasTile(coords))
							{
								// Create AtlasTexture
								var tex = new AtlasTexture();
								tex.Atlas = region.Texture;
								tex.Region = region.GetTileTextureRegion(coords);
								_portrait.Texture = tex;
								_portrait.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
								_portrait.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
							}
							else
							{
								GD.PrintErr($"[OfficerCard] Invalid Portrait Coords: {x},{y} for Source {pSrc}");
							}
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
					int fType = r.IsDBNull(15) ? 0 : r.GetInt32(15);
					int mtt = r.IsDBNull(17) ? 0 : r.GetInt32(17);
					int oct = r.IsDBNull(18) ? 0 : r.GetInt32(18);

					// Logic: If Rel < 10, show range or ???.
					// For now: Mask completely if Rel < 10.
					if (rel >= 10 || _officerId == _playerId) // Always know self
					{
						_statsLabel.Text = $"[center][hint=Leadership]Ldr[/hint]: {ldr}\n[hint=Intelligence]Int[/hint]: {intl} | [hint=Strength]Str[/hint]: {str}\n[hint=Politics]Pol[/hint]: {pol} | [hint=Charisma]Cha[/hint]: {cha}[/center]";

						// Populate Skills
						int farm = r.GetInt32(19);
						int biz = r.GetInt32(20);
						int inv = r.GetInt32(21);
						int fort = r.GetInt32(22);
						int gov = r.GetInt32(23);
						int pubAtt = r.GetInt32(24);
						_skillsLabel.Text = $"[center][hint=Farming Skill]Farm[/hint]: {farm} | [hint=Business Skill]Biz[/hint]: {biz}\n[hint=Technology/Inventing Skill]Tech[/hint]: {inv} | [hint=Fortification Skill]Fort[/hint]: {fort} | [hint=Governance Skill]Gov[/hint]: {gov}\n[hint=Public Attitude Bonus]Attitude[/hint]: {pubAtt}[/center]";
						_skillsLabel.Visible = true;
						_skillsLabel.Modulate = Colors.Green;

						// Populate New Skill Elements
						TroopType tt = (mtt > 0) ? (TroopType)(mtt - 1) : TroopType.Infantry;
						TroopType ot = (oct > 0) ? (TroopType)(oct - 1) : TroopType.Infantry;
						FormationShape fs = (FormationShape)fType;

						_formationLabel.Text = $"Formation: {fs}";
						_troopLabel.Text = $"Troop: {tt}";
						_battleLabel.Text = $"Battle: {ot}";

						// Hide labels if player (they have buttons)
						bool isSelf = (_officerId == _playerId);
						_formationLabel.Visible = !isSelf;
						_troopLabel.Visible = !isSelf;
						_battleLabel.Visible = !isSelf;

						// Optional Color coding
						_formationLabel.Modulate = new Color(0.7f, 0.8f, 1.0f); // Soft blue
						_troopLabel.Modulate = new Color(1.0f, 0.8f, 0.6f);     // Soft orange
						_battleLabel.Modulate = new Color(1.0f, 0.7f, 0.7f);    // Soft red

						if (_officerId != _playerId)
						{
							// No longer needed in stats label text
						}
					}
					else
					{
						_statsLabel.Text = "Stats: ??? (Get closer to reveal)";
						_skillsLabel.Visible = false;
						_formationLabel.Visible = false;
						_troopLabel.Visible = false;
						_battleLabel.Visible = false;
					}

					SetupActions(rel, isLeader, factionId, isGov, fType, isLocal, mtt, oct);
				}
			}
		}
	}

	private void SetupActions(int rel, bool isTargetLeader, int targetFactionId, bool isTargetGovernor, int currentFormation, bool isLocal, int currentTroopIdx, int currentOffTypeIdx)
	{
		// Clear
		foreach (Node c in _actionContainer.GetChildren()) c.QueueFree();

		if (_officerId == _playerId)
		{
			// Self Actions
			AddButton($"Formation: {(FormationShape)currentFormation} (Cycle)", () => OnFormationCycle(currentFormation));

			// Resolve Troop Type display
			TroopType tt = (currentTroopIdx > 0) ? (TroopType)(currentTroopIdx - 1) : TroopType.Infantry;
			AddButton($"Troop Type: {tt} (Cycle)", () => OnTroopCycle(currentTroopIdx));

			// Resolve Battle Type (Officer Type) display
			TroopType ot = (currentOffTypeIdx > 0) ? (TroopType)(currentOffTypeIdx - 1) : TroopType.Infantry;
			AddButton($"Battle Type: {ot} (Cycle)", () => OnBattleTypeCycle(currentOffTypeIdx));

			return;
		}

		if (!isLocal)
		{
			var lbl = new Label();
			lbl.Text = "Cannot Interact (Travel to same city)";
			lbl.Modulate = Colors.Gray;
			lbl.HorizontalAlignment = HorizontalAlignment.Center;
			_actionContainer.AddChild(lbl);
			return;
		}

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

	private void OnFormationCycle(int current)
	{
		int next = (current + 1) % 4; // Cycle 0-3 (Surrounded, Vanguard, RearGuard, Line)
									  // Update DB
		UpdateOfficerTypeStat("formation_type", next);
		Refresh();
	}

	private void OnTroopCycle(int currentIdx)
	{
		// currentIdx is 0 (auto) or 1-5 (enum values 0-4 + 1)
		// Restrict Player to first 3 types: Infantry, Archer, Cavalry
		int currentType = (currentIdx == 0) ? -1 : (currentIdx - 1);
		int nextType = (currentType + 1) % 3;
		UpdateOfficerTypeStat("main_troop_type", nextType + 1);
		Refresh();
	}

	private void OnBattleTypeCycle(int currentIdx)
	{
		// Restrict Player to first 3 types: Infantry, Archer, Cavalry
		int currentType = (currentIdx == 0) ? -1 : (currentIdx - 1);
		int nextType = (currentType + 1) % 3;
		UpdateOfficerTypeStat("officer_type", nextType + 1);
		Refresh();
	}

	private void UpdateOfficerTypeStat(string columnName, int value)
	{
		string dbPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../tree_kingdoms.db");
		using (var conn = new SqliteConnection($"Data Source={dbPath}"))
		{
			conn.Open();
			var cmd = conn.CreateCommand();
			cmd.CommandText = $"UPDATE officers SET {columnName} = $val WHERE officer_id = $oid";
			cmd.Parameters.AddWithValue("$val", value);
			cmd.Parameters.AddWithValue("$oid", _officerId);
			cmd.ExecuteNonQuery();
		}
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
