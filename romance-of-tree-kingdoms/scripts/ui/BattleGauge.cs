using Godot;
using System;
using System.Linq;

public partial class BattleGauge : Control
{
    [Export] public Color LeftColor = Colors.Firebrick;
    [Export] public Color RightColor = Colors.DodgerBlue;
    [Export] public Color MoraleHighColor = Colors.Green;
    [Export] public Color MoraleNormalColor = Colors.White;
    [Export] public Color MoraleLowColor = Colors.Red;

    private Control _leftBar;
    private Control _rightBar;
    private Control _separator;

    private Control[] _leftMoraleBars = new Control[5];
    private Control[] _rightMoraleBars = new Control[5];

    private Label _leftCountLabel;
    private Label _rightCountLabel;

    private Tween _gaugeTween;
    private float _currentRatio = 0.5f;

    public override void _Ready()
    {
        // Locate nodes based on the expected structure
        _leftBar = GetNode<Control>("%LeftBar");
        _rightBar = GetNode<Control>("%RightBar");
        _separator = GetNode<Control>("%Separator");

        _leftCountLabel = GetNode<Label>("%LeftCount");
        _rightCountLabel = GetNode<Label>("%RightCount");

        // Find Morale Bars (assuming naming convention MoraleL1..5, MoraleR1..5 or just by index in container)
        var lContainer = GetNode<Control>("%LeftMoraleContainer");
        var rContainer = GetNode<Control>("%RightMoraleContainer");

        for (int i = 0; i < 5; i++)
        {
            // Try to find by index if children exist, otherwise by name
            if (i < lContainer.GetChildCount()) _leftMoraleBars[i] = lContainer.GetChild(i) as Control;
            if (i < rContainer.GetChildCount()) _rightMoraleBars[i] = rContainer.GetChild(i) as Control;
        }

        UpdateVisuals(0.5f);
    }

    public void UpdateStats(int leftTroops, int rightTroops, float leftMorale, float rightMorale)
    {
        int total = leftTroops + rightTroops;
        float ratio = 0.5f;
        if (total > 0)
        {
            ratio = (float)leftTroops / total;
        }

        // Labels
        _leftCountLabel.Text = $"{leftTroops}";
        _rightCountLabel.Text = $"{rightTroops}";

        // Smooth Gauge
        if (_gaugeTween != null && _gaugeTween.IsRunning()) _gaugeTween.Kill();
        _gaugeTween = CreateTween();
        _gaugeTween.TweenMethod(Callable.From<float>(UpdateVisuals), _currentRatio, ratio, 0.5f)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);

        _currentRatio = ratio;

        // Update Morale
        // Map 0-100 morale to 0-5 bars
        int lBars = Mathf.CeilToInt((leftMorale / 100.0f) * 5);
        int rBars = Mathf.CeilToInt((rightMorale / 100.0f) * 5);

        UpdateMoraleBars(_leftMoraleBars, lBars);
        UpdateMoraleBars(_rightMoraleBars, rBars);
    }

    private void UpdateVisuals(float leftRatio)
    {
        // 0.0 = All Right, 1.0 = All Left
        // Left Bar Anchor: Left=0, Right=Ratio
        _leftBar.AnchorRight = leftRatio;

        // Right Bar Anchor: Left=Ratio, Right=1
        _rightBar.AnchorLeft = leftRatio;

        // Separator centered on Ratio
        _separator.AnchorLeft = leftRatio;
        _separator.AnchorRight = leftRatio;
        // Position adjustment is handled by center anchor preset usually, but ensuring it sticks
    }

    private void UpdateMoraleBars(Control[] bars, int activeCount)
    {
        // 1 is considering running (Red), 3 Normal (White), 5 Max (Green)
        Color statusColor = MoraleNormalColor;
        if (activeCount <= 1) statusColor = MoraleLowColor;
        if (activeCount >= 4) statusColor = MoraleHighColor;

        for (int i = 0; i < 5; i++)
        {
            if (bars[i] == null) continue;

            // Bars fill from bottom up usually, so index 0 is bottom? Or top?
            // Let's assume index 4 is Top (High Morale) and 0 is Bottom (Base).
            // If we have 3 bars active (Normal), bars 0, 1, 2 are lit. 3, 4 are dim.

            bool isActive = i < activeCount;
            bars[i].Modulate = isActive ? statusColor : new Color(0.2f, 0.2f, 0.2f, 0.5f);
        }
    }
}
