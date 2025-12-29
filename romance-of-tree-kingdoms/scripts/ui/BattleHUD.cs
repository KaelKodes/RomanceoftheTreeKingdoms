using Godot;
using System;

public partial class BattleHUD : CanvasLayer
{
    private BattleController _controller;

    public void Init(BattleController controller)
    {
        _controller = controller;
    }

    private Label _endingLabel;

    public override void _Ready()
    {
        // 1. Root Container (Top Right)
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        AddChild(margin);

        var hbox = new HBoxContainer();
        margin.AddChild(hbox);

        // 2. Buttons
        AddButton(hbox, "||", () => _controller.TogglePause());
        AddButton(hbox, "1x", () => _controller.SetSpeed(1.0f));
        AddButton(hbox, "2x", () => _controller.SetSpeed(2.0f));
        AddButton(hbox, "4x", () => _controller.SetSpeed(4.0f));

        // 3. Victory Label (Center)
        var centerContainer = new CenterContainer();
        centerContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        centerContainer.MouseFilter = Control.MouseFilterEnum.Ignore; // Click through
        AddChild(centerContainer);

        _endingLabel = new Label();
        _endingLabel.Text = "";
        _endingLabel.AddThemeFontSizeOverride("font_size", 64);
        _endingLabel.Hide();
        centerContainer.AddChild(_endingLabel);
    }

    private void AddButton(Container parent, string text, Action onClick)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(40, 40);
        btn.Pressed += () => onClick();
        parent.AddChild(btn);
    }

    public void ShowWinner(string text)
    {
        _endingLabel.Text = text;
        _endingLabel.Show();
    }
}
