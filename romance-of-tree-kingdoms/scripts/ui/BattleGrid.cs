using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class BattleGrid : TileMap
{
	// Grid Configuration
	private const int MAP_WIDTH = 30; // Wider for meaningful lanes
	private const int MAP_HEIGHT = 20;

	// Tile IDs
	private const int TILE_PLAINS = 0; // Walkable
	private const int TILE_FOREST = 1; // Blocked (Trees/Walls)

	private PackedScene _cpScene; // If we use a separate scene file, otherwise pure code

	public override void _Ready()
	{
		// _cpScene = GD.Load<PackedScene>("res://scenes/ControlPoint.tscn"); // Later
	}

	public List<ControlPoint> GenerateMapWithCPs(int defenderId, int attackerId)
	{
		GD.Print("Generating Networked Map...");
		Clear();
		var cps = new List<ControlPoint>();
		var rng = new Random();

		// 1. Fill with Forest (Blocked)
		for (int x = 0; x < MAP_WIDTH; x++)
		{
			for (int y = 0; y < MAP_HEIGHT; y++)
			{
				SetCell(0, new Vector2I(x, y), 0, new Vector2I(TILE_FOREST, 0));
			}
		}

		// 2. Place Nodes (Control Points)
		// Defender HQ (Left)
		var defHQ = CreateCP(new Vector2I(3, MAP_HEIGHT / 2), ControlPoint.CPType.HQ, defenderId);
		cps.Add(defHQ);

		// Attacker HQ (Right)
		var attHQ = CreateCP(new Vector2I(MAP_WIDTH - 4, MAP_HEIGHT / 2), ControlPoint.CPType.HQ, attackerId);
		cps.Add(attHQ);

		// Random Points (Middle)
		int numPoints = 6;
		for (int i = 0; i < numPoints; i++)
		{
			int x = rng.Next(6, MAP_WIDTH - 6);
			int y = rng.Next(3, MAP_HEIGHT - 3);
			Vector2I pos = new Vector2I(x, y);

			// Avoid overlapping
			if (cps.Any(c => c.Position.DistanceTo(GridToWorld(pos)) < 100)) continue;

			var type = rng.NextDouble() > 0.5 ? ControlPoint.CPType.SupplyDepot : ControlPoint.CPType.Outpost;
			cps.Add(CreateCP(pos, type, 0)); // Neutral
		}

		// 3. Connect Nodes & Carve Paths
		// Connect everyone to nearest 2 neighbors to form a web
		foreach (var cp in cps)
		{
			var neighbors = cps
				.Where(c => c != cp)
				.OrderBy(c => c.Position.DistanceSquaredTo(cp.Position))
				.Take(2); // Connect to 2 closest

			foreach (var neighbor in neighbors)
			{
				cp.AddConnection(neighbor);
				CarvePath(cp.GridPosition, neighbor.GridPosition);
			}
		}

		// Ensure HQs are connected to something (should be covered by nearest, but just in case)

		return cps;
	}

	private ControlPoint CreateCP(Vector2I gridPos, ControlPoint.CPType type, int ownerId)
	{
		// Since we don't have a scene yet, we create Node2D and add script manually? 
		// Or just use the class directly if it inherits Node2D.
		var cp = new ControlPoint();
		AddChild(cp);
		cp.Initialize(gridPos, type, ownerId);
		cp.Position = GridToWorld(gridPos);

		// Carve space around CP
		CarveArea(gridPos, 2);

		return cp;
	}

	private void CarvePath(Vector2I start, Vector2I end)
	{
		// Simple Line Drawing suitable for Grid
		var points = GetLine(start, end);
		foreach (var p in points)
		{
			CarveArea(p, 1); // Radius 1 (Width 3 path)
		}
	}

	private void CarveArea(Vector2I center, int radius)
	{
		for (int x = -radius; x <= radius; x++)
		{
			for (int y = -radius; y <= radius; y++)
			{
				Vector2I pos = center + new Vector2I(x, y);
				if (pos.X >= 0 && pos.X < MAP_WIDTH && pos.Y >= 0 && pos.Y < MAP_HEIGHT)
				{
					SetCell(0, pos, 0, new Vector2I(TILE_PLAINS, 0));
				}
			}
		}
	}

	// Bresenham's Line Algorithm or similar
	private List<Vector2I> GetLine(Vector2I p0, Vector2I p1)
	{
		List<Vector2I> points = new List<Vector2I>();
		int x0 = p0.X, y0 = p0.Y, x1 = p1.X, y1 = p1.Y;
		int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
		int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
		int err = dx + dy, e2;
		while (true)
		{
			points.Add(new Vector2I(x0, y0));
			if (x0 == x1 && y0 == y1) break;
			e2 = 2 * err;
			if (e2 >= dy) { err += dy; x0 += sx; }
			if (e2 <= dx) { err += dx; y0 += sy; }
		}
		return points;
	}

	public bool IsWalkable(Vector2I coords)
	{
		var data = GetCellAtlasCoords(0, coords);
		int id = data.X;
		return id == TILE_PLAINS; // Forest is now walls
	}

	public int GetMovementCost(Vector2I coords)
	{
		return 1;
	}

	// Helper: Convert World Position to Grid
	public Vector2I WorldToGrid(Vector2 worldPos)
	{
		return LocalToMap(ToLocal(worldPos));
	}

	public Vector2 GridToWorld(Vector2I gridPos)
	{
		return ToGlobal(MapToLocal(gridPos));
	}
}
