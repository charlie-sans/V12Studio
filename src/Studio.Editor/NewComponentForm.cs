using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Core.Core.Interfaces;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// Inline New Component form (rendered inside the Inspector panel — the
    /// widget layer has no interactive modal, so this is a real form, not a
    /// fake dialog). Per spec §4 the catalogue is shown as groups (Rendering,
    /// Lighting, Physics, …, Your Components, Custom script), the dialog
    /// carries Name / Language / Pack / Creates path / Fields table /
    /// Target element, and the actions are Cancel / Create &amp; Attach.
    /// Per §5 a failure explains itself and keeps every input; only a real
    /// attach returns to the Inspector.
    /// </summary>
    public sealed class NewComponentForm
    {
        private const string CustomLabel = "Custom script…";
        private const string NoneLabel = "(none found)";

        private readonly StudioShell _shell;
        private readonly IUIProvider _ui;
        private readonly IWorldElement _target;
        private readonly Action _onFinished;

        private readonly List<IHBox> _fieldRows = new();
        private readonly List<ITextField> _fieldNames = new();
        private readonly List<ITextField> _fieldTypes = new();
        private readonly List<ITextField> _fieldDefaults = new();
        private readonly IVBox _fieldsBox;

        // Catalogue groups: built-in categories, discovered project scripts,
        // then the custom-script workflow (always the last entry).
        private readonly List<string> _groupKeys = new();
        private readonly IDropdown _group;
        private readonly IDropdown _entry;
        private IReadOnlyList<CatalogEntry> _groupEntries = Array.Empty<CatalogEntry>();

        // Spec §4: the dialog can retarget a different element than the one
        // that opened it, so the target is a real choice, not a fixed header.
        private readonly IWorldElement[] _targetChoices;
        private readonly IDropdown _targetPicker;

        private readonly ILabel _title;
        private readonly ITextField _name;
        private readonly IDropdown _language;
        private readonly ITextField _pack;
        private readonly ITextField _path;
        private readonly ILabel _result;

        // Custom-flow controls, toggled Visible with the group dropdown.
        // Populated in Build(); empty until then (SyncMode no-ops).
        private IWidget[] _customOnly = Array.Empty<IWidget>();
        private IWidget? _entryRow;

        public NewComponentForm(StudioShell shell, IWorldElement target, Action onFinished)
        {
            _shell = shell;
            _ui = shell.UI;
            _target = target;
            _onFinished = onFinished;

            _title = _ui.CreateLabel($"New Component: {target.Name ?? "(unnamed)"}");

            foreach (var category in ComponentCatalog.BuiltInEntries.Select(e => e.Category).Distinct())
                _groupKeys.Add(category);
            _groupKeys.Add(ComponentCatalog.YourComponentsCategory);
            _groupKeys.Add(CustomLabel);

            _group = _ui.CreateDropdown(_groupKeys.Select(ComponentCatalog.CategoryTitle).ToArray(), _ =>
            {
                // A failure recorded for the old group must not outlive it.
                _result.Text = "";
                PopulateEntries();
                SyncMode();
            });
            _group.SelectedIndex = _groupKeys.Count - 1; // custom flow, as before

            _entry = _ui.CreateDropdown(new[] { NoneLabel }, _ => _result.Text = "");
            PopulateEntries();

            _targetChoices = EnumerateTargets();
            var targetNames = _targetChoices.Select(NameOf).ToArray();
            _targetPicker = _ui.CreateDropdown(targetNames, _ => SyncTarget());
            var initial = Array.IndexOf(_targetChoices, target);
            _targetPicker.SelectedIndex = initial >= 0 ? initial : 0;

            _name = _ui.CreateTextField("ComponentName", _ => SyncPathFromName());
            _language = _ui.CreateDropdown(
                ComponentCatalog.ScriptLanguages.Select(l => l.Language).ToArray(), _ => SyncPathFromName());
            _pack = _ui.CreateTextField("e.g. ecovr.v12pak", _ => { });
            _path = _ui.CreateTextField("scripts/BossArena", _ => { });
            _result = _ui.CreateLabel("");

            _fieldsBox = _ui.CreateVBox();
        }

        public IWidget Build()
        {
            var box = _ui.CreateVBox();
            box.AddChild(_title);

            var groupRow = _ui.CreateHBox();
            groupRow.AddChild(InspectorLayout.RowLabel(_ui, "Group"));
            groupRow.AddChild(_group);
            box.AddChild(groupRow);

            var entryRow = _ui.CreateHBox();
            entryRow.AddChild(InspectorLayout.RowLabel(_ui, "Component"));
            entryRow.AddChild(_entry);
            box.AddChild(entryRow);
            _entryRow = entryRow;

            var nameRow = _ui.CreateHBox();
            nameRow.AddChild(InspectorLayout.RowLabel(_ui, "Name"));
            nameRow.AddChild(_name);
            box.AddChild(nameRow);

            var langRow = _ui.CreateHBox();
            langRow.AddChild(InspectorLayout.RowLabel(_ui, "Language"));
            langRow.AddChild(_language);
            box.AddChild(langRow);

            var packRow = _ui.CreateHBox();
            packRow.AddChild(InspectorLayout.RowLabel(_ui, "Pack"));
            packRow.AddChild(_pack);
            box.AddChild(packRow);

            var pathRow = _ui.CreateHBox();
            pathRow.AddChild(InspectorLayout.RowLabel(_ui, "Creates path"));
            pathRow.AddChild(_path);
            box.AddChild(pathRow);

            var fieldsBlock = _ui.CreateVBox();
            fieldsBlock.AddChild(_ui.CreateLabel("Fields (name, type, default)"));
            fieldsBlock.AddChild(_fieldsBox);
            fieldsBlock.AddChild(_ui.CreateButton("+ Add Field", AddFieldRow));
            AddFieldRow(); // seed one row so the feature is discoverable
            box.AddChild(fieldsBlock);

            _customOnly = new IWidget[] { nameRow, langRow, packRow, pathRow, fieldsBlock };

            var targetRow = _ui.CreateHBox();
            targetRow.AddChild(InspectorLayout.RowLabel(_ui, "Target element"));
            targetRow.AddChild(_targetPicker);
            box.AddChild(targetRow);

            var buttons = _ui.CreateHBox();
            buttons.AddChild(_ui.CreateButton("Cancel", () => _onFinished()));
            buttons.AddChild(_ui.CreateButton("Create & Attach", OnCreate));
            box.AddChild(buttons);
            box.AddChild(_result);

            SyncMode();
            return box;
        }

        /// <summary>Display name for an element target row.</summary>
        private static string NameOf(IWorldElement el) => el.Name ?? "(unnamed)";

        private IWorldElement[] EnumerateTargets()
        {
            var world = _shell.Session.SceneWorld;
            if (world == null) return new[] { _target };
            var list = new List<IWorldElement>();
            foreach (var el in world.Root)
                Collect(el, list);
            return list.Count > 0 ? list.ToArray() : new[] { _target };

            static void Collect(IWorldElement el, List<IWorldElement> list)
            {
                list.Add(el);
                foreach (var child in el.Children)
                    Collect(child, list);
            }
        }

        private string CurrentGroupKey =>
            _group.SelectedIndex >= 0 && _group.SelectedIndex < _groupKeys.Count
                ? _groupKeys[_group.SelectedIndex]
                : CustomLabel;

        private bool IsCustom => CurrentGroupKey == CustomLabel;

        private IWorldElement CurrentTarget =>
            _targetChoices.Length > 0 && _targetPicker.SelectedIndex >= 0
                ? _targetChoices[Math.Min(_targetPicker.SelectedIndex, _targetChoices.Length - 1)]
                : _target;

        private CatalogEntry? SelectedEntry
        {
            get
            {
                if (IsCustom || _groupEntries.Count == 0) return null;
                var i = _entry.SelectedIndex;
                return i >= 0 && i < _groupEntries.Count ? _groupEntries[i] : null;
            }
        }

        /// <summary>Repopulate the entry dropdown when the group changes;
        /// "Your Components" re-scans the project folder each time.</summary>
        private void PopulateEntries()
        {
            var key = CurrentGroupKey;
            if (key == ComponentCatalog.YourComponentsCategory)
                _groupEntries = ComponentCatalog.DiscoverScripts(_shell.ProjectDir);
            else if (key == CustomLabel)
                _groupEntries = Array.Empty<CatalogEntry>();
            else
            {
                // Runtime C# components join their declared category. Compile the
                // project's scripts once so freshly authored files are listed.
                _shell.Session.CSharp.Ensure();
                _groupEntries = ComponentCatalog.BuiltInEntries
                    .Where(e => e.Category == key)
                    .Concat(ComponentCatalog.DiscoverCSharpComponents().Where(e => e.Category == key))
                    .ToList();
            }

            var names = _groupEntries.Select(ComponentCatalog.EntryDisplayName).ToArray();
            _entry.SetOptions(names.Length > 0 ? names : new[] { NoneLabel });
            if (_entry.SelectedIndex < 0 || _entry.SelectedIndex >= names.Length)
                _entry.SelectedIndex = 0;
        }

        private void SyncMode()
        {
            if (_customOnly.Length == 0 || _entryRow == null) return; // dropdown may fire before Build() finishes
            var custom = IsCustom;
            foreach (var w in _customOnly)
                w.Visible = custom;
            _entryRow.Visible = !custom;
        }

        private void SyncTarget()
        {
            _title.Text = $"New Component: {NameOf(CurrentTarget)}";
        }

        private void SyncPathFromName()
        {
            if (!IsCustom) return;
            var lang = ComponentCatalog.ScriptLanguages[Math.Max(0, _language.SelectedIndex)];
            var name = _name.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            _path.Text = $"scripts/{name}{lang.Extension}";
        }

        private void AddFieldRow()
        {
            var row = _ui.CreateHBox();
            var name = _ui.CreateTextField("name", _ => { });
            var type = _ui.CreateTextField("type (number, string…)", _ => { });
            var def = _ui.CreateTextField("default", _ => { });
            var remove = _ui.CreateButton("×", () =>
            {
                _fieldsBox.RemoveChild(row);
                _fieldRows.Remove(row);
                _fieldNames.Remove(name);
                _fieldTypes.Remove(type);
                _fieldDefaults.Remove(def);
            });
            row.AddChild(name);
            row.AddChild(type);
            row.AddChild(def);
            row.AddChild(remove);
            _fieldsBox.AddChild(row);
            _fieldRows.Add(row);
            _fieldNames.Add(name);
            _fieldTypes.Add(type);
            _fieldDefaults.Add(def);
        }

        private void OnCreate()
        {
            var entry = SelectedEntry;
            if (!IsCustom && entry == null)
            {
                _result.Text = CurrentGroupKey == ComponentCatalog.YourComponentsCategory
                    ? "No scripts found in the project folder. Use Custom script… to create one."
                    : "Choose a component to attach.";
                return;
            }

            var req = new CreateComponentRequest
            {
                Target = CurrentTarget,
                Pack = _pack.Text.Trim(),
                Builtin = entry,
            };

            if (IsCustom)
            {
                req.ComponentName = _name.Text.Trim();
                var lang = ComponentCatalog.ScriptLanguages[Math.Max(0, _language.SelectedIndex)];
                req.Language = lang.Language;
                req.CreatesPath = _path.Text.Trim();
                for (int i = 0; i < _fieldNames.Count; i++)
                {
                    var fname = _fieldNames[i].Text.Trim();
                    if (string.IsNullOrEmpty(fname)) continue;
                    req.Fields.Add(new ScriptField(
                        fname,
                        string.IsNullOrWhiteSpace(_fieldTypes[i].Text) ? "string" : _fieldTypes[i].Text.Trim(),
                        string.IsNullOrWhiteSpace(_fieldDefaults[i].Text) ? null : _fieldDefaults[i].Text.Trim()));
                }
            }

            var result = _shell.Creator.CreateAndAttach(req);
            if (result.Success && result.Attached)
            {
                _onFinished(); // §5: success shows the component in the Inspector
                return;
            }
            // §5: anything else explains itself and preserves every input.
            _result.Text = result.Message;
        }
    }
}
