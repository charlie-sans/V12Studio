using System;
using System.Linq;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// "Console" tab: the session log with level counts and a clear button.
    /// Bounded to the most recent 200 entries so a chatty scene cannot grow
    /// the UI without limit.
    /// </summary>
    public sealed class ConsolePanel
    {
        private const int MaxShown = 200;

        private readonly StudioSession _session;
        private readonly IUIProvider _ui;
        private readonly IScrollView _scroll;
        private readonly ILabel _counts;
        private IWidget? _list;

        public IPanel Panel { get; }

        public ConsolePanel(StudioShell shell)
        {
            _session = shell.Session;
            _ui = shell.UI;

            Panel = _ui.CreatePanel("Console");

            var top = _ui.CreateHBox();
            top.AddChild(_ui.CreateButton("Clear", _session.Log.Clear));
            _counts = _ui.CreateLabel("");
            top.AddChild(_counts);

            _scroll = _ui.CreateScrollView();

            var box = _ui.CreateVBox();
            box.AddChild(top);
            box.AddChild(_scroll);
            Panel.AddChild(box);

            _session.Log.Changed += Refresh;
            Refresh();
        }

        private void Refresh()
        {
            var entries = _session.Log.Entries;
            _counts.Text = $"{entries.Count} entries — {_session.Log.ErrorCount} error(s), " +
                           $"{_session.Log.WarningCount} warning(s)";

            var list = _ui.CreateVBox();
            foreach (var entry in entries.TakeLast(MaxShown))
            {
                var prefix = entry.Level switch
                {
                    LogLevel.Warning => "warn: ",
                    LogLevel.Error => "error: ",
                    _ => "",
                };
                list.AddChild(_ui.CreateLabel(
                    $"[{entry.Timestamp:HH:mm:ss}] {prefix}{entry.Message}"));
            }
            if (entries.Count == 0)
                list.AddChild(_ui.CreateLabel("(console is empty)"));

            _scroll.SetContent(list);
            _list = list;
        }
    }
}
