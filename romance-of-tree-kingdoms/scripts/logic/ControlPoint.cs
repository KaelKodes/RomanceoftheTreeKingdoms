using Godot;
using System;
using System.Collections.Generic;

public partial class ControlPoint : Node2D
{
	public enum CPType { HQ, SupplyDepot, Outpost }

	public CPType Type { get; private set; }
	public int OwnerFactionId { get; private set; } // 0 = Neutral
	public Vector2I GridPosition { get; private set; }
	public List<ControlPoint> Connections { get; private set; } = new List<ControlPoint>();

	// Stats
	public int MaxHealth { get; private set; } = 500;
	public int CurrentHealth { get; private set; }
	public bool IsDestroyed => CurrentHealth <= 0;

	// Visuals
	private Sprite2D _sprite;

	public void Initialize(Vector2I gridPos, CPType type, int ownerId)
	{
		GridPosition = gridPos;
		Type = type;
		OwnerFactionId = ownerId;
		Connections = new List<ControlPoint>();
		CurrentHealth = MaxHealth;

		// Create Sprite
		_sprite = new Sprite2D();
		AddChild(_sprite);

		UpdateVisuals();
	}

	public void SetType(CPType type)
	{
		Type = type;
		UpdateVisuals(); // Refresh to show new type icon
	}

	// isAllyRef: 0=Unknown/Neutral, 1=Ally, -1=Enemy. If 0, uses internal logic.
	public void UpdateVisuals(int allyStatus = 0)
	{
		string filename = "";

		if (OwnerFactionId == 0)
		{
			filename = "UnOccupiedControlPoint.png";
		}
		else
		{
			// Determine Color
			string color = "Red"; // Default Enemy

			if (allyStatus == 1) color = "Blue";
			else if (allyStatus == -1) color = "Red";
			else
			{
				// Fallback Logic
				if (OwnerFactionId == -1) color = "Blue";
			}

			string typeStr = "OP";
			switch (Type)
			{
				case CPType.HQ: typeStr = "HQ"; break;
				case CPType.SupplyDepot: typeStr = "Supply"; break;
				case CPType.Outpost: typeStr = "OP"; break;
			}
			filename = $"{color}{typeStr}.png";
		}

		string path = $"res://assets/art/battle/{filename}";
		var texture = GD.Load<Texture2D>(path);
		if (texture != null)
		{
			_sprite.Texture = texture;
			_sprite.Scale = new Vector2(2, 2); // 32px -> 64px
			_sprite.TextureFilter = TextureFilterEnum.Nearest;
		}
		else
		{
			GD.PrintErr($"Failed to load texture: {path}");
		}

		QueueRedraw();
	}

	public void AddConnection(ControlPoint other)
	{
		if (!Connections.Contains(other))
		{
			Connections.Add(other);
			other.Connections.Add(this); // Bidirectional
		}
	}

	public override void _Draw()
	{
		// Draw Connections (Lines)
		foreach (var neighbor in Connections)
		{
			if (IsInstanceValid(neighbor))
			{
				DrawLine(Vector2.Zero, neighbor.Position - Position, new Color(1, 1, 1, 0.5f), 4.0f);
			}
		}
	}

	public void TakeDamage(int amount)
	{
		if (IsDestroyed) return;
		CurrentHealth -= amount;
		if (CurrentHealth <= 0)
		{
			CurrentHealth = 0;
			GD.Print($"Control Point {Type} Destroyed!");
			_sprite.Modulate = Colors.DarkGray; // visual feedback
		}
	}

	public void SetOwner(int factionId)
	{
		if (OwnerFactionId != factionId)
		{
			OwnerFactionId = factionId;
			UpdateVisuals();
		}
	}
}
