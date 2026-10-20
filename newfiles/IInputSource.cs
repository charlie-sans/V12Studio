using System.Collections.Generic;
using System.Numerics;

namespace V12.Core.Input
{
    /// <summary>
    /// Generic per-frame input state from any source (host renderer, OS input system).
    /// No editor-specific keybindings — just raw state. The editor (or game) maps
    /// this to its own actions.
    /// </summary>
    public interface IInputSource
    {
        InputSnapshot GetSnapshot();
    }

    /// <summary>
    /// Raw input state for one frame, in window coordinates.
    /// Consumers (editor, game, UI) interpret these keys/buttons as needed.
    /// </summary>
    public struct InputSnapshot
    {
        /// <summary>Mouse position in window/screen space.</summary>
        public Vector2 MousePosition;

        /// <summary>Per-button pressed state: index = button id.</summary>
        public bool LeftDown;
        public bool RightDown;
        public bool MiddleDown;

        /// <summary>Absolute wheel value (increases on scroll-up).
        /// Consumers derive per-frame deltas.</summary>
        public int Wheel;

        /// <summary>Pressed keys as a set (fast lookup).</summary>
        public HashSet<int> PressedKeys;

        /// <summary>Window has input focus.</summary>
        public bool WindowActive;

        /// <summary>Host has mouse captured (hidden + confined).</summary>
        public bool MouseLocked;
    }
}
