using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using V12.Components;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;

// Non-UI components share names with UI components; alias the UI ones so the
// row-building code below reads cleanly.
using ButtonComponent = V12.Components.UI.ButtonComponent;
using ProgressBarComponent = V12.Components.UI.ProgressBarComponent;

namespace V12Studio.Editor
{
    /// <summary>
    /// Keeps the editor's Scene Hierarchy panel in sync with the V12 world.
    ///
    /// The world is the single source of truth: every tick this service walks
    /// the active worlds (SelectedWorld first, then PersistentWorld), builds a
    /// tree signature, and only when the tree actually changed does it rebuild
    /// the hierarchy element tree under <c>_treeRoot</c> (an element bearing a
    /// TreeComponent). WorldCanvasSystem mirrors that element nesting onto a
    /// Godot Tree control, giving native collapse arrows and indentation.
    ///
    /// Editor chrome (the screen-space canvas and its pure-UI subtrees) is
    /// filtered out so the panel shows scene content — meshes, lights, cameras,
    /// entities — not the editor's own buttons. Hidden elements don't produce
    /// rows, but their children are still walked (a viewport lives under the
    /// hidden MainContent splitter).
    ///
    /// Runs as an IGameService on the V12 worker thread; world mutations go
    /// through Element.AddChild/RemoveChild which take the world write lock, so
    /// the main-thread WorldCanvasSystem (read lock) sees a consistent tree.
    /// </summary>
    public sealed class HierarchySyncService : IGameService
    {
        private readonly Element _treeRoot;
        private readonly Element _fpsLabel;
        private readonly Action<IWorldElement?> _onSelect;
        private string _signature = "";
        private long _selectedId;
        private float _fps = 60f;

        public HierarchySyncService(Element treeRoot, Element fpsLabel, Action<IWorldElement?> onSelect)
        {
            _treeRoot = treeRoot;
            _fpsLabel = fpsLabel;
            _onSelect = onSelect;
        }

        public void Initialize(GameRoot g) { }
        public void Update(float deltaTime) { UpdateFps(deltaTime); Refresh(GameRoot.Instance); }
        public void Update(GameRoot gameRoot) => Refresh(gameRoot);

        /// <summary>Highlight the row for <paramref name="elementId"/> (0 = none).
        /// Forces the next refresh so the highlight moves immediately.</summary>
        public void SetSelected(long elementId)
        {
            if (_selectedId == elementId) return;
            _selectedId = elementId;
            _signature = ""; // selection is part of the render state — rebuild
        }

        private void Refresh(GameRoot? root)
        {
            if (root == null) return;

            string sig = BuildSignature(root);
            if (sig == _signature) return;
            _signature = sig;
            RebuildTree(root);
        }

        private void UpdateFps(float deltaTime)
        {
            if (_fpsLabel == null) return;
            if (deltaTime > 0.0001f && deltaTime < 1f)
                _fps = Math.Clamp(_fps * 0.9f + (1f / deltaTime) * 0.1f, 0f, 240f);
            var lbl = _fpsLabel.GetComponent<LabelComponent>();
            if (lbl == null) return;
            string text = $"FPS: {_fps:F0}";
            if (lbl.Text != text)
                lbl.Text = text;
        }

        // ── Signature (structure + names + selection) ─────────────────────

        private string BuildSignature(GameRoot root)
        {
            var sb = new StringBuilder();
            if (root.SelectedWorld != null)
                SigWorld(sb, root.SelectedWorld);
            SigWorld(sb, root.PersistentWorld);
            sb.Append("#sel=").Append(_selectedId);
            return sb.ToString();
        }

        private void SigWorld(StringBuilder sb, World world)
        {
            sb.Append("w|").Append(world.WorldName).Append(';');
            world.Lock.EnterReadLock();
            try
            {
                foreach (var el in world.Root)
                    SigElement(sb, el);
            }
            finally { world.Lock.ExitReadLock(); }
        }

        private void SigElement(StringBuilder sb, IWorldElement el)
        {
            if (ShouldHide(el)) return;
            sb.Append(el.Id).Append('|').Append(el.Name ?? "").Append(';');
            foreach (var child in el.Children)
                SigElement(sb, child);
        }

        // ── Tree build ────────────────────────────────────────────────────

        /// <summary>
        /// Rebuild the hierarchy element tree. World reads happen under read
        /// locks and only produce plain <see cref="NodeInfo"/> descriptors —
        /// building the element rows (Element.AddChild) must NOT run under a
        /// world lock, because AddChild takes the world WRITE lock and
        /// read-lock-held → write-acquire throws LockRecursionException.
        /// </summary>
        private void RebuildTree(GameRoot root)
        {
            var roots = new List<NodeInfo>();
            if (root.SelectedWorld != null)
                CollectWorld(root.SelectedWorld, roots);
            CollectWorld(root.PersistentWorld, roots);

            // No world locks held here — safe to mutate the element tree.
            foreach (var child in _treeRoot.Children.ToList())
                _treeRoot.RemoveChild(child);
            foreach (var node in roots)
                AddNode(node, _treeRoot);
        }

        private void CollectWorld(World world, List<NodeInfo> roots)
        {
            world.Lock.EnterReadLock();
            try
            {
                var worldNode = new NodeInfo
                {
                    Key = "w:" + world.WorldName,
                    Text = world.WorldName == "__Persistent__" ? "Scene" : world.WorldName,
                    World = true
                };
                foreach (var el in world.Root)
                    CollectElement(el, worldNode.Children);
                roots.Add(worldNode);
            }
            finally { world.Lock.ExitReadLock(); }
        }

        private void CollectElement(IWorldElement el, List<NodeInfo> outList)
        {
            if (ShouldHide(el))
            {
                // Editor chrome: no row, but children may hold scene content
                // (e.g. the GameView viewport under the hidden MainContent).
                foreach (var child in el.Children)
                    CollectElement(child, outList);
                return;
            }

            var node = new NodeInfo
            {
                Key = "e:" + el.Id,
                Text = string.IsNullOrEmpty(el.Name) ? "<unnamed>" : el.Name,
                Selected = el.Id == _selectedId,
                Target = el
            };
            foreach (var child in el.Children)
                CollectElement(child, node.Children);
            outList.Add(node);
        }

        private void AddNode(NodeInfo node, Element parent)
        {
            var el = new Element(node.Key);
            if (node.World)
            {
                el.AddComponent(new LabelComponent(node.Text));
                el.AddComponent(new UIStyleComponent { StyleHint = "accent" });
            }
            else
            {
                var target = node.Target;
                el.AddComponent(new ButtonComponent(node.Text, () => _onSelect?.Invoke(target)));
                if (node.Selected)
                    el.AddComponent(new UIStyleComponent { StyleHint = "selected" });
            }
            parent.AddChild(el);
            foreach (var child in node.Children)
                AddNode(child, el);
        }

        /// <summary>True when the element only carries UI/layout components (or a
        /// screen-space canvas) — i.e. it's editor chrome, not scene content.
        /// Viewports are always shown (they contain the scene).</summary>
        private static bool ShouldHide(IWorldElement el)
        {
            if (el.GetComponent<CanvasComponent>() != null) return true;
            if (el.GetComponent<ViewportComponent>() != null) return false;
            var comps = el.Components;
            if (comps == null || comps.Count == 0) return false; // bare group node → scene content
            foreach (var c in comps)
            {
                bool isUi = c is LabelComponent
                    or ButtonComponent or V12.Components.ButtonComponent
                    or RectComponent or ProgressBarComponent or ToggleComponent
                    or CheckboxComponent or SliderComponent or TextInputComponent
                    or ImageComponent or IconComponent
                    or HLayoutComponent or VLayoutComponent or SplitterComponent
                    or UIStyleComponent or LayoutElementComponent
                    or TransformComponent or V12.Components.TransformComponent;
                if (!isUi) return false;
            }
            return true;
        }

        private sealed class NodeInfo
        {
            public string Key = "";
            public string Text = "";
            public bool World;
            public bool Selected;
            public IWorldElement? Target;
            public List<NodeInfo> Children = new();
        }
    }
}
