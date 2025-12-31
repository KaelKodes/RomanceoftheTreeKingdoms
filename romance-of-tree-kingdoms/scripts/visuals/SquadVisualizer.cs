using Godot;
using System;
using System.Collections.Generic;

public partial class SquadVisualizer : Node2D
{
    private MultiMeshInstance2D _multiMeshInstance;
    private MultiMesh _multiMesh;
    private FormationShape _formation;
    private int _maxTroops;
    private int _currentTroops;
    private string _combatPos;
    private Label _officerMarker;

    private BattleOfficer _officerData;
    private const int MAX_DOTS_PER_SQUAD = 1000;

    public void Initialize(BattleOfficer officer)
    {
        _officerData = officer;
        _maxTroops = officer.MaxTroops;
        _formation = officer.Formation;
        _combatPos = officer.CombatPosition;

        SetupMultiMesh(officer.MainTroopType);
        SetupOfficerMarker();
        UpdateTroops(officer.Troops);
    }

    private void SetupOfficerMarker()
    {
        if (_officerMarker != null) return;
        _officerMarker = new Label();

        string marker = "X";
        if (_officerData.OfficerType == TroopType.Archer) marker = "O";
        else if (_officerData.OfficerType == TroopType.Cavalry) marker = "C";

        _officerMarker.Text = marker;
        _officerMarker.HorizontalAlignment = HorizontalAlignment.Center;
        _officerMarker.VerticalAlignment = VerticalAlignment.Center;

        // Style it to be visible
        var fontSetting = new LabelSettings();
        fontSetting.FontSize = 24;
        fontSetting.FontColor = Colors.White;
        fontSetting.OutlineSize = 4;
        fontSetting.OutlineColor = Colors.Black;
        _officerMarker.LabelSettings = fontSetting;

        _officerMarker.Size = new Vector2(40, 40);
        _officerMarker.Position = new Vector2(-20, -20); // Center on pivot
        AddChild(_officerMarker);
    }

    private void SetupMultiMesh(TroopType type)
    {
        _multiMeshInstance = new MultiMeshInstance2D();
        _multiMesh = new MultiMesh();

        // Setup Mesh (Simple Quad/Dot)
        var mesh = new QuadMesh();
        mesh.Size = new Vector2(3, 3);
        _multiMesh.Mesh = mesh;
        _multiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
        _multiMesh.UseColors = true;
        _multiMesh.InstanceCount = 0; // Init empty

        _multiMeshInstance.Multimesh = _multiMesh;
        AddChild(_multiMeshInstance);
    }

    public void UpdateTroops(int currentCount)
    {
        _currentTroops = currentCount;

        // 1. Calculate how many dots per active slot (5x5 grid now)
        var slots = FormationHelper.GetSlots(_formation);
        List<Vector2I> activeSlots = new List<Vector2I>();
        for (int x = 0; x < 5; x++)
            for (int y = 0; y < 5; y++)
                if (slots[x, y]) activeSlots.Add(new Vector2I(x, y));

        if (activeSlots.Count == 0) return;

        // Spread troops more thin if needed, but capped at MAX_DOTS
        int desiredDots = Math.Min(MAX_DOTS_PER_SQUAD, _currentTroops);
        int dotsPerSlot = desiredDots / activeSlots.Count;
        int totalDots = dotsPerSlot * activeSlots.Count;

        _multiMesh.InstanceCount = totalDots;
        _multiMesh.VisibleInstanceCount = totalDots;

        int dotIndex = 0;
        Random rng = new Random();

        // 5x5 Grid Spacing:
        // We want this to cover a large area (the "Ocean" feel)
        // Let's go WIDER: 24px per slot = 120px total spread.
        float slotSize = 24.0f;
        Vector2 slotOffset = new Vector2(slotSize, slotSize);

        foreach (var slot in activeSlots)
        {
            // Officer is at 2,2 Center.
            float xPos = (slot.X - 2) * slotOffset.X;
            float yPos = (slot.Y - 2) * slotOffset.Y;
            Vector2 slotCenter = new Vector2(xPos, yPos);

            // Large spread within slot to create overlap
            float spread = 12.0f;

            for (int i = 0; i < dotsPerSlot; i++)
            {
                float rX = (float)(rng.NextDouble() * 2 - 1) * spread;
                float rY = (float)(rng.NextDouble() * 2 - 1) * spread;

                // Add a bit of jitter to the slotCenter itself for "fluid" feel
                float jX = (float)(rng.NextDouble() * 2 - 1) * 2.0f;
                float jY = (float)(rng.NextDouble() * 2 - 1) * 2.0f;

                Transform2D t = new Transform2D(0, slotCenter + new Vector2(rX + jX, rY + jY));
                _multiMesh.SetInstanceTransform2D(dotIndex, t);
                _multiMesh.SetInstanceColor(dotIndex, Colors.White);

                dotIndex++;
            }
        }

        // Update Officer Marker Position
        if (_officerMarker != null)
        {
            // Position based on _combatPos (Front, Middle, Rear)
            // But now we want the *Troops* to be offset from the *Officer* (Center)
            // If Officer is "Front" (Vanguard), Troops should be BEHIND?
            // "CombatPosition" usually describes where the Commander is relative to the army.
            // Front = Commander moves first, Army follows.
            // Rear = Army moves, Commander stays back.

            // Current Logic is simplified:
            // Officer is always (0,0) of this Node.
            // We shift the MultiMesh (Troops) relative to us.

            float yOff = 0;
            if (_combatPos == "Front") yOff = slotSize * 2; // Troops are behind
            else if (_combatPos == "Rear") yOff = -slotSize * 2; // Troops are front

            // We apply this offset to the Multimesh Node itself, not just the dots
            _multiMeshInstance.Position = new Vector2(0, yOff);

            // Marker stays at 0,0 (The Anchor)
            _officerMarker.Position = new Vector2(-20, -20);
            _officerMarker.Modulate = _multiMeshInstance.Modulate;
        }
    }

    public void SetFacing(Vector2 direction)
    {
        // Rotate the SQUAD (MultiMesh) to face the movement direction
        // The Officer Marker (Text) should probably stay upright or rotate too?
        // Let's rotate the whole visualizer to match movement

        if (direction.LengthSquared() > 0.01f)
        {
            Rotation = direction.Angle() - Mathf.Pi / 2; // Assuming "Up" is 0 rotation, but Godot 0 is Right. 
            // Actually, let's keep it simple: Map "Up" (-Y) to orientation. 
            // Standard Godot: 0 = Right. +90 = Down. 
            // If we want the army to face the direction, we just rotate 'this'.
            Rotation = direction.Angle() + Mathf.Pi / 2; // Align "Up" (-Y) to direction

            // Keep text upright?
            if (_officerMarker != null)
            {
                _officerMarker.Rotation = -Rotation;
            }
        }
    }

    public void UpdateSelectionVisuals(bool isAlly, bool isSelected, bool isPlayer = false, bool isRouted = false)
    {
        // Selection takes priority
        if (isSelected)
        {
            _multiMeshInstance.Modulate = Colors.Yellow;
            if (_officerMarker != null) _officerMarker.Modulate = Colors.Yellow;
            return;
        }

        if (isRouted)
        {
            _multiMeshInstance.Modulate = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            if (_officerMarker != null) _officerMarker.Modulate = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            return;
        }

        Color mod;
        if (isPlayer) mod = Colors.Green;
        else mod = isAlly ? Colors.Cyan : Colors.Red;

        _multiMeshInstance.Modulate = mod;
        if (_officerMarker != null) _officerMarker.Modulate = mod;
    }
}
