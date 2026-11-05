using System;
using Godot;

namespace V12Studio.Godot
{
    /// <summary>
    /// Standalone (--probe-tree-drag) isolation of the drag plumbing: adds a
    /// bare Tree subclass (not the studio one) and pushes mouse events at it,
    /// reporting which of _Input / _GuiInput fire. Removes the studio scene
    /// from the picture so the only variable is the Tree control itself.
    /// </summary>
    internal partial class TreeDragIsolation : Control
    {
        private sealed partial class ProbeTree : Tree
        {
            public int InputHits;
            public int GuiHits;

            public override void _Input(InputEvent @event)
            {
                InputHits++;
                GD.Print($"[ProbeTree] _Input {@event.GetType().Name}");
            }

            public override void _GuiInput(InputEvent @event)
            {
                GuiHits++;
                GD.Print($"[ProbeTree] _GuiInput {@event.GetType().Name}");
            }
        }

        private static ProbeTree? _tree;
        private static int _step;

        public static bool Tick(Node rootViewportNode, int frame)
        {
            if (_tree == null)
            {
                if (frame < 5) return false;
                var holder = new Control { Position = new Vector2(300, 300), Size = new Vector2(300, 300) };
                rootViewportNode.AddChild(holder);
                _tree = new ProbeTree { HideRoot = true };
                _tree.SetProcessInput(true);
                holder.AddChild(_tree);
                _tree.Size = new Vector2(300, 300);

                var r = _tree.GetRoot();
                var item = r != null ? _tree.CreateItem(r) : _tree.CreateItem();
                item.SetText(0, "Hello");
                GD.Print($"[ProbeTreeDiag] viewport={_tree.GetViewport().GetPath()} procInput={_tree.IsProcessingInput()}");
                return false;
            }

            var at = new Vector2(450, 315); // inside the tree row area
            switch (_step)
            {
                case 0:
                    Push(new InputEventMouseButton
                    {
                        ButtonIndex = MouseButton.Left, Pressed = true,
                        Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left,
                    });
                    break;
                case 1:
                    Push(new InputEventMouseMotion
                    {
                        Position = at + new Vector2(10, 0),
                        GlobalPosition = at + new Vector2(10, 0),
                        ButtonMask = MouseButtonMask.Left, Relative = new Vector2(10, 0),
                    });
                    break;
                case 2:
                    Push(new InputEventMouseButton
                    {
                        ButtonIndex = MouseButton.Left, Pressed = false,
                        Position = at, GlobalPosition = at,
                    });
                    break;
                case 3:
                    GD.Print($"[ProbeTreeDiag] result _Input={_tree.InputHits} _GuiInput={_tree.GuiHits}");
                    return true;
            }
            _step++;
            return false;
        }

        private static void Push(InputEvent ev)
            => _tree!.GetViewport().PushInput(ev, true);
    }
}
