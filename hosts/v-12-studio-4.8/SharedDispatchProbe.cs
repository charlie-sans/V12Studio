using Godot;
using V12TwoDog.Godot.UI;

namespace V12Studio.Godot
{
    /// <summary>
    /// --probe-shared: does native->managed virtual dispatch work for
    /// SharedScriptProbe, a GodotObject subclass compiled in Nova.Shared
    /// (plain GodotSharp package, no Godot.SourceGenerators)? Frame-driven
    /// like InputProbeNode: PushInput delivers on the following frame.
    /// </summary>
    internal static class SharedDispatchProbe
    {
        private static SharedScriptProbe? _probe;

        public static void Start(Node root)
        {
            _probe = new SharedScriptProbe();
            root.AddChild(_probe);
            GD.Print("[SharedProbe] started (type lives in Nova.Shared)");
        }

        public static void Push(Node root, Vector2 at)
        {
            if (_probe == null) return;
            var vp = _probe.GetViewport();
            GD.Print("[SharedProbe] pushing left-press to viewport");
            vp.PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = true,
                Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left,
            }, true);
        }

        public static void Report()
        {
            if (_probe == null) { GD.Print("[SharedProbe] no instance"); return; }
            GD.Print($"[SharedProbe] result: ready={_probe.ReadyCount} " +
                     $"process={_probe.ProcessCount} input={_probe.InputCount} " +
                     $"last={_probe.LastInput}");
        }
    }
}
