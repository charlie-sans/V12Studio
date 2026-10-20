using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.WorldML;
using MeshShape = V12.Components.MeshShape;

namespace V12Studio.Core
{
    /// <summary>Primitive kinds the editor can create.</summary>
    public enum PrimitiveKind { Box, Sphere, Capsule, Cylinder }

    /// <summary>
    /// Scene hierarchy operations: create, duplicate (WorldML round-trip deep
    /// clone), delete, reparent (with cycle guard), group, and path helpers.
    /// Every mutation notifies the session so hierarchy views refresh.
    /// </summary>
    public sealed class HierarchyOps
    {
        private readonly StudioSession _session;

        public HierarchyOps(StudioSession session)
        {
            _session = session;
        }

        /// <summary>Create an empty element at the world root (or under
        /// <paramref name="parent"/>) and select it.</summary>
        public IWorldElement CreateEmpty(string? name, IWorldElement? parent = null)
        {
            var el = new Element(string.IsNullOrWhiteSpace(name) ? "Empty" : name.Trim());
            Attach(el, parent);
            _session.Select(el);
            _session.Log.Info($"Created '{el.Name}'.");
            return el;
        }

        /// <summary>Create a coloured primitive mesh element and select it.</summary>
        public IWorldElement CreatePrimitive(PrimitiveKind kind, string? name, IWorldElement? parent = null)
        {
            var label = string.IsNullOrWhiteSpace(name) ? kind.ToString() : name!.Trim();
            var (shape, w, h, d) = kind switch
            {
                PrimitiveKind.Sphere => (MeshShape.Sphere, 1f, 1f, 1f),
                PrimitiveKind.Capsule => (MeshShape.Capsule, 0.5f, 1.2f, 0.5f),
                PrimitiveKind.Cylinder => (MeshShape.Cylinder, 0.7f, 1f, 0.7f),
                _ => (MeshShape.Box, 1f, 1f, 1f),
            };
            var el = StudioSession.MakeEditorMesh(label, shape, w, h, d, 0.7f, 0.7f, 0.75f);
            float y = 0.5f + 0.35f * (_session.NextAddSerial() % 8);
            el.LocalTransform = new TRS { Position = new Vector3(0f, y, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            Attach(el, parent);
            _session.Select(el);
            _session.Log.Info($"Created {kind} '{el.Name}'.");
            return el;
        }

        /// <summary>
        /// Deep-clone <paramref name="el"/> (whole subtree, components and all)
        /// via a WorldML round-trip. The clone lands next to the original and
        /// is selected. Returns null on serialization failure (with the error
        /// reported in <paramref name="error"/>).
        /// </summary>
        public IWorldElement? Duplicate(IWorldElement el, out string error)
        {
            error = "";
            if (!_session.IsSceneElement(el)) { error = "Only scene elements can be duplicated."; return null; }
            try
            {
                var xml = WorldMLSerializer.Serialize(new List<IWorldElement> { el }, el.Name ?? "Element");
                var parsed = new WorldMLParser().Parse(xml);
                var clone = parsed.Children.FirstOrDefault();
                if (clone == null) { error = "Serialization produced no element."; return null; }

                parsed.RemoveChild(clone);
                clone.Name = NextSiblingName(el, el.Name ?? "Element");
                var lt = clone.LocalTransform;
                lt.Position += new Vector3(0.5f, 0f, 0.5f);
                clone.LocalTransform = lt;

                Attach(clone, el.Parent);
                _session.Select(clone);
                _session.Log.Info($"Duplicated '{el.Name}' as '{clone.Name}'.");
                return clone;
            }
            catch (Exception ex)
            {
                error = $"Duplicate failed: {ex.Message}";
                _session.Log.Error(error);
                return null;
            }
        }

        /// <summary>Delete <paramref name="el"/> (and its subtree) from the scene.</summary>
        public bool Delete(IWorldElement el, out string error)
        {
            error = "";
            if (!_session.IsSceneElement(el)) { error = "Only scene elements can be deleted."; return false; }
            if (ReferenceEquals(el, _session.ViewportElement)) { error = "The editor viewport element cannot be deleted."; return false; }
            var name = el.Name ?? "(unnamed)";
            if (ReferenceEquals(_session.Selected, el) || IsDescendantOf(_session.Selected, el))
                _session.Select(null);
            var parent = el.Parent;
            if (parent != null)
                parent.RemoveChild(el);
            else
                _session.SceneWorld?.RemoveElement(el);
            _session.NotifyHierarchyChanged();
            _session.Log.Info($"Deleted '{name}'.");
            return true;
        }

        /// <summary>
        /// Reparent <paramref name="el"/> under <paramref name="newParent"/>
        /// (null = move to the world root). Refuses self-parenting and
        /// descendants of <paramref name="el"/> (cycle guard). Local transform
        /// is preserved as-is (it becomes relative to the new parent).
        /// </summary>
        public bool Reparent(IWorldElement el, IWorldElement? newParent, out string error)
        {
            error = "";
            if (!_session.IsSceneElement(el)) { error = "Only scene elements can be reparented."; return false; }
            if (ReferenceEquals(el, newParent)) { error = "Cannot parent an element to itself."; return false; }
            if (newParent != null && IsDescendantOf(newParent, el)) { error = "Cannot parent an element to its own child."; return false; }

            var oldParent = el.Parent;
            if (ReferenceEquals(oldParent, newParent)) return true;

            if (newParent == null)
            {
                if (oldParent != null)
                {
                    oldParent.RemoveChild(el);
                    _session.SceneWorld?.AddElement(el);
                }
            }
            else
            {
                if (oldParent != null)
                    oldParent.RemoveChild(el);
                else
                    _session.SceneWorld?.RemoveElement(el);
                newParent.AddChild(el);
            }

            _session.NotifyHierarchyChanged();
            _session.Log.Info($"Reparented '{el.Name}' under '{newParent?.Name ?? "World"}'.");
            return true;
        }

        /// <summary>Wrap <paramref name="elements"/> in a new "Group" element
        /// beside the first element's parent; returns the group, selected.</summary>
        public IWorldElement? Group(IReadOnlyList<IWorldElement> elements, out string error)
        {
            error = "";
            var scene = elements.Where(_session.IsSceneElement).ToList();
            if (scene.Count == 0) { error = "Nothing to group."; return null; }

            var group = new Element("Group");
            Attach(group, scene[0].Parent);
            foreach (var el in scene)
                Reparent(el, group, out _);
            _session.Select(group);
            _session.Log.Info($"Grouped {scene.Count} element(s) into 'Group'.");
            return group;
        }

        /// <summary>Hierarchy path of <paramref name="el"/>, e.g. "World / Ground / Leg".</summary>
        public string GetPath(IWorldElement el)
        {
            var parts = new List<string> { el.Name ?? "(unnamed)" };
            var cur = el.Parent;
            while (cur != null)
            {
                parts.Add(cur.Name ?? "(unnamed)");
                cur = cur.Parent;
            }
            parts.Reverse();
            return string.Join(" / ", parts);
        }

        /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or inside its subtree.</summary>
        public static bool IsDescendantOf(IWorldElement? candidate, IWorldElement ancestor)
        {
            var cur = candidate;
            while (cur != null)
            {
                if (ReferenceEquals(cur, ancestor)) return true;
                cur = cur.Parent;
            }
            return false;
        }

        private void Attach(IWorldElement el, IWorldElement? parent)
        {
            if (parent != null)
                parent.AddChild(el);
            else
                _session.SceneWorld?.AddElement(el);
            _session.NotifyHierarchyChanged();
        }

        private static string NextSiblingName(IWorldElement el, string baseName)
        {
            var parent = el.Parent;
            var existing = parent != null
                ? parent.Children.Select(c => c.Name ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; ; i++)
            {
                var candidate = $"{baseName} ({i})";
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }
    }
}
