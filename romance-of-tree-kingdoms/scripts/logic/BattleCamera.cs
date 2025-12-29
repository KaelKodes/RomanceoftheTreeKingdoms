using Godot;
using System;

public partial class BattleCamera : Camera2D
{
	[Export] public float ZoomSpeed = 0.1f;
	[Export] public Vector2 MinZoom = new Vector2(0.5f, 0.5f);
	[Export] public Vector2 MaxZoom = new Vector2(2.0f, 2.0f);

	// Pan Speed (Future proofing)
	[Export] public float PanSpeed = 500.0f;

	public override void _Ready()
	{
		// Ensure we are the active camera
		MakeCurrent();
		// Center on the 30x20 64px grid (1920x1280)
		Position = new Vector2(960, 640);
	}

	public override void _Process(double delta)
	{
		Vector2 moveDir = Vector2.Zero;

		if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) moveDir.Y -= 1;
		if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) moveDir.Y += 1;
		if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) moveDir.X -= 1;
		if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) moveDir.X += 1;

		if (moveDir != Vector2.Zero)
		{
			Position += moveDir.Normalized() * PanSpeed * (float)delta * (1.0f / Zoom.X);
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.Pressed)
			{
				if (mb.ButtonIndex == MouseButton.WheelUp)
				{
					Vector2 newZoom = Zoom + new Vector2(ZoomSpeed, ZoomSpeed);
					Zoom = newZoom.Clamp(MinZoom, MaxZoom);
					GetViewport().SetInputAsHandled();
				}
				else if (mb.ButtonIndex == MouseButton.WheelDown)
				{
					Vector2 newZoom = Zoom - new Vector2(ZoomSpeed, ZoomSpeed);
					Zoom = newZoom.Clamp(MinZoom, MaxZoom);
					GetViewport().SetInputAsHandled();
				}
			}
		}
	}
}
