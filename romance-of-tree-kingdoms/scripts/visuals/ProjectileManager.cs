using Godot;
using System;
using System.Collections.Generic;

public partial class ProjectileManager : Node2D
{
    public static ProjectileManager Instance { get; private set; }

    private class Projectile
    {
        public Vector2 Start;
        public Vector2 End;
        public float T; // 0 to 1
        public Color Color;
        public float Speed;
        public float ArcHeight;
    }

    private List<Projectile> _activeProjectiles = new List<Projectile>();

    public override void _Ready()
    {
        Instance = this;
        // Ensure we are drawn above units
        ZIndex = 10;
    }

    public void LaunchArrow(Vector2 start, Vector2 end, Color color)
    {
        float dist = start.DistanceTo(end);
        _activeProjectiles.Add(new Projectile
        {
            Start = start,
            End = end,
            T = 0,
            Color = color,
            Speed = 400.0f / dist, // Time to travel = 0.25s at 100px? Adjusted for distance.
            ArcHeight = Mathf.Clamp(dist * 0.25f, 20, 60)
        });
    }

    public void LaunchVolley(Vector2 startCenter, Vector2 endCenter, Color color, int count = 10, float spread = 20.0f)
    {
        var rng = new Random();
        float dist = startCenter.DistanceTo(endCenter);

        for (int i = 0; i < count; i++)
        {
            // Randomize start/end within spread
            Vector2 startOffset = new Vector2((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * spread * 2; // e.g. -20 to +20
            Vector2 endOffset = new Vector2((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * spread * 2;

            // Vary speed slighly for natural feel
            float speedVar = 0.8f + (float)rng.NextDouble() * 0.4f;

            _activeProjectiles.Add(new Projectile
            {
                Start = startCenter + startOffset,
                End = endCenter + endOffset,
                T = 0 - (float)rng.NextDouble() * 0.2f, // Stagger launch slightly
                Color = color,
                Speed = (400.0f / dist) * speedVar,
                ArcHeight = Mathf.Clamp(dist * 0.25f, 20, 80) + (float)rng.NextDouble() * 15
            });
        }
    }

    public override void _Process(double delta)
    {
        if (_activeProjectiles.Count == 0) return;

        for (int i = _activeProjectiles.Count - 1; i >= 0; i--)
        {
            var p = _activeProjectiles[i];
            p.T += (float)delta * p.Speed * 2.0f; // Adjust speed multiplier as needed

            if (p.T >= 1.0f)
            {
                _activeProjectiles.RemoveAt(i);
            }
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var p in _activeProjectiles)
        {
            Vector2 current = GetArcPosition(p.Start, p.End, p.ArcHeight, p.T);

            // Draw a small "tracer" line
            float prevT = Mathf.Max(0, p.T - 0.05f);
            Vector2 prev = GetArcPosition(p.Start, p.End, p.ArcHeight, prevT);

            DrawLine(prev, current, p.Color, 2.0f, true);
            // Optional: Draw a dot at the tip
            DrawCircle(current, 1.5f, p.Color);
        }
    }

    private Vector2 GetArcPosition(Vector2 start, Vector2 end, float height, float t)
    {
        Vector2 mid = start.Lerp(end, t);
        // Add a simple quadratic arc (Up is -Y in Godot)
        float arc = 4 * height * t * (1 - t);
        return mid + new Vector2(0, -arc);
    }
}
