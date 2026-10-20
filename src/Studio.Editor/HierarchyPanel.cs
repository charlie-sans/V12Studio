using System;
using System.Collections.Generic;
using System.Linq;
using V12.Core.Core.Interfaces;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// Left "Hierarchy" panel: the scene tree with drag-reparent, inline
    /// rename, per-item context menu, and a small toolbar. Two-way bound to
    /// the session's selection. Tree items carry their IWorldElement as
    /// UserData — no id round-trips.
    /// </summary>
    public sealed class HierarchyPanel
    {
        private readonly StudioShell _shell;
        private readonly IUIProvider _ui;
        private readonly StudioSession _session;
        private readonly HierarchyOps _ops;

        private readonly ITreeView _tree;

        // Element → its current tree item. Rebuilt with the tree on every
        // HierarchyChanged pass; used only to bridge selection both ways.
        private readonly Dictionary<IWorldElement, ITreeItem> _items = new();
        private bool _syncing;

        public IPanel Panel { get; }

        /// <summary>Raised when the user picks "Add Component" for an element.</summary>
        public event Action<IWorldElement>? AddComponentRequested;

        public HierarchyPanel(StudioShell shell)
        {
            _shell = shell;
            _ui = shell.UI;
            _session = shell.Session;
            _ops = shell.Ops;

            Panel = _ui.CreatePanel("Hierarchy");
            var box = _ui.CreateVBox();
            _tree = _ui.CreateTreeView();
            _tree.SetDropEnabled(true);
            _tree.SetOnSelectionChanged(OnTreeSelection);
            _tree.SetOnRenamed(OnRenamed);
            _tree.SetOnItemDropped(OnItemDropped);
            _tree.SetOnContextMenuRequested(OnContextMenuRequested);

            box.AddChild(_tree);
            Panel.AddChild(box);

            _session.HierarchyChanged += Rebuild;
            _session.SelectionChanged += SyncSelection;
            Rebuild();
        }

        /// <summary>Rebuild the whole tree from the scene world, preserving
        /// expansion state and selection where elements survived.</summary>
        public void Rebuild()
        {
            var expanded = new HashSet<IWorldElement>(
                _items.Where(kv => kv.Value.Expanded).Select(kv => kv.Key));

            _syncing = true;
            try
            {
                _tree.Clear();
                _items.Clear();
                var world = _session.SceneWorld;
                if (world != null)
                {
                    foreach (var el in world.Root)
                        AddRecursive(null, el, expanded);
                }
                if (_session.Selected != null && _items.TryGetValue(_session.Selected, out var item))
                    _tree.SetSelected(item);
                else
                    _tree.SetSelected(null);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void AddRecursive(ITreeItem? parent, IWorldElement el, HashSet<IWorldElement> expanded)
        {
            var item = _tree.AddItem(parent, el.Name ?? "(unnamed)", el);
            item.IconKey = IconKeyFor(el);
            if (expanded.Contains(el))
                item.Expanded = true;
            _items[el] = item;
            foreach (var child in el.Children)
                AddRecursive(item, child, expanded);
        }

        private static string IconKeyFor(IWorldElement el)
        {
            if (el.GetComponent<V12.Basic.Components.CameraComponent>() != null) return "camera";
            if (el.GetComponent<V12.Components.ScriptComponent>() != null) return "script";
            return "element";
        }

        private void SyncSelection()
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                if (_session.Selected != null && _items.TryGetValue(_session.Selected, out var item))
                {
                    _tree.SetSelected(item);
                    _tree.ScrollTo(item);
                }
                else
                {
                    _tree.SetSelected(null);
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnTreeSelection(ITreeItem? item)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                _session.Select(item?.UserData as IWorldElement);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnRenamed(ITreeItem item, string label)
        {
            if (item.UserData is IWorldElement el)
            {
                el.Name = label;
                _session.NotifyHierarchyChanged();
            }
        }

        private void OnItemDropped(ITreeItem dragged, ITreeItem target)
        {
            if (dragged.UserData is not IWorldElement el) return;
            if (target.UserData is not IWorldElement newParent) return;
            _ops.Reparent(el, newParent, out _);
        }

        private IContextMenu? OnContextMenuRequested(ITreeItem item)
        {
            if (item.UserData is not IWorldElement el) return null;

            var menu = _ui.CreateContextMenu();
            menu.AddItem("Create Empty Child", () => _ops.CreateEmpty(null, el));
            menu.AddItem("Create Box Child", () => _ops.CreatePrimitive(PrimitiveKind.Box, null, el));
            menu.AddItem("Create Sphere Child", () => _ops.CreatePrimitive(PrimitiveKind.Sphere, null, el));
            menu.AddItem("Create Capsule Child", () => _ops.CreatePrimitive(PrimitiveKind.Capsule, null, el));
            menu.AddItem("Create Cylinder Child", () => _ops.CreatePrimitive(PrimitiveKind.Cylinder, null, el));
            menu.AddSeparator();
            menu.AddItem("Rename", () => _tree.BeginRename(item));
            menu.AddItem("Duplicate", () => _ops.Duplicate(el, out _));
            menu.AddItem("Group Children", () =>
            {
                if (el.Children.Count > 0) _ops.Group(el.Children.ToList(), out _);
            }, el.Children.Count > 0);
            menu.AddItem("Add Component…", () => AddComponentRequested?.Invoke(el));
            menu.AddSeparator();
            menu.AddItem("Delete", () => _ops.Delete(el, out _));
            return menu;
        }
    }
}
