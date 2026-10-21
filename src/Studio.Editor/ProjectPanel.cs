using System;
using System.Collections.Generic;
using System.Linq;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    public sealed class ProjectPanel
    {
        private static readonly string[] Categories = { "Scenes", "Models", "Textures", "Audio", "Scripts", "Packs", "Other" };

        private readonly StudioShell _shell;
        private readonly IUIProvider _ui;
        private readonly ITextField _path;
        private readonly ITextField _filter;
        private readonly ILabel _detail;
        private readonly IButton _openScene;
        private readonly IAssetGrid _grid;
        private readonly List<AssetRecord> _assets = new();
        private readonly Dictionary<string, IToggle> _categoryButtons = new(StringComparer.OrdinalIgnoreCase);
        private string _category = "Scenes";
        private bool _syncingCategories;

        public IPanel Panel { get; }

        public ProjectPanel(StudioShell shell)
        {
            _shell = shell;
            _ui = shell.UI;
            Panel = _ui.CreatePanel("Project");

            var rootRow = _ui.CreateHBox();
            _path = _ui.CreateTextField("Project folder", _ => { });
            _path.Text = _shell.ProjectDir;
            rootRow.AddChild(_path);
            rootRow.AddChild(_ui.CreateButton("Scan", Rescan));
            rootRow.AddChild(_ui.CreateButton("Browse…", Browse));

            var browser = _ui.CreateHBox();
            var categoryRail = _ui.CreateVBox();
            // The rail hugs its labels instead of splitting the row 50/50
            // with the asset browser; the browser column takes the spare width.
            categoryRail.Expand = false;
            foreach (var category in Categories)
            {
                var toggle = _ui.CreateToggle(category, category == _category);
                toggle.SetOnToggled(pressed =>
                {
                    if (_syncingCategories) return;
                    if (!pressed)
                    {
                        if (string.Equals(category, _category, StringComparison.OrdinalIgnoreCase))
                        {
                            _syncingCategories = true;
                            toggle.Toggled = true;
                            _syncingCategories = false;
                        }
                        return;
                    }
                    _category = category;
                    _syncingCategories = true;
                    foreach (var (name, button) in _categoryButtons)
                        button.Toggled = string.Equals(name, _category, StringComparison.OrdinalIgnoreCase);
                    _syncingCategories = false;
                    RefreshGrid();
                });
                _categoryButtons.Add(category, toggle);
                categoryRail.AddChild(toggle);
            }

            var content = _ui.CreateVBox();
            var searchRow = _ui.CreateHBox();
            _filter = _ui.CreateTextField("Search assets", _ => RefreshGrid());
            searchRow.AddChild(_filter);
            content.AddChild(searchRow);

            _grid = _ui.CreateAssetGrid();
            _grid.SetOnSelectionChanged(OnSelectionChanged);
            _grid.SetOnItemActivated(OnItemActivated);
            content.AddChild(_grid);

            browser.AddChild(categoryRail);
            browser.AddChild(content);

            _detail = _ui.CreateLabel("Select an asset to view its path.");
            _openScene = _ui.CreateButton("Open scene", OpenSelectedScene);
            _openScene.Enabled = false;
            var footer = _ui.CreateHBox();
            footer.AddChild(_detail);
            footer.AddChild(_openScene);

            var box = _ui.CreateVBox();
            box.AddChild(rootRow);
            box.AddChild(browser);
            box.AddChild(footer);
            Panel.AddChild(box);

            Rescan();
        }

        private void Browse()
        {
            _ui.CreateFilePicker().ShowFolder("Select the project folder", dir =>
            {
                if (string.IsNullOrEmpty(dir)) return;
                _path.Text = dir;
                Rescan();
            });
        }

        private void Rescan()
        {
            var root = _path.Text.Trim();
            _assets.Clear();
            _assets.AddRange(_shell.Scanner.Scan(root));
            _detail.Text = _assets.Count == 0
                ? "No supported assets found. Check the project folder or scan depth."
                : "Select an asset to view its path.";
            _openScene.Enabled = false;
            RefreshGrid();
            _shell.Session.Log.Info($"Project scan of '{root}': {_assets.Count} asset(s).");
        }

        private void RefreshGrid()
        {
            var categoryType = _category switch
            {
                "Scenes" => "Scene",
                "Models" => "Model",
                "Textures" => "Texture",
                "Audio" => "Audio",
                "Scripts" => "Script",
                "Packs" => "Pack",
                _ => "Other",
            };
            var query = _filter.Text.Trim();
            var entries = _assets
                .Where(asset => categoryType == "Other"
                    ? asset.Type is "Data" or "Other"
                    : string.Equals(asset.Type, categoryType, StringComparison.OrdinalIgnoreCase))
                .Where(asset => query.Length == 0
                    || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || asset.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(asset => new AssetGridEntry(asset.Name, asset.Type, IconFor(asset.Type), asset))
                .ToList();
            _grid.SetItems(entries);
            if (_grid.SelectedUserData is AssetRecord selected)
            {
                _detail.Text = $"{selected.Type}  |  {selected.FullPath}";
                _openScene.Enabled = selected.Type == "Scene";
            }
            else
            {
                _detail.Text = entries.Count == 0
                    ? $"No {categoryType.ToLowerInvariant()} assets match."
                    : $"{entries.Count} {categoryType.ToLowerInvariant()} asset(s)";
                _openScene.Enabled = false;
            }
        }

        private void OnSelectionChanged(object? data)
        {
            if (data is AssetRecord asset)
            {
                _detail.Text = $"{asset.Type}  |  {asset.FullPath}";
                _openScene.Enabled = asset.Type == "Scene";
            }
            else
            {
                _detail.Text = "Select an asset to view its path.";
                _openScene.Enabled = false;
            }
        }

        private void OnItemActivated(object data)
        {
            if (data is AssetRecord { Type: "Scene" } scene)
                _shell.Session.LoadScene(scene.FullPath, out _);
        }

        private void OpenSelectedScene()
        {
            if (_grid.SelectedUserData is AssetRecord { Type: "Scene" } scene)
                _shell.Session.LoadScene(scene.FullPath, out _);
        }

        private static string IconFor(string type) => type switch
        {
            "Scene" => "scene",
            "Script" => "script",
            "Pack" => "asset",
            "Model" => "mesh",
            "Texture" => "texture",
            "Audio" => "audio",
            "Data" => "data",
            _ => "asset",
        };
    }
}
