using System;
using System.IO;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// Code tab: opens project scripts in the shared <see cref="ICodeEditor"/>
    /// (Origami's CodeEditor under the Paper provider) and writes edits back to disk.
    /// </summary>
    public sealed class CodePanel
    {
        private readonly StudioShell _shell;
        private readonly IUIProvider _ui;
        private readonly ICodeEditor _editor;
        private readonly ILabel _title;
        private readonly IButton _save;

        private string? _path;
        private bool _dirty;
        private bool _loading;

        public IPanel Panel { get; }

        /// <summary>Currently open file path, or null when the tab is empty.</summary>
        public string? Path => _path;

        public CodePanel(StudioShell shell)
        {
            _shell = shell;
            _ui = shell.UI;

            Panel = _ui.CreatePanel("Code");
            _title = _ui.CreateLabel("No file open — double-click a script in the Project tab.");
            _save = _ui.CreateButton("Save", Save);
            _save.Enabled = false;

            var header = _ui.CreateHBox();
            header.AddChild(_title, expand: true);
            header.AddChild(_save);

            _editor = _ui.CreateCodeEditor();
            _editor.SetOnChanged(_ =>
            {
                if (_loading) return;
                _dirty = true;
                UpdateTitle();
            });
            _editor.SetOnSaveRequested(Save);
            _editor.ReadOnly = true;

            var box = _ui.CreateVBox();
            box.AddChild(header);
            box.AddChild(_editor);
            Panel.AddChild(box);
        }

        /// <summary>Loads a file into the editor tab (UTF-8).</summary>
        public void Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _shell.Session.Log.Warn($"Cannot open '{path}': file not found.");
                return;
            }

            try
            {
                string full = System.IO.Path.GetFullPath(path);
                string text = File.ReadAllText(full);
                _loading = true;
                try
                {
                    _path = full;
                    string languageId = LanguageIdFor(full);
                    if (languageId == "csharp")
                        _editor.SetCompilerLanguage(new RoslynCodeLanguage(full, _shell.ProjectDir));
                    else
                        _editor.SetLanguage(languageId);
                    _editor.FilePath = full;
                    _editor.Text = text;
                    _editor.ReadOnly = false;
                }
                finally { _loading = false; }

                _dirty = false;
                _save.Enabled = true;
                UpdateTitle();
                _shell.Session.Log.Info($"Opened {full}");
            }
            catch (Exception ex)
            {
                _shell.Session.Log.Error($"Open '{path}' failed: {ex.Message}");
            }
        }

        /// <summary>Writes the editor buffer back to the open file.</summary>
        public void Save()
        {
            if (_path == null) return;
            try
            {
                File.WriteAllText(_path, _editor.Text);
                _dirty = false;
                UpdateTitle();
                _shell.Session.Log.Info($"Saved {_path}");
            }
            catch (Exception ex)
            {
                _shell.Session.Log.Error($"Save '{_path}' failed: {ex.Message}");
            }
        }

        private void UpdateTitle()
        {
            string name = _path == null ? "No file open" : System.IO.Path.GetFileName(_path);
            _title.Text = (_dirty ? "* " : "") + name;
        }

        private static string LanguageIdFor(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".lua" => "lua",
            _ => "plain",
        };
    }
}
