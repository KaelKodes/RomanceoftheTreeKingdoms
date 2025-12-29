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
	private Label _label;

	public void Initialize(Vector2I gridPos, CPType type, int ownerId)
	{
		GridPosition = gridPos;
		Type = type;
		OwnerFactionId = ownerId;
		Connections = new List<ControlPoint>();

		CurrentHealth = MaxHealth;

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
		// Only draw if index is lower to avoid double drawing lines
		foreach (var neighbor in Connections)
		{
			// Check if neighbor is instantiated and valid
			if (IsInstanceValid(neighbor))
			{
				DrawLine(Vector2.Zero, neighbor.Position - Position, new Color(1, 1, 1, 0.3f), 2.0f);
			}
		}

		// Draw Self
		Color color = Colors.Gray;
		if (OwnerFactionId == -1) color = Colors.Blue; // Player
		else if (OwnerFactionId > 0) color = Colors.Red; // Enemy (Simplification for now)

		if (IsDestroyed) color = Colors.Black;

		Vector2 size = new Vector2(24, 24);
		Rect2 rect = new Rect2(-size / 2, size);

		switch (Type)
		{
			case CPType.HQ:
				DrawRect(rect, color); // Square
				DrawRect(rect, Colors.White, false, 2.0f); // Border
				break;
			case CPType.SupplyDepot:
				DrawCircle(Vector2.Zero, 12, color); // Circle
				break;
			case CPType.Outpost:
				// Triangle
				Vector2[] points = new Vector2[] {
					new Vector2(0, -12),
					new Vector2(12, 12),
					new Vector2(-12, 12)
				};
				DrawColoredPolygon(points, color);
				break;
		}

		// Draw Label (Type)
		// Handled via child label if added, or just Shape for now.
	}

	public void TakeDamage(int amount)
	{
		if (IsDestroyed) return;
		CurrentHealth -= amount;
		if (CurrentHealth <= 0)
		{
			CurrentHealth = 0;
			GD.Print($"Control Point {Type} Destroyed!");
			QueueRedraw();
		}
	}

	public void SetOwner(int factionId)
	{
		if (OwnerFactionId != factionId)
		{
			OwnerFactionId = factionId;
			QueueRedraw();
		}
	}
}
