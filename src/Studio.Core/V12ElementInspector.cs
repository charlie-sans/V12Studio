using V12.Core;
using V12.Core.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using V12.Components.UI;

namespace V12Studio.Core
{
    /// <summary>
    /// IInspector implementation that generates V12 world elements (label +
    /// control rows) into a target container element. Frontends render those
    /// elements like any other UI, so a component can describe its editable
    /// fields by overriding <c>BuildInspector</c> and get a full inspector
    /// panel for free — the component generates its own UI.
    ///
    /// Row layout: each control is an HLayout row containing a fixed-width
    /// muted label and the control (text input / checkbox). Float/Int/String
    /// inputs commit via the component's OnChanged callback; the field text
    /// itself is driven by the getter so invalid input simply reverts.
    /// </summary>
    public class V12ElementInspector : IInspector
    {
        private readonly Element _root;
        private readonly Stack<Element> _indentStack = new();
        private int _spacing;

        public V12ElementInspector(Element root)
        {
            _root = root;
        }

        private Element Target => _indentStack.Count > 0 ? _indentStack.Peek() : _root;

        // ── Primitives ────────────────────────────────────────────────────

        public void Bool(string label, Func<bool> getter, Action<bool> setter)
        {
            var row = Row(label);
            var box = new Element("val:" + label);
            var cb = new CheckboxComponent { Checked = getter() };
            cb.OnChanged += on => setter(on);
            box.AddComponent(cb);
            row.AddChild(box);
        }

        public void Int(string label, Func<int> getter, Action<int> setter)
        {
            TextRow(label, getter().ToString(), text =>
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    setter(v);
            });
        }

        public void Float(string label, Func<float> getter, Action<float> setter)
        {
            TextRow(label, getter().ToString("F3", CultureInfo.InvariantCulture), text =>
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    setter(v);
            });
        }

        public void String(string label, Func<string> getter, Action<string> setter)
        {
            TextRow(label, getter() ?? "", setter);
        }

        public void Vector2(string label, Func<object> getter, Action<object> setter)
        {
            var v = getter();
            if (v is System.Numerics.Vector2 vec)
            {
                Float(label + " X", () => vec.X, x => setter(new System.Numerics.Vector2(x, vec.Y)));
                Float(label + " Y", () => vec.Y, y => setter(new System.Numerics.Vector2(vec.X, y)));
            }
        }

        public void Vector3(string label, Func<object> getter, Action<object> setter)
        {
            var v = getter();
            if (v is System.Numerics.Vector3 vec)
            {
                Float(label + " X", () => vec.X, x => setter(new System.Numerics.Vector3(x, vec.Y, vec.Z)));
                Float(label + " Y", () => vec.Y, y => setter(new System.Numerics.Vector3(vec.X, y, vec.Z)));
                Float(label + " Z", () => vec.Z, z => setter(new System.Numerics.Vector3(vec.X, vec.Y, z)));
            }
        }

        public void Color(string label, Func<object> getter, Action<object> setter)
        {
            // Colors render as read-only swatch rows for now.
            ReadOnly(label, getter()?.ToString() ?? "");
        }

        public void Enum<T>(string label, Func<T> getter, Action<T> setter) where T : struct, Enum
        {
            TextRow(label, getter().ToString(), text =>
            {
                if (System.Enum.TryParse<T>(text, true, out var v))
                    setter(v);
            });
        }

        public void Asset<T>(string label, Func<T> getter, Action<T> setter) where T : class
            => ReadOnly(label, getter()?.ToString() ?? "");

        public void Entity(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "");

        // ── Layout helpers ────────────────────────────────────────────────

        public void Section(string title)
        {
            var el = new Element("sec:" + title);
            el.AddComponent(new LabelComponent(title));
            el.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
            Target.AddChild(el);
        }

        public void Separator()
        {
            var el = new Element("sep:" + _spacing++);
            el.AddComponent(new LabelComponent("—"));
            el.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            Target.AddChild(el);
        }

        public void Indent()
        {
            var nested = new Element("indent:" + _spacing++);
            nested.AddComponent(new VLayoutComponent { Spacing = 1, Padding = 6 });
            Target.AddChild(nested);
            _indentStack.Push(nested);
        }

        public void Unindent()
        {
            if (_indentStack.Count > 0) _indentStack.Pop();
        }

        public void SameLine() { }
        public void Spacing() { }

        // ── Stateful widgets ──────────────────────────────────────────────

        public void Foldout(string label, Func<bool> getter, Action<bool> setter)
            => Section(label);

        public void TreeNode(string label) => Section(label);
        public void TabBar(string label) => Section(label);
        public void CollapsingHeader(string label) => Section(label);

        // ── Validation & UX ───────────────────────────────────────────────

        public void HelpBox(string message) => MessageRow(message, "muted");
        public void Warning(string message) => MessageRow(message, "danger");
        public void Error(string message) => MessageRow(message, "danger");

        public void Tooltip(string text) { }

        public void ReadOnly(string label, object value)
        {
            var el = new Element("ro:" + label);
            el.AddComponent(new LabelComponent($"{label}: {value}"));
            el.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            Target.AddChild(el);
        }

        public void Disabled(string label, bool disabled)
        {
            if (disabled) ReadOnly(label, "—");
            else Section(label);
        }

        // ── Runtime interaction ───────────────────────────────────────────

        public void Button(string label, Action action)
        {
            var el = new Element("btn:" + label);
            el.AddComponent(new ButtonComponent(label, action));
            Target.AddChild(el);
        }

        public void Action(string label, Action action) => Button(label, action);

        public void Command(string label, string command) => Button(label, () => { });

        public void ContextMenu(string label) => Button(label, () => { });

        // ── Collections ───────────────────────────────────────────────────

        public void List<T>(string label, Func<List<T>> getter, Action<List<T>> setter)
            => ReadOnly(label, $"{getter()?.Count ?? 0} items");

        public void Array<T>(string label, Func<T[]> getter, Action<T[]> setter)
            => ReadOnly(label, $"{getter()?.Length ?? 0} items");

        public void Dictionary<K, V>(string label, Func<Dictionary<K, V>> getter, Action<Dictionary<K, V>> setter)
            => ReadOnly(label, $"{getter()?.Count ?? 0} entries");

        // ── Advanced V12 ──────────────────────────────────────────────────

        public void ComponentReference(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");

        public void WorldReference(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");

        public void ObjectIRField(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");

        public void NetworkState(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");

        public void ReplicationFlags(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");

        // ── Helpers ───────────────────────────────────────────────────────

        private Element Row(string label)
        {
            var row = new Element("row:" + label);
            row.AddComponent(new HLayoutComponent { Spacing = 6, Padding = 1 });
            var lbl = new Element("lbl:" + label);
            lbl.AddComponent(new LabelComponent(label));
            lbl.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            lbl.AddComponent(new LayoutElementComponent { MinWidth = 110 });
            row.AddChild(lbl);
            Target.AddChild(row);
            return row;
        }

        private void TextRow(string label, string initial, Action<string> onCommit)
        {
            var row = Row(label);
            var inputEl = new Element("val:" + label);
            var input = new TextInputComponent { Placeholder = label };
            input.Value = initial;
            input.OnChanged += text =>
            {
                if (string.IsNullOrEmpty(text)) return;
                onCommit(text);
            };
            inputEl.AddComponent(input);
            inputEl.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
            row.AddChild(inputEl);
        }

        private void MessageRow(string text, string hint)
        {
            var el = new Element("msg:" + text);
            el.AddComponent(new LabelComponent(text));
            el.AddComponent(new UIStyleComponent { StyleHint = hint });
            Target.AddChild(el);
        }
    }
}
