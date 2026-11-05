using System;
using Godot;

namespace V12Studio.Godot
{
    /// <summary>
    /// Minimal probe (--probe-input): does Viewport.PushInput reach Node._Input
    /// and a Control's _GuiInput in this project's setup? Confirms the harness
    /// itself rather than guessing at Godot's plumbing.
    /// </summary>
    internal partial class InputProbeNode : Control
    {
        public int InputHits;
        public int GuiHits;

        private static InputProbeNode? _instance;

        public override void _Input(InputEvent @event)
        {
            InputHits++;
            GD.Print($"[InputProbeNode] _Input {(@event as InputEventMouse != null ? ((InputEventMouse)@event).Position.ToString() : @event.GetType().Name)}");
        }

        public override void _GuiInput(InputEvent @event)
        {
            GuiHits++;
            GD.Print($"[InputProbeNode] _GuiInput {(@event as InputEventMouse != null ? ((InputEventMouse)@event).Position.ToString() : @event.GetType().Name)}");
        }

        public static void Push(Node root, Vector2 at)
        {
            var probe = new InputProbeNode
            {
                Position = new Vector2(0, 0),
                Size = new Vector2(600, 600),
                MouseFilter = MouseFilterEnum.Stop,
            };
            root.AddChild(probe);
            _instance = probe;
            GD.Print($"[InputProbe] pushing press/motion/release at {at} to viewport");
            var vp = probe.GetViewport();
            vp.PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = true,
                Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left,
            }, true);
            vp.PushInput(new InputEventMouseMotion
            {
                Position = at + new Vector2(20, 0),
                GlobalPosition = at + new Vector2(20, 0),
                ButtonMask = MouseButtonMask.Left, Relative = new Vector2(20, 0),
            }, true);
            vp.PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = false,
                Position = at + new Vector2(20, 0), GlobalPosition = at + new Vector2(20, 0),
            }, true);
        }

        public static void Report()
        {
            if (_instance == null) { GD.Print("[InputProbe] no instance"); return; }
            GD.Print($"[InputProbe] result: _Input hits={_instance.InputHits} _GuiInput hits={_instance.GuiHits}");
        }
    }
}
