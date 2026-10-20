using V12.Core.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Core;
using V12.Components.UI;

namespace V12Studio.Core
{
    /// <summary>
    /// In-app file browser behind <see cref="IViewportInteraction.ShowFileDialog"/>
    /// (the godot host renders a native FileDialog; MonoGame has no native
    /// dialogs, so this builds the same job as V12 UI in its own screen-space
    /// canvas, centered via CanvasComponent's default 0.5 anchors).
    ///
    /// Behaviour matches nova's dialog: initial path splits into start
    /// directory + default file name, no filters (all files listed), Open/Save
    /// returns the chosen path and Cancel (or Esc) returns null. Directories
    /// first, ".." row when there is a parent; clicking a file fills the name
    /// field, confirming combines it with the current directory.
    ///
    /// The dialog paints on top because its canvas is captured after the
    /// editor's (PersistentWorld root order) and new Gum controls append to the
    /// end of the root; while open, ViewportInteractionService keeps the
    /// viewport inert so nothing picks through it.
    /// </summary>
    public sealed class ViewportFileDialog
    {
        private const int DialogWidth = 720;
        private const int DialogHeight = 520;

        private readonly GameRoot _root;

        private Element? _canvas;
        private Element? _listView;
        private Element? _pathLabel;
        private TextInputComponent? _nameInput;
        private bool _save;
        private string _dir = "";
        private Action<string?>? _onComplete;

        public ViewportFileDialog(GameRoot root)
        {
            _root = root;
        }

        public bool IsOpen => _canvas != null;

        /// <summary>Build the dialog and show it. Any previous open dialog is
        /// cancelled first so its caller is never left without a callback.</summary>
        public void Open(bool save, string title, string initialPath, Action<string?> onComplete)
        {
            if (IsOpen) Cancel();

            _save = save;
            _onComplete = onComplete;
            _dir = StartDirectory(initialPath);

            var canvas = new Element("FileDialogCanvas");
            canvas.AddComponent(new CanvasComponent
            {
                ScreenSpace = true,
                Width = DialogWidth,
                Height = DialogHeight,
                // AnchorX/AnchorY default to 0.5 → centered overlay.
            });

            // Background first so the content paints on top of it.
            var bg = new Element("FileDialogBg");
            bg.AddComponent(new RectComponent
            {
                BackgroundColor = "#17181d",
                CornerRadius = 8f,
                Width = DialogWidth,
                Height = DialogHeight,
            });
            canvas.AddChild(bg);

            var root = new Element("FileDialogRoot");
            root.AddComponent(new VLayoutComponent { Spacing = 6, Padding = 14, Expand = true });
            canvas.AddChild(root);

            var titleEl = new Element("FileDialogTitle");
            titleEl.AddComponent(new LabelComponent(title));
            titleEl.AddComponent(new UIStyleComponent { StyleHint = "title" });
            root.AddChild(titleEl);

            _pathLabel = new Element("FileDialogPath");
            _pathLabel.AddComponent(new LabelComponent(_dir));
            _pathLabel.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            root.AddChild(_pathLabel);

            var listScroll = new Element("FileDialogListScroll");
            listScroll.AddComponent(new ScrollComponent());
            listScroll.AddComponent(new LayoutElementComponent { FlexibleHeight = 1 });
            root.AddChild(listScroll);

            _listView = new Element("FileDialogList");
            _listView.AddComponent(new VLayoutComponent { Spacing = 1, Padding = 0 });
            listScroll.AddChild(_listView);

            var nameRow = new Element("FileDialogNameRow");
            nameRow.AddComponent(new HLayoutComponent { Spacing = 6, Padding = 0 });
            var nameLabel = new Element("FileDialogNameLabel");
            nameLabel.AddComponent(new LabelComponent("File"));
            nameRow.AddChild(nameLabel);
            var nameEl = new Element("FileDialogName");
            _nameInput = new TextInputComponent { Placeholder = "file name" };
            _nameInput.Value = SafeFileName(initialPath);
            nameEl.AddComponent(_nameInput);
            nameEl.AddComponent(new LayoutElementComponent { FlexibleWidth = 1, MinHeight = 26 });
            nameRow.AddChild(nameEl);
            root.AddChild(nameRow);

            var buttons = new Element("FileDialogButtons");
            buttons.AddComponent(new HLayoutComponent { Spacing = 6, Padding = 0 });
            var spacer = new Element("FileDialogSpacer");
            spacer.AddComponent(new LabelComponent(""));
            spacer.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
            buttons.AddChild(spacer);
            var cancelBtn = new Element("FileDialogCancel");
            cancelBtn.AddComponent(new ButtonComponent("Cancel", Cancel));
            cancelBtn.AddComponent(new LayoutElementComponent { MinWidth = 96, MinHeight = 26 });
            buttons.AddChild(cancelBtn);
            var okBtn = new Element("FileDialogConfirm");
            okBtn.AddComponent(new ButtonComponent(save ? "Save" : "Open", Confirm));
            okBtn.AddComponent(new UIStyleComponent { StyleHint = "accent" });
            okBtn.AddComponent(new LayoutElementComponent { MinWidth = 96, MinHeight = 26 });
            buttons.AddChild(okBtn);
            root.AddChild(buttons);

            _canvas = canvas;
            _root.PersistentWorld.AddElement(canvas); // also marks the frame dirty
            RebuildList();
            Console.WriteLine($"[FileDialog] {(save ? "Save" : "Open")} dialog '{title}' in {_dir}");
        }

        /// <summary>Dismiss without a result — the caller's callback gets null.</summary>
        public void Cancel() => Complete(null);

        // ── Directory listing ─────────────────────────────────────────────

        /// <summary>Rebuild the entry rows for <see cref="_dir"/>. Rows are
        /// plain buttons: "..", then directories, then files.</summary>
        private void RebuildList()
        {
            if (_listView == null) return;

            foreach (var child in _listView.Children.ToList())
                _listView.RemoveChild(child);

            if (_pathLabel?.GetComponent<LabelComponent>() is { } pathLabel)
                pathLabel.Text = _dir;

            List<string> dirs, files;
            try
            {
                dirs = Directory.GetDirectories(_dir).ToList();
                files = Directory.GetFiles(_dir).ToList();
            }
            catch (Exception ex)
            {
                var error = new Element("FileDialogError");
                error.AddComponent(new LabelComponent("Cannot read directory: " + ex.Message));
                error.AddComponent(new UIStyleComponent { StyleHint = "muted" });
                _listView.AddChild(error);
                _root.MarkRenderDirty();
                return;
            }

            var parent = Directory.GetParent(_dir);
            if (parent != null)
                AddRow("..", () => Navigate(parent.FullName));

            foreach (var d in dirs.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                AddRow(name + "/", () => Navigate(d));
            }
            foreach (var f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(f);
                AddRow(name, () => SelectFile(name));
            }

            _root.MarkRenderDirty();
        }

        private void AddRow(string label, Action onClick)
        {
            var row = new Element("FileDialogRow_" + label);
            row.AddComponent(new ButtonComponent(label, onClick));
            row.AddComponent(new LayoutElementComponent { FlexibleWidth = 1, MinHeight = 24 });
            _listView!.AddChild(row);
        }

        private void Navigate(string dir)
        {
            if (!Directory.Exists(dir)) return;
            _dir = dir;
            RebuildList();
        }

        /// <summary>Click a file row: fill the name field (confirming is a
        /// separate step, matching native open dialogs — no double-click here).</summary>
        private void SelectFile(string fileName)
        {
            if (_nameInput != null)
                _nameInput.Value = fileName; // marks the frame dirty
        }

        // ── Completion ────────────────────────────────────────────────────

        private void Confirm()
        {
            var name = _nameInput?.Value?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("[FileDialog] No file name entered.");
                return;
            }

            string path;
            try
            {
                // A rooted name typed by hand wins over the current directory.
                path = Path.GetFullPath(Path.Combine(_dir, name));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileDialog] Bad path '{name}': {ex.Message}");
                return;
            }
            Complete(path);
        }

        /// <summary>Tear the dialog down, then hand the result to the caller.
        /// Removal happens first so the callback can react (e.g. refill the
        /// scene path field) against a clean frame.</summary>
        private void Complete(string? path)
        {
            var cb = _onComplete;
            _onComplete = null;

            if (_canvas != null)
            {
                _root.PersistentWorld.RemoveElement(_canvas); // also marks dirty
                _canvas = null;
            }
            _listView = null;
            _pathLabel = null;
            _nameInput = null;

            try { cb?.Invoke(path); }
            catch (Exception ex) { Console.WriteLine($"[FileDialog] completion handler failed: {ex.Message}"); }
        }

        // ── Path helpers ──────────────────────────────────────────────────

        /// <summary>Directory the dialog opens in: the initial path's folder
        /// when it exists, else the executable's directory (same resolution the
        /// editor uses for relative scene paths).</summary>
        private static string StartDirectory(string? initialPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(initialPath))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(initialPath));
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch { /* malformed initial path — fall through */ }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string SafeFileName(string? initialPath)
        {
            try { return string.IsNullOrEmpty(initialPath) ? "" : Path.GetFileName(initialPath); }
            catch { return ""; }
        }
    }
}
