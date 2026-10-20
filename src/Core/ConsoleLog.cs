using System;
using System.Collections.Generic;

namespace V12Studio.Core
{
    public enum LogLevel { Info, Warning, Error }

    /// <summary>One editor console line.</summary>
    public sealed record ConsoleEntry(DateTime Timestamp, LogLevel Level, string Message);

    /// <summary>
    /// Editor console model. UI layers render this; they never write to
    /// Console.WriteLine to talk back to the user.
    /// </summary>
    public sealed class ConsoleLog
    {
        private readonly object _gate = new();
        private readonly List<ConsoleEntry> _entries = new();

        /// <summary>Raised (on the calling thread) whenever entries are added
        /// or cleared. UI layers refresh on this.</summary>
        public event Action? Changed;

        public IReadOnlyList<ConsoleEntry> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public int InfoCount { get; private set; }
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }

        public void Add(string message, LogLevel level = LogLevel.Info)
        {
            lock (_gate)
            {
                _entries.Add(new ConsoleEntry(DateTime.Now, level, message));
                switch (level)
                {
                    case LogLevel.Info: InfoCount++; break;
                    case LogLevel.Warning: WarningCount++; break;
                    case LogLevel.Error: ErrorCount++; break;
                }
            }
            Changed?.Invoke();
        }

        public void Info(string message) => Add(message, LogLevel.Info);
        public void Warn(string message) => Add(message, LogLevel.Warning);
        public void Error(string message) => Add(message, LogLevel.Error);

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                InfoCount = WarningCount = ErrorCount = 0;
            }
            Changed?.Invoke();
        }
    }
}
