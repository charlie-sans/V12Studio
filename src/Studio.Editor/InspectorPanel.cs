using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using V12.Core.UI;
using V12.Core.Core.Interfaces;
using V12.UI;
using V12Studio.Core;
using ScriptComponent = V12.Components.ScriptComponent;
using TransformComponent = V12.Components.TransformComponent;
using NonPersistentComponent = V12.Components.NonPersistentComponent;

namespace V12Studio.Editor
{
    /// <summary>
    /// Right "Inspector" panel for the selected element: name, transform
    /// (position / rotation degrees / scale), the component list with remove,
    /// script @field rows, and the inline New Component form.
    /// </summary>
    public sealed class InspectorPanel
    {
        private readonly StudioShell _shell;
        private readonly IUIProvider _ui;
        private readonly StudioSession _session;

        private IWidget? _currentRoot;
        private IButton? _addComponentButton;
        private bool _showingForm;

        public IPanel Panel { get; }

        public InspectorPanel(StudioShell shell)
        {
            _shell = shell;
            _ui = shell.UI;
            _session = shell.Session;

            Panel = _ui.CreatePanel("Inspector");
            Panel.UseScrollableContent = true;
            ShowMain();
            // A selection change while the New Component form is open cancels
            // the form — the inspector always tracks the current selection.
            _session.SelectionChanged += ShowMain;
        }

        /// <summary>Render the standard inspector for the current selection.</summary>
        public void ShowMain()
        {
            var box = _ui.CreateVBox();
            var sel = _session.Selected;

            if (sel == null || !_session.IsSceneElement(sel))
            {
                box.AddChild(_ui.CreateLabel("Nothing selected."));
                SetContent(box);
                return;
            }

            var nameRow = _ui.CreateHBox();
            nameRow.AddChild(InspectorLayout.RowLabel(_ui, "Name"));
            var nameField = _ui.CreateTextField("name", _ => { });
            nameField.Text = sel.Name ?? "";
            nameField.SetOnChanged(v =>
            {
                sel.Name = v;
                _session.NotifyHierarchyChanged();
            });
            nameRow.AddChild(nameField);
            box.AddChild(nameRow);

            TransformRow(box, "Position", TransformEdit.PositionOf(sel),
                (el, x, y, z) => TransformEdit.SetPosition(el, x, y, z), "F2");
            TransformRow(box, "Rotation", TransformEdit.RotationDegreesOf(sel),
                (el, x, y, z) => TransformEdit.SetRotationDegrees(el, x, y, z), "F1");
            TransformRow(box, "Scale", TransformEdit.ScaleOf(sel),
                (el, x, y, z) => TransformEdit.SetScale(el, x, y, z), "F2");

            box.AddChild(_ui.CreateLabel("Components"));
            foreach (var component in sel.Components.ToList())
            {
                // Internal persistence marker (e.g. on the editor camera):
                // plumbing, not an authored component.
                if (component is NonPersistentComponent) continue;

                var row = _ui.CreateHBox();
                row.AddChild(InspectorLayout.RowLabel(_ui, FriendlyName(component)));
                var remove = _ui.CreateButton("Remove", () =>
                {
                    sel.RemoveComponent(component);
                    _session.NotifyHierarchyChanged();
                    ShowMain();
                });
                row.AddChild(remove);
                box.AddChild(row);

                var fields = _ui.CreateVBox();
                var adapter = new StudioInspectorAdapter(_ui, fields, _shell.ProjectDir);
                bool hasInspectorContent;

                if (component is TransformComponent)
                {
                    // Spec §3: one presentation of transform values. The
                    // Position / Rotation / Scale rows at the top edit the
                    // element itself; this component keeps a separate example
                    // store, and restating it here in a different layout
                    // would only duplicate those rows confusingly.
                    fields.AddChild(_ui.CreateLabel("(edited in the rows above)"));
                    hasInspectorContent = true;
                }
                else
                {
                    // Persistence switch: unchecked = the component is editor/
                    // runtime only and is skipped by the WorldML serializer on
                    // save/export. The element's pose always round-trips (it
                    // lives on the element), so TransformComponent above has no
                    // such row.
                    var persistRow = _ui.CreateHBox();
                    persistRow.AddChild(InspectorLayout.RowLabel(_ui, "Save with world"));
                    var persistBox = _ui.CreateCheckbox("", component.IsPersistent);
                    persistBox.SetOnChanged(v => component.IsPersistent = v);
                    persistRow.AddChild(persistBox);
                    fields.AddChild(persistRow);

                    try
                    {
                        component.BuildInspector(adapter);
                    }
                    catch (Exception ex)
                    {
                        fields.AddChild(_ui.CreateLabel($"Inspector error: {ex.Message}"));
                    }
                    hasInspectorContent = adapter.ControlCount > 0;
                    if (!hasInspectorContent)
                        hasInspectorContent = AddGeneratedFields(fields, component);

                    // Script @field rows live in the script's own block, so a
                    // component reads as one unit instead of trailing rows.
                    if (component is ScriptComponent script)
                        hasInspectorContent |= AddScriptFields(fields, script);

                    hasInspectorContent = true; // the persistence row is always content
                }

                if (hasInspectorContent)
                    box.AddChild(fields);
            }

            var add = _ui.CreateButton("Add Component…", () => ShowAddComponentMenu(sel));
            _addComponentButton = add;
            box.AddChild(add);
            _showingForm = false;
            SetContent(box);
        }

        /// <summary>
        /// Unity-style Add Component menu for <paramref name="target"/>: one
        /// submenu per catalogue group, a freshly rescanned "Your Components"
        /// (scripts in the project folder — a script you just created is
        /// attachable immediately), and the full New Script form at the end.
        /// Clicking an entry attaches it directly.
        /// </summary>
        public void ShowAddComponentMenu(IWorldElement target)
        {
            if (!ReferenceEquals(_session.Selected, target))
                _session.Select(target);          // fires SelectionChanged → ShowMain
            else if (_showingForm)
                ShowMain();                       // swap the form back to the list
            // Main list already showing: keep it (and the user's scroll
            // position) — the anchor button is laid out and stays valid.
            if (_addComponentButton == null) return;
            BuildAddComponentMenu(target).ShowAt(_addComponentButton);
        }

        private IContextMenu BuildAddComponentMenu(IWorldElement target)
        {
            var menu = _ui.CreateContextMenu();

            foreach (var key in ComponentCatalog.Categories)
            {
                var entries = ComponentCatalog.BuiltInEntries
                    .Where(e => e.Category == key).ToList();
                if (entries.Count == 0) continue;
                var sub = _ui.CreateContextMenu();
                foreach (var entry in entries)
                {
                    var e = entry;
                    sub.AddItem(ComponentCatalog.EntryDisplayName(e),
                        () => AttachFromMenu(target, e));
                }
                menu.AddSubmenu(ComponentCatalog.CategoryTitle(key), sub);
            }

            var scripts = ComponentCatalog.DiscoverScripts(_shell.ProjectDir);
            if (scripts.Count > 0)
            {
                var sub = _ui.CreateContextMenu();
                foreach (var entry in scripts)
                {
                    var e = entry;
                    sub.AddItem(ComponentCatalog.EntryDisplayName(e),
                        () => AttachFromMenu(target, e));
                }
                menu.AddSubmenu(ComponentCatalog.YourComponentsCategory, sub);
            }

            menu.AddSeparator();
            menu.AddItem("New Script Component…", () => ShowNewComponentForm(target));
            return menu;
        }

        private void AttachFromMenu(IWorldElement target, CatalogEntry entry)
        {
            var result = _shell.Creator.CreateAndAttach(new CreateComponentRequest
            {
                Target = target,
                Builtin = entry,
            });
            if (!result.Success)
                _session.Log.Error(result.Message); // Console panel explains (§5)
            ShowMain(); // success shows the new component; failure re-renders as before
        }

        /// <summary>Render the inline New Component form targeting
        /// <paramref name="target"/>. Create/Cancel return to the main view.</summary>
        public void ShowNewComponentForm(IWorldElement target)
        {
            var form = new NewComponentForm(_shell, target, onFinished: ShowMain);
            _showingForm = true;
            SetContent(form.Build());
        }

        private void TransformRow(IVBox box, string label,
            (float X, float Y, float Z) current,
            Action<IWorldElement, float, float, float> apply, string format)
        {
            var row = _ui.CreateHBox();
            row.AddChild(InspectorLayout.RowLabel(_ui, label));

            var x = _ui.CreateNumericField(current.X, format);
            var y = _ui.CreateNumericField(current.Y, format);
            var z = _ui.CreateNumericField(current.Z, format);
            row.AddChild(LabeledAxis("X", x));
            row.AddChild(LabeledAxis("Y", y));
            row.AddChild(LabeledAxis("Z", z));

            void Apply()
            {
                if (_session.Selected is { } el && _session.IsSceneElement(el))
                    apply(el, x.Value, y.Value, z.Value);
            }
            x.SetOnChanged(_ => Apply());
            y.SetOnChanged(_ => Apply());
            z.SetOnChanged(_ => Apply());
            box.AddChild(row);
        }

        private IWidget LabeledAxis(string axis, INumericField field)
        {
            var box = _ui.CreateHBox();
            box.AddChild(_ui.CreateLabel(axis));
            box.AddChild(field);
            return box;
        }

        private static readonly System.Collections.Generic.HashSet<string> GeneratedFieldExclusions = new(StringComparer.Ordinal)
        {
            "Id", "EntityId", "Owner", "IsDirty", "Name", "Description",
            "IsPersistent", // has its own "Save with world" row per component block
            "Runtime", "IsInitialized", "IsPlaying", "Position", "Forward", "Up",
            "RenderType", "Transform", "WorldTransform", "LocalTransform", "IsWorldLocked",
            "Material", "MeshPoints", "Indices", "PrimaryTexture", "RX", "RY", "RZ",
            "Color", "Intensity", "IsPointing", "IsSelecting", "HitEntityId", "HitPosition",
            "HitNormal", "BuildUI",
        };

        private bool AddGeneratedFields(IVBox box, IComponent component)
        {
            bool generated = false;
            foreach (var property in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (GeneratedFieldExclusions.Contains(property.Name)
                    || property.GetIndexParameters().Length != 0
                    || property.GetMethod?.IsPublic != true
                    || property.SetMethod?.IsPublic != true)
                    continue;

                var type = property.PropertyType;
                if (type == typeof(bool))
                {
                    var row = _ui.CreateHBox();
                    row.AddChild(InspectorLayout.RowLabel(_ui, property.Name));
                    var field = _ui.CreateCheckbox("", (bool)(property.GetValue(component) ?? false));
                    field.SetOnChanged(value => property.SetValue(component, value));
                    row.AddChild(field);
                    box.AddChild(row);
                    generated = true;
                }
                else if (type == typeof(float) || type == typeof(double) || type == typeof(int))
                {
                    var row = _ui.CreateHBox();
                    row.AddChild(InspectorLayout.RowLabel(_ui, property.Name));
                    var initial = Convert.ToSingle(property.GetValue(component), CultureInfo.InvariantCulture);
                    var format = type == typeof(int) ? "F0" : "F3";
                    var field = _ui.CreateNumericField(initial, format);
                    field.SetOnChanged(value =>
                    {
                        object converted = type == typeof(int)
                            ? (object)Convert.ToInt32(MathF.Round(value), CultureInfo.InvariantCulture)
                            : type == typeof(double) ? (object)(double)value : value;
                        property.SetValue(component, converted);
                    });
                    row.AddChild(field);
                    box.AddChild(row);
                    generated = true;
                }
                else if (type == typeof(string))
                {
                    var row = _ui.CreateHBox();
                    row.AddChild(InspectorLayout.RowLabel(_ui, property.Name));
                    var field = _ui.CreateTextField("", _ => { });
                    field.Text = (string?)property.GetValue(component) ?? "";
                    field.SetOnChanged(value => property.SetValue(component, value));
                    row.AddChild(field);
                    box.AddChild(row);
                    generated = true;
                }
                else if (type.IsEnum)
                {
                    var row = _ui.CreateHBox();
                    row.AddChild(InspectorLayout.RowLabel(_ui, property.Name));
                    var values = Enum.GetValues(type);
                    var names = Enum.GetNames(type);
                    var field = _ui.CreateDropdown(names, index =>
                    {
                        if (index >= 0 && index < values.Length)
                            property.SetValue(component, values.GetValue(index));
                    });
                    field.SelectedIndex = System.Array.IndexOf(values, property.GetValue(component));
                    row.AddChild(field);
                    box.AddChild(row);
                    generated = true;
                }
            }
            return generated;
        }

        /// <summary>Render a script's @field rows inside the component's own
        /// block. Returns true (content was rendered) always: either the rows
        /// themselves or the honest "no declarations" note.</summary>
        private bool AddScriptFields(IVBox box, ScriptComponent script)
        {
            var fields = string.IsNullOrEmpty(script.Source)
                ? Array.Empty<ScriptField>()
                : ScriptFieldParser.ParseFile(script.Source);

            if (fields.Count == 0)
            {
                box.AddChild(_ui.CreateLabel("(no @field declarations)"));
                return true;
            }

            foreach (var field in fields)
            {
                var row = _ui.CreateHBox();
                row.AddChild(InspectorLayout.RowLabel(_ui, $"{field.Name} ({field.Type})"));

                if (field.Type == "number")
                {
                    var defaultText = string.IsNullOrWhiteSpace(field.Default) ? "0" : field.Default!;
                    float.TryParse(defaultText, NumberStyles.Float, CultureInfo.InvariantCulture, out var initial);
                    var nf = _ui.CreateNumericField(initial, "F2");
                    if (script.Runtime == null)
                    {
                        nf.Enabled = false;
                        row.AddChild(nf);
                        row.AddChild(_ui.CreateLabel("(runtime not attached)"));
                    }
                    else
                    {
                        nf.SetOnChanged(v => script.Runtime?.SetGlobal(field.Name, v));
                        row.AddChild(nf);
                    }
                }
                else
                {
                    var tf = _ui.CreateTextField(field.Default ?? "", _ => { });
                    tf.Text = field.Default ?? "";
                    if (script.Runtime == null)
                    {
                        tf.Enabled = false;
                        row.AddChild(tf);
                        row.AddChild(_ui.CreateLabel("(runtime not attached)"));
                    }
                    else
                    {
                        tf.SetOnChanged(v => script.Runtime?.SetGlobal(field.Name, v));
                        row.AddChild(tf);
                    }
                }
                box.AddChild(row);
            }
            return true;
        }

        private static string FriendlyName(IComponent component)
        {
            var name = component.GetType().Name;
            return name.EndsWith("Component", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - "Component".Length)
                : name;
        }

        private void SetContent(IWidget widget)
        {
            if (_currentRoot != null)
                Panel.RemoveChild(_currentRoot);
            Panel.AddChild(widget);
            _currentRoot = widget;
        }
    }
}
