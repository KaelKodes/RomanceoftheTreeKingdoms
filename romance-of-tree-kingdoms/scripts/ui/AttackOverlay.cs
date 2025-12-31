using Godot;
using System.Collections.Generic;

public partial class AttackOverlay : Control
{
    public class AttackVisual
    {
        public string FromCity;
        public string ToCity;
        public Color FactionColor;
    }

    private WorldMap _map;
    private List<AttackVisual> _attacks = new List<AttackVisual>();

    public void Init(WorldMap map)
    {
        _map = map;
        // Make sure we don't block clicks on cities underneath
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void UpdateAttacks(List<AttackVisual> attacks)
    {
        _attacks = attacks;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_map == null || _attacks == null) return;

        var cityButtons = _map.GetCityButtons();

        foreach (var att in _attacks)
        {
            if (cityButtons.ContainsKey(att.FromCity) && cityButtons.ContainsKey(att.ToCity))
            {
                var btnFrom = cityButtons[att.FromCity];
                var btnTo = cityButtons[att.ToCity];

                // Positions relative to this overlay (assuming overlay covers same area as map container or is child)
                // If this overlay is child of WorldMap (Control), and Buttons are in CityContainer.
                // We need GlobalPosition conversion to be safe, or local if same parent.
                // Let's use logic from RouteOverlay:

                Vector2 start = btnFrom.GlobalPosition - GlobalPosition + btnFrom.Size / 2;
                Vector2 end = btnTo.GlobalPosition - GlobalPosition + btnTo.Size / 2;

                DrawBattleArrow(start, end, att.FactionColor);
            }
        }
    }

    private void DrawBattleArrow(Vector2 start, Vector2 end, Color color)
    {
        // 1. Calculate Control Point for Arch
        // Midpoint
        Vector2 mid = (start + end) / 2;
        // Direction vector
        Vector2 dir = end - start;
        // Normal vector (perpendicular)
        Vector2 normal = new Vector2(-dir.Y, dir.X).Normalized();

        // Offset: Fixed amount or based on distance? Let's do distance-based for consistent arc shape.
        float dist = start.DistanceTo(end);
        float archHeight = Mathf.Clamp(dist * 0.2f, 30f, 100f);

        // Determine "Up" or "Down" arch? 
        // Always arch "Up" relative to the map? Or always "Left"?
        // Let's try consistently "Up" (-Y) by checking dot product or just forcing Y negative?
        // Actually, normal is just perpendicular. Let's start with simple normal.

        Vector2 control = mid + normal * archHeight;

        // 2. Plot Curve Points
        int segments = 20;
        var points = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            // Quadratic Bezier: (1-t)^2 P0 + 2(1-t)t P1 + t^2 P2
            float u = 1 - t;
            float tt = t * t;
            float uu = u * u;

            points[i] = (uu * start) + (2 * u * t * control) + (tt * end);
        }

        // 3. Draw Curve
        for (int i = 0; i < segments; i++)
        {
            DrawLine(points[i], points[i + 1], color, 4.0f, true);
        }

        // 4. Draw Arrowhead at End
        // Direction at end (Tangent) -> Derivative: 2(1-t)(P1-P0) + 2t(P2-P1)? 
        // Or just vector from last point to end point.
        Vector2 arrowDir = (points[segments] - points[segments - 1]).Normalized();

        // Arrowhead shape
        float arrowSize = 25f;
        Vector2 arrowP1 = end - arrowDir * arrowSize + arrowDir.Rotated(Mathf.Pi / 2) * (arrowSize * 0.5f);
        Vector2 arrowP2 = end - arrowDir * arrowSize - arrowDir.Rotated(Mathf.Pi / 2) * (arrowSize * 0.5f);

        var triangle = new Vector2[] { end, arrowP1, arrowP2 };
        DrawColoredPolygon(triangle, color);
    }
}
