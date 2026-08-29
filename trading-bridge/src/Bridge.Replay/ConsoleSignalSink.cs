using System;
using System.Globalization;
using System.IO;
using Bridge.Contracts;

namespace Bridge.Replay
{
    /// <summary>
    /// Writes every signal to stdout and, optionally, to a CSV journal.
    ///
    /// This file is the thing you sell. A timestamped, append-only record of every
    /// call the bots made is what turns "trust me" into a track record -- and it
    /// costs nothing because the signal already exists inside the engine.
    /// </summary>
    public sealed class ConsoleSignalSink : ISignalSink, IDisposable
    {
        private readonly StreamWriter? _journal;
        private readonly bool _quiet;

        public ConsoleSignalSink(string? journalPath = null, bool quiet = false)
        {
            _quiet = quiet;
            if (journalPath != null)
            {
                bool isNew = !File.Exists(journalPath);
                _journal = new StreamWriter(journalPath, append: true) { AutoFlush = true };
                if (isNew) _journal.WriteLine("timeMs,strategy,symbol,targetQty,note");
            }
        }

        public void Publish(Signal s)
        {
            string dir = s.TargetQty > 0 ? "LONG" : s.TargetQty < 0 ? "SHORT" : "FLAT";
            if (!_quiet)
                Console.WriteLine($"[signal] {s.StrategyId} {s.Symbol} -> {dir} {Math.Abs(s.TargetQty)} {s.Note}");

            _journal?.WriteLine(string.Join(",",
                s.TimeMs.ToString(CultureInfo.InvariantCulture),
                s.StrategyId, s.Symbol,
                s.TargetQty.ToString(CultureInfo.InvariantCulture),
                s.Note.Replace(',', ';')));
        }

        public void Dispose() => _journal?.Dispose();
    }
}
