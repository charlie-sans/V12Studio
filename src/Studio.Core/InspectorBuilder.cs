using V12.Core.UI;
namespace V12Studio.Core
{
    using System;
    using System.Collections.Generic;
    using V12.Core.Core.Interfaces;

    public class InspectorBuilder : IInspector
    {
        private readonly VBoxElement _root;
        private readonly Stack<UiElement> _containers;

        public InspectorBuilder()
        {
            _root = new VBoxElement { Name = "InspectorRoot" };
            _containers = new Stack<UiElement>();
            _containers.Push(_root);
        }

        public UiElement Build() => _root;

        private UiElement Current => _containers.Peek();

        // IInspector implementation
        public void Bool(string label, Func<bool> getter, Action<bool> setter)
        {
            var row = new HBoxElement { Name = label };
            row.Children.Add(new LabelElement { Text = label });
            var toggle = new ToggleElement { IsOn = getter(), OnToggled = setter };
            row.Children.Add(toggle);
            Current.Children.Add(row);
        }

        public void Int(string label, Func<int> getter, Action<int> setter)
        {
            var row = new HBoxElement { Name = label };
            row.Children.Add(new LabelElement { Text = label });
            var input = new TextFieldElement { Text = getter().ToString(), OnCommit = s => { if (int.TryParse(s, out var v)) setter(v); } };
            row.Children.Add(input);
            Current.Children.Add(row);
        }

        public void Float(string label, Func<float> getter, Action<float> setter)
        {
            var row = new HBoxElement { Name = label };
            row.Children.Add(new LabelElement { Text = label });
            var input = new TextFieldElement { Text = getter().ToString(), OnCommit = s => { if (float.TryParse(s, out var v)) setter(v); } };
            row.Children.Add(input);
            Current.Children.Add(row);
        }

        public void String(string label, Func<string> getter, Action<string> setter)
        {
            var row = new HBoxElement { Name = label };
            row.Children.Add(new LabelElement { Text = label });
            var input = new TextFieldElement { Text = getter(), OnCommit = setter };
            row.Children.Add(input);
            Current.Children.Add(row);
        }

        public void Vector2(string label, Func<object> getter, Action<object> setter)
        {
            var row = new HBoxElement { Name = label };
            row.Children.Add(new LabelElement { Text = label });
            // Simplified: just a text field for now
            var input = new TextFieldElement { Text = getter()?.ToString() ?? "", OnCommit = s => setter(s) };
            row.Children.Add(input);
            Current.Children.Add(row);
        }
        
        public void Vector3(string label, Func<object> getter, Action<object> setter) => Vector2(label, getter, setter);
        public void Color(string label, Func<object> getter, Action<object> setter) => Vector2(label, getter, setter);

        public void Enum<T>(string label, Func<T> getter, Action<T> setter) where T : struct, Enum
        {
            var row = new HBoxElement { Name = label };
            row.Children.Add(new LabelElement { Text = label });
            var picker = new OptionPickerElement();
            foreach (var name in System.Enum.GetNames(typeof(T))) picker.Items.Add(name);
            picker.SelectedIndex = System.Array.IndexOf(System.Enum.GetValues(typeof(T)), getter());
            picker.OnSelectionChanged = idx => setter((T)System.Enum.GetValues(typeof(T)).GetValue(idx)!);
            row.Children.Add(picker);
            Current.Children.Add(row);
        }
        
        public void Asset<T>(string label, Func<T> getter, Action<T> setter) where T : class { /* Placeholder */ }
        public void Entity(string label, Func<object> getter, Action<object> setter) { /* Placeholder */ }

        public void Section(string title) { Current.Children.Add(new LabelElement { Text = title, StyleHint = "accent", FontSize = 14 }); }
        public void Separator() { Current.Children.Add(new SeparatorElement()); }
        public void Indent() { /* Needs UIBuilder support */ }
        public void Unindent() { /* Needs UIBuilder support */ }
        public void SameLine() { /* Needs UIBuilder support */ }
        public void Spacing() { Current.Children.Add(new RectElement { Height = 10f }); }

        public void Foldout(string label, Func<bool> getter, Action<bool> setter)
        {
            var btn = new ButtonElement { Text = (getter() ? "▼ " : "▶ ") + label, OnPressed = () => setter(!getter()), StyleHint = "titlebar" };
            Current.Children.Add(btn);
        }
        
        public void TreeNode(string label) { Current.Children.Add(new LabelElement { Text = "  " + label }); }
        public void TabBar(string label) { Current.Children.Add(new LabelElement { Text = "[Tab: " + label + "]" }); }
        public void CollapsingHeader(string label) { Current.Children.Add(new LabelElement { Text = "≡ " + label, StyleHint = "accent" }); }

        public void HelpBox(string message) { Current.Children.Add(new LabelElement { Text = "ℹ " + message, StyleHint = "muted" }); }
        public void Warning(string message) { Current.Children.Add(new LabelElement { Text = "⚠ " + message, StyleHint = "danger" }); }
        public void Error(string message) { Current.Children.Add(new LabelElement { Text = "✖ " + message, StyleHint = "danger" }); }
        public void Tooltip(string text) { /* Frontend specific */ }
        public void ReadOnly(string label, object value) { Current.Children.Add(new LabelElement { Text = label + ": " + value, StyleHint = "muted" }); }
        public void Disabled(string label, bool disabled) { /* Frontend specific */ }

        public void Button(string label, Action action) { Current.Children.Add(new ButtonElement { Text = label, OnPressed = action, StyleHint = "accent" }); }
        public void Action(string label, Action action) => Button(label, action);
        public void Command(string label, string command) { Current.Children.Add(new ButtonElement { Text = label }); }
        public void ContextMenu(string label) { Current.Children.Add(new ButtonElement { Text = "⋮ " + label }); }

        public void List<T>(string label, Func<List<T>> getter, Action<List<T>> setter) { }
        public void Array<T>(string label, Func<T[]> getter, Action<T[]> setter) { }
        public void Dictionary<K, V>(string label, Func<Dictionary<K, V>> getter, Action<Dictionary<K, V>> setter) { }

        public void ComponentReference(string label, Func<object> getter, Action<object> setter) { }
        public void WorldReference(string label, Func<object> getter, Action<object> setter) { }
        public void ObjectIRField(string label, Func<object> getter, Action<object> setter) { }
        public void NetworkState(string label, Func<object> getter, Action<object> setter) { }
        public void ReplicationFlags(string label, Func<object> getter, Action<object> setter) { }


    }
}
