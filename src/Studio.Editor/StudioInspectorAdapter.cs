using System;
using System.Collections.Generic;
using System.Globalization;
using V12.Core.UI;
using V12.UI;

namespace V12Studio.Editor
{
    internal sealed class StudioInspectorAdapter : IInspector
    {
        private readonly IUIProvider _ui;
        private readonly IVBox _root;

        public StudioInspectorAdapter(IUIProvider ui, IVBox root)
        {
            _ui = ui;
            _root = root;
        }

        public int ControlCount { get; private set; }

        public void Bool(string label, Func<bool> getter, Action<bool> setter)
        {
            var row = _ui.CreateHBox();
            row.AddChild(InspectorLayout.RowLabel(_ui, label));
            var field = _ui.CreateCheckbox("", getter());
            field.SetOnChanged(setter);
            row.AddChild(field);
            Add(row);
        }

        public void Int(string label, Func<int> getter, Action<int> setter)
        {
            var row = LabelRow(label);
            var field = _ui.CreateTextField("", _ => { });
            field.Text = getter().ToString(CultureInfo.InvariantCulture);
            field.SetOnChanged(text =>
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    setter(value);
            });
            row.AddChild(field);
            Add(row);
        }

        public void Float(string label, Func<float> getter, Action<float> setter)
        {
            var row = LabelRow(label);
            var field = _ui.CreateNumericField(getter(), "F3");
            field.SetOnChanged(value => setter(value));
            row.AddChild(field);
            Add(row);
        }

        public void String(string label, Func<string> getter, Action<string> setter)
        {
            var row = LabelRow(label);
            var field = _ui.CreateTextField("", _ => { });
            field.Text = getter() ?? "";
            field.SetOnChanged(setter);
            row.AddChild(field);
            Add(row);
        }

        public void Enum<T>(string label, Func<T> getter, Action<T> setter) where T : struct, Enum
        {
            var row = LabelRow(label);
            var values = System.Enum.GetValues<T>();
            var field = _ui.CreateDropdown(System.Enum.GetNames<T>(), index =>
            {
                if (index >= 0 && index < values.Length)
                    setter(values[index]);
            });
            field.SelectedIndex = System.Array.IndexOf(values, getter());
            row.AddChild(field);
            Add(row);
        }

        public void Vector2(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");
        public void Vector3(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");
        public void Color(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");
        public void Asset<T>(string label, Func<T> getter, Action<T> setter) where T : class
            => ReadOnly(label, getter()?.ToString() ?? "—");
        public void Entity(string label, Func<object> getter, Action<object> setter)
            => ReadOnly(label, getter()?.ToString() ?? "—");

        public void Section(string title)
        {
            Add(_ui.CreateLabel(title));
        }

        public void Separator()
        {
            Add(_ui.CreateLabel("────────────────"));
        }

        public void Indent() { }
        public void Unindent() { }
        public void SameLine() { }
        public void Spacing() { _root.AddChild(_ui.CreateLabel("")); }
        public void Foldout(string label, Func<bool> getter, Action<bool> setter) => Section(label);
        public void TreeNode(string label) => Section(label);
        public void TabBar(string label) => Section(label);
        public void CollapsingHeader(string label) => Section(label);
        public void HelpBox(string message) => Add(_ui.CreateLabel(message));
        public void Warning(string message) => Add(_ui.CreateLabel(message));
        public void Error(string message) => Add(_ui.CreateLabel(message));
        public void Tooltip(string text) { }
        public void ReadOnly(string label, object value) => Add(_ui.CreateLabel($"{label}: {value}"));
        public void Disabled(string label, bool disabled) { if (disabled) ReadOnly(label, "—"); }
        public void Button(string label, Action action) => Add(_ui.CreateButton(label, action));
        public void Action(string label, Action action) => Button(label, action);
        public void Command(string label, string command) => ReadOnly(label, command);
        public void ContextMenu(string label) => Section(label);
        public void List<T>(string label, Func<List<T>> getter, Action<List<T>> setter) => ReadOnly(label, $"{getter().Count} items");
        public void Array<T>(string label, Func<T[]> getter, Action<T[]> setter) => ReadOnly(label, $"{getter().Length} items");
        public void Dictionary<K, V>(string label, Func<Dictionary<K, V>> getter, Action<Dictionary<K, V>> setter) => ReadOnly(label, $"{getter().Count} entries");
        public void ComponentReference(string label, Func<object> getter, Action<object> setter) => ReadOnly(label, getter()?.ToString() ?? "—");
        public void WorldReference(string label, Func<object> getter, Action<object> setter) => ReadOnly(label, getter()?.ToString() ?? "—");
        public void ObjectIRField(string label, Func<object> getter, Action<object> setter) => ReadOnly(label, getter()?.ToString() ?? "—");
        public void NetworkState(string label, Func<object> getter, Action<object> setter) => ReadOnly(label, getter()?.ToString() ?? "—");
        public void ReplicationFlags(string label, Func<object> getter, Action<object> setter) => ReadOnly(label, getter()?.ToString() ?? "—");

        private IHBox LabelRow(string label)
        {
            var row = _ui.CreateHBox();
            row.AddChild(InspectorLayout.RowLabel(_ui, label));
            return row;
        }

        private void Add(IWidget widget)
        {
            _root.AddChild(widget);
            ControlCount++;
        }
    }
}
