using System;
using System.Collections.Generic;
using Godot;
using V12.UI;
using V12Studio.Core;
using V12Studio.Editor;
using V12TwoDog.Godot.UI;

namespace V12Studio.Godot
{
    /// <summary>
    /// Debug aid (--probe-drag): drives synthetic hierarchy drags through the
    /// real Godot input pipeline at the real tree rows and reports the world
    /// order after each one. Frame-driven on purpose: Viewport.PushInput
    /// dispatches its events on the FOLLOWING frame, so pushing at the quit
    /// frame (the first version did that) meant the events were never seen.
    /// </summary>
    internal static partial class DragProbe
    {
        private static Tree? _tree;
        private static StudioSession? _session;
        private static string? _fromText;
        private static string? _toText;
        private static int _scenario;
        private static int _step;

        private static readonly (string Name, float Band)[] Scenarios =
        {
            ("After(bottom band)", 0.85f),
            ("Before(top band)", 0.15f),
            ("Child(middle band)", 0.5f),
        };

        /// <summary>Advance one frame. Returns true when finished.</summary>
        public static bool Tick(StudioShell shell, StudioSession session)
        {
            var treeView = shell.Hierarchy.Tree;
            if (treeView is not GodotTreeView gtv)
            {
                GD.PrintErr("[DragProbe] tree is not GodotTreeView");
                return true;
            }

            if (_tree == null)
            {
                _tree = gtv.NativeTree;
                _session = session;
                var root = _tree.GetRoot();
                if (root == null || root.GetChildCount() < 2)
                {
                    GD.PrintErr("[DragProbe] need >=2 rows");
                    return true;
                }

                GD.Print($"[DragProbe] ==== drag probe ====");
                GD.Print($"[DragProbe] tree size={_tree.Size} global={_tree.GlobalPosition} " +
                         $"visible={_tree.Visible} mouseFilter={_tree.MouseFilter}");
                var rows = new List<TreeItem>();
                for (int i = 0; i < root.GetChildCount() && rows.Count < 4; i++)
                {
                    var c = root.GetChild(i);
                    if (c != null) rows.Add(c);
                }
                foreach (var r in rows)
                    GD.Print($"[DragProbe] row '{r.GetText(0)}' rect={_tree.GetItemAreaRect(r, 0, -1)}");

                // Reparents rebuild the tree and free every TreeItem, so the
                // probe must re-resolve rows by label on every tick — never
                // hold native refs across a drop.
                _fromText = rows[1].GetText(0);
                _toText = rows[0].GetText(0);
                GD.Print($"[DragProbe] will drag '{_fromText}' near '{_toText}'");
                Diagnose(_tree);
                return false;
            }

            if (_scenario >= Scenarios.Length)
            {
                DumpRoot();
                return true;
            }

            var fromItem = FindRow(_tree, _fromText!);
            var toItem = FindRow(_tree, _toText!);
            if (fromItem == null || toItem == null)
            {
                GD.PrintErr($"[DragProbe] row missing after rebuild: " +
                    $"from='{_fromText}' {(fromItem == null ? "MISSING" : "ok")} " +
                    $"to='{_toText}' {(toItem == null ? "MISSING" : "ok")}");
                DumpRoot();
                return true;
            }

            var (name, band) = Scenarios[_scenario];
            var fromPt = RowCenter(_tree, fromItem);
            var toRect = _tree.GetItemAreaRect(toItem, 0, -1);
            var toPt = new Vector2(
                _tree.GlobalPosition.X + toRect.Position.X + toRect.Size.X * 0.5f,
                _tree.GlobalPosition.Y + toRect.Position.Y + toRect.Size.Y * band);

            switch (_step)
            {
                case 0:
                    GD.Print($"[DragProbe] --- {name}: {fromPt} -> {toPt} ---");
                    Push(_tree, new InputEventMouseButton
                    {
                        ButtonIndex = MouseButton.Left, Pressed = true,
                        Position = fromPt, GlobalPosition = fromPt,
                        ButtonMask = MouseButtonMask.Left,
                    });
                    break;
                case 1:
                    Push(_tree, new InputEventMouseMotion
                    {
                        Position = fromPt + new Vector2(10, 0),
                        GlobalPosition = fromPt + new Vector2(10, 0),
                        ButtonMask = MouseButtonMask.Left, Relative = new Vector2(10, 0),
                    });
                    break;
                case 2:
                    Push(_tree, new InputEventMouseMotion
                    {
                        Position = toPt, GlobalPosition = toPt,
                        ButtonMask = MouseButtonMask.Left, Relative = toPt - fromPt,
                    });
                    break;
                case 3:
                    Push(_tree, new InputEventMouseButton
                    {
                        ButtonIndex = MouseButton.Left, Pressed = false,
                        Position = toPt, GlobalPosition = toPt,
                    });
                    break;
                case 4:
                    DumpRoot();
                    _scenario++;
                    _step = -1;
                    break;
            }

            _step++;
            return false;
        }

        private static void Push(Tree tree, InputEvent ev)
        {
            System.Console.WriteLine($"[DragDbg] Probe.Push {ev.GetType().Name} pos={(ev as InputEventMouse)?.Position} handledBefore={tree.GetViewport().IsInputHandled()}");
            tree.GetViewport().PushInput(ev, true);
            System.Console.WriteLine($"[DragDbg] Probe.Push done handledAfter={tree.GetViewport().IsInputHandled()}");
        }

        /// <summary>Isolate which viewport/tree the DragTree lives in vs the probe.</summary>
        private sealed partial class InputEcho : Node
        {
            public string Tag = "";
            public override void _Input(InputEvent @event)
            {
                if (@event is InputEventMouse)
                    System.Console.WriteLine($"[DragDbg] Echo({Tag}) _Input {@event.GetType().Name}");
            }
        }

        public static void Diagnose(Tree tree)
        {
            GD.Print($"[DragProbe] DragTree viewport={tree.GetViewport().GetType().Name} " +
                     $"vpPath={tree.GetViewport().GetPath()} windowType={tree.GetWindow().GetType().Name} " +
                     $"windowPath={tree.GetWindow().GetPath()} procInput={tree.IsProcessingInput()} " +
                     $"procUnhandled={tree.IsProcessingUnhandledInput()}");
            var parent = tree.GetParent();
            GD.Print($"[DragProbe] DragTree parent={parent?.GetType().Name} '{parent?.Name}' " +
                     $"parentProcInput={parent?.IsProcessingInput()}");

            void Walk(Node n)
            {
                if (n.IsProcessingInput())
                    GD.Print($"[DragProbe]   procInput node: {n.GetType().Name} '{n.Name}' path={n.GetPath()}");
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(tree.GetTree().Root);

            // Propagation experiment: echoes around the hierarchy DragTree.
            if (!_echoesAdded)
            {
                _echoesAdded = true;
                var childEcho = new InputEcho { Tag = "child-of-tree" };
                tree.AddChild(childEcho);
                var parentEcho = new InputEcho { Tag = "parent-sibling" };
                tree.GetParent().AddChild(parentEcho);
                var windowEcho = new InputEcho { Tag = "window-child" };
                tree.GetWindow().AddChild(windowEcho);
                GD.Print($"[DragProbe] echoes added");
            }
        }

        private static bool _echoesAdded;

        private static void DumpRoot()
        {
            var world = _session?.SceneWorld;
            if (world == null) return;
            var names = new List<string>();
            foreach (var el in world.Root) names.Add(el.Name);
            GD.Print($"[DragProbe] root order: {string.Join(", ", names)}");
        }

        private static TreeItem? FindRow(Tree tree, string text)
        {
            var root = tree.GetRoot();
            return root == null ? null : FindIn(root, text);
        }

        private static TreeItem? FindIn(TreeItem item, string text)
        {
            if (item.GetText(0) == text) return item;
            for (int i = 0; i < item.GetChildCount(); i++)
            {
                var child = item.GetChild(i);
                if (child == null) continue;
                var hit = FindIn(child, text);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Vector2 RowCenter(Tree tree, TreeItem item)
        {
            var r = tree.GetItemAreaRect(item, 0, -1);
            // GetItemAreaRect is tree-local; input events carry viewport coords.
            return tree.GlobalPosition + r.Position + r.Size * 0.5f;
        }
    }
}
