using Godot;
using System.Collections.Generic;

public partial class RouteOverlay : Control
{
    private WorldMap _map;
    private List<RouteData> _routes = new List<RouteData>();

    public void Init(WorldMap map, List<RouteData> routes)
    {
        _map = map;
        _routes = routes;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_map == null || _routes == null) return;

        var cityButtons = _map.GetCityButtons();
        foreach (var r in _routes)
        {
            if (cityButtons.ContainsKey(r.From) && cityButtons.ContainsKey(r.To))
            {
                var btnFrom = cityButtons[r.From];
                var btnTo = cityButtons[r.To];

                // Calculate positions relative to this overlay
                var start = btnFrom.GlobalPosition - GlobalPosition + btnFrom.Size / 2;
                var end = btnTo.GlobalPosition - GlobalPosition + btnTo.Size / 2;

                DrawLine(start, end, Colors.Gray, 5.0f);
            }
        }
    }
}
