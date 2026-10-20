using System;
using System.IO;
using System.Linq;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// "Packs" tab: discover *.v12pak in a directory (manifest-only summary)
    /// and load the selected pack into the running runtime for real.
    /// </summary>
    public sealed class PacksPanel
    {
        private readonly StudioShell _shell;
        private readonly IUIProvider _ui;
        private readonly ITreeView _tree;
        private readonly ITextField _dir;
        private readonly ILabel _detail;
        private readonly ILabel _loaded;

        public IPanel Panel { get; }

        public PacksPanel(StudioShell shell)
        {
            _shell = shell;
            _ui = shell.UI;

            Panel = _ui.CreatePanel("Packs");

            var defaultDir = Path.Combine(AppContext.BaseDirectory, "gamepaks");
            var top = _ui.CreateHBox();
            _dir = _ui.CreateTextField(defaultDir, _ => { });
            _dir.Text = Directory.Exists(defaultDir) ? defaultDir : AppContext.BaseDirectory;
            var scan = _ui.CreateButton("Scan", Rescan);
            top.AddChild(_dir);
            top.AddChild(scan);

            _tree = _ui.CreateTreeView();
            _tree.SetDropEnabled(false);
            _tree.SetOnSelectionChanged(item =>
                _detail.Text = item?.UserData is PackInfo info
                    ? $"{info.Path} — {info.WorldCount} world(s), {info.AssetCount} asset(s), {info.GamepakCount} gamepak(s), {info.ScriptCount} script(s)"
                    : "");

            _detail = _ui.CreateLabel("");
            _loaded = _ui.CreateLabel("");

            var actions = _ui.CreateHBox();
            actions.AddChild(_ui.CreateButton("Load Selected Pack", () =>
            {
                if (_tree.SelectedItem?.UserData is PackInfo info)
                {
                    _shell.Packs.Load(info.Path);
                    RefreshLoaded();
                }
            }));

            var box = _ui.CreateVBox();
            box.AddChild(top);
            box.AddChild(_tree);
            box.AddChild(_detail);
            box.AddChild(actions);
            box.AddChild(_loaded);
            Panel.AddChild(box);

            Rescan();
        }

        private void Rescan()
        {
            var dir = _dir.Text.Trim();
            var packs = _shell.Packs.Discover(dir);

            _tree.Clear();
            foreach (var pack in packs)
            {
                var item = _tree.AddItem(null, pack.Name, pack);
                item.IconKey = "asset";
            }

            if (packs.Count == 0)
                _shell.Session.Log.Warn($"No .v12pak files found in '{dir}'.");
            else
                _shell.Session.Log.Info($"Found {packs.Count} pack(s) in '{dir}'.");

            RefreshLoaded();
        }

        private void RefreshLoaded()
        {
            var loaded = _shell.Packs.LoadedPaks;
            _loaded.Text = loaded.Count == 0
                ? "Loaded packs: none"
                : "Loaded packs: " + string.Join(", ", loaded.Select(Path.GetFileName));
        }
    }
}
