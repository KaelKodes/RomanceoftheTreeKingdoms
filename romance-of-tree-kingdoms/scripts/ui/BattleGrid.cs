using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class BattleGrid : Node2D // Changed from TileMap
{
	// Grid Configuration
	private const int MAP_WIDTH = 30;
	private const int MAP_HEIGHT = 20;
	private const int TILE_SIZE = 64; // Assuming 64px tiles based on assets

	// Tile IDs
	private const int TILE_PLAINS = 0;
	private const int TILE_FOREST = 1;

	// Logical Grid
	private int[,] _grid;
	private Node2D _terrainContainer;

	public override void _Ready()
	{
		_terrainContainer = new Node2D();
		AddChild(_terrainContainer);
		// Init Grid
		_grid = new int[MAP_WIDTH, MAP_HEIGHT];
	}

	public List<ControlPoint> GenerateMapWithCPs(int defenderId, int attackerId)
	{
		GD.Print("Generating Networked Map...");

		// Reset
		_terrainContainer.QueueFree();
		_terrainContainer = new Node2D();
		AddChild(_terrainContainer);
		_grid = new int[MAP_WIDTH, MAP_HEIGHT];

		var cps = new List<ControlPoint>();
		var rng = new Random();

		// 1. Fill with Forest (Blocked)
		for (int x = 0; x < MAP_WIDTH; x++)
		{
			for (int y = 0; y < MAP_HEIGHT; y++)
			{
				_grid[x, y] = TILE_FOREST;
			}
		}

		// 2. Place Nodes (Control Points)
		var defHQ = CreateCP(new Vector2I(3, MAP_HEIGHT / 2), ControlPoint.CPType.HQ, defenderId);
		cps.Add(defHQ);

		var attHQ = CreateCP(new Vector2I(MAP_WIDTH - 4, MAP_HEIGHT / 2), ControlPoint.CPType.HQ, attackerId);
		cps.Add(attHQ);

		int numPoints = 6;
		for (int i = 0; i < numPoints; i++)
		{
			int x = rng.Next(6, MAP_WIDTH - 6);
			int y = rng.Next(3, MAP_HEIGHT - 3);
			Vector2I pos = new Vector2I(x, y);

			if (cps.Any(c => c.Position.DistanceTo(GridToWorld(pos)) < 200)) continue; // Increased distance for larger tiles

			var type = rng.NextDouble() > 0.5 ? ControlPoint.CPType.SupplyDepot : ControlPoint.CPType.Outpost;
			cps.Add(CreateCP(pos, type, 0));
		}

		// 3. Connect Nodes & Carve Paths
		foreach (var cp in cps)
		{
			var neighbors = cps
				.Where(c => c != cp)
				.OrderBy(c => c.Position.DistanceSquaredTo(cp.Position))
				.Take(2);

			foreach (var neighbor in neighbors)
			{
				cp.AddConnection(neighbor);
				CarvePath(cp.GridPosition, neighbor.GridPosition);
			}
		}

		// 4. Render
		RenderTerrain();

		return cps;
	}

	private void RenderTerrain()
	{
		// Load Textures
		var texFloor = GD.Load<Texture2D>("res://assets/art/battle/Terrain/Floor.png");
		var texForest = GD.Load<Texture2D>("res://assets/art/battle/Terrain/Forest.png");

		// Dictionary for directional forests
		var texForestDir = new Dictionary<string, Texture2D>();
		string[] dirs = { "N", "S", "E", "W", "NE", "NW", "SE", "SW" };
		foreach (var d in dirs)
		{
			texForestDir[d] = GD.Load<Texture2D>($"res://assets/art/battle/Terrain/Forest{d}.png");
		}

		for (int x = 0; x < MAP_WIDTH; x++)
		{
			for (int y = 0; y < MAP_HEIGHT; y++)
			{
				var sprite = new Sprite2D();
				sprite.Position = new Vector2(x * TILE_SIZE, y * TILE_SIZE);
				sprite.Centered = false; // Easier logic

				if (_grid[x, y] == TILE_PLAINS)
				{
					sprite.Texture = texFloor;
				}
				else // Forest
				{
					// Auto-tiling Logic
					// Check neighbors for PLAINS (Floor)
					bool n = IsPlains(x, y - 1);
					bool s = IsPlains(x, y + 1);
					bool e = IsPlains(x + 1, y);
					bool w = IsPlains(x - 1, y);

					// Determine texture
					string suffix = "";

					// Corners
					if (n && e) suffix = "NE";
					else if (n && w) suffix = "NW";
					else if (s && e) suffix = "SE";
					else if (s && w) suffix = "SW";
					// Cardinals
					else if (n) suffix = "N";
					else if (s) suffix = "S";
					else if (e) suffix = "E";
					else if (w) suffix = "W";

					if (suffix != "" && texForestDir.ContainsKey(suffix))
					{
						sprite.Texture = texForestDir[suffix];
					}
					else
					{
						sprite.Texture = texForest; // Surrounded by forest
					}
				}

				sprite.Scale = new Vector2(TILE_SIZE / 32f, TILE_SIZE / 32f);
				sprite.TextureFilter = TextureFilterEnum.Nearest;
				_terrainContainer.AddChild(sprite);
			}
		}
	}

	private bool IsPlains(int x, int y)
	{
		if (x < 0 || x >= MAP_WIDTH || y < 0 || y >= MAP_HEIGHT) return false;
		return _grid[x, y] == TILE_PLAINS;
	}

	private ControlPoint CreateCP(Vector2I gridPos, ControlPoint.CPType type, int ownerId)
	{
		var cp = new ControlPoint();
		AddChild(cp); // Add to Grid, but ensure it draws ON TOP of terrain
					  // Since we add terrain to _terrainContainer, and CP to this (Node2D), 
					  // if _terrainContainer is added first, CPs will be on top.

		cp.Initialize(gridPos, type, ownerId);
		// Use Local Coordinates. GridToWorld returns Global, which causes double-offset if Parent is moved.
		cp.Position = new Vector2(gridPos.X * TILE_SIZE, gridPos.Y * TILE_SIZE) + new Vector2(TILE_SIZE / 2f, TILE_SIZE / 2f);

		CarveArea(gridPos, 2);

		return cp;
	}

	private void CarvePath(Vector2I start, Vector2I end)
	{
		var points = GetLine(start, end);
		foreach (var p in points)
		{
			CarveArea(p, 1);
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
					_grid[pos.X, pos.Y] = TILE_PLAINS;
				}
			}
		}
	}

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
		if (coords.X < 0 || coords.X >= MAP_WIDTH || coords.Y < 0 || coords.Y >= MAP_HEIGHT) return false;
		return _grid[coords.X, coords.Y] == TILE_PLAINS;
	}

	public int GetMovementCost(Vector2I coords)
	{
		return 1;
	}

	public Vector2I WorldToGrid(Vector2 worldPos)
	{
		Vector2 local = ToLocal(worldPos);
		return new Vector2I((int)(local.X / TILE_SIZE), (int)(local.Y / TILE_SIZE));
	}

	public Vector2 GridToWorld(Vector2I gridPos)
	{
		// Top-left of tile
		return ToGlobal(new Vector2(gridPos.X * TILE_SIZE, gridPos.Y * TILE_SIZE));
	}

	// A* Pathfinding
	public List<Vector2I> GetPath(Vector2I start, Vector2I end)
	{
		if (!IsWalkable(end))
		{
			// If target is obstacle, try to find nearest walkable neighbor
			var validNeighbors = GetNeighbors(end).Where(n => IsWalkable(n)).OrderBy(n => n.DistanceTo(start));
			if (validNeighbors.Any())
			{
				end = validNeighbors.First();
			}
			else
			{
				return null;
			}
		}

		var openSet = new PriorityQueue<Vector2I, float>();
		openSet.Enqueue(start, 0);

		var cameFrom = new Dictionary<Vector2I, Vector2I>();
		var gScore = new Dictionary<Vector2I, float> { { start, 0 } };
		var fScore = new Dictionary<Vector2I, float> { { start, start.DistanceTo(end) } };

		var openSetHash = new HashSet<Vector2I> { start };

		while (openSet.Count > 0)
		{
			var current = openSet.Dequeue();
			openSetHash.Remove(current);

			if (current == end)
			{
				return ReconstructPath(cameFrom, current);
			}

			foreach (var neighbor in GetNeighbors(current))
			{
				if (!IsWalkable(neighbor)) continue;

				float tentativeGScore = gScore[current] + 1; // Cost is 1

				if (!gScore.ContainsKey(neighbor) || tentativeGScore < gScore[neighbor])
				{
					cameFrom[neighbor] = current;
					gScore[neighbor] = tentativeGScore;
					float f = tentativeGScore + neighbor.DistanceTo(end); // Heuristic: Euclidean
					fScore[neighbor] = f;

					if (!openSetHash.Contains(neighbor))
					{
						openSet.Enqueue(neighbor, f);
						openSetHash.Add(neighbor);
					}
				}
			}
		}

		return null; // No path found
	}

	private List<Vector2I> ReconstructPath(Dictionary<Vector2I, Vector2I> cameFrom, Vector2I current)
	{
		var totalPath = new List<Vector2I> { current };
		while (cameFrom.ContainsKey(current))
		{
			current = cameFrom[current];
			totalPath.Insert(0, current);
		}
		// Remove start node from path, as we are already there
		if (totalPath.Count > 0) totalPath.RemoveAt(0);
		return totalPath;
	}

	private List<Vector2I> GetNeighbors(Vector2I pos)
	{
		var list = new List<Vector2I>();
		// 4-way movement
		Vector2I[] dirs = { Vector2I.Up, Vector2I.Down, Vector2I.Left, Vector2I.Right };
		foreach (var d in dirs)
		{
			list.Add(pos + d);
		}
		return list;
	}
}
