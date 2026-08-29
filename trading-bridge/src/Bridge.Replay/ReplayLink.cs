using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Bridge.Contracts;

namespace Bridge.Replay
{
    /// <summary>
    /// Drop-in replacement for the live link that reads bars from CSV and fills
    /// orders against a simple model. Same IMarketLink surface, so a strategy
    /// cannot tell the difference -- which is the point: what you test here is
    /// literally the code that trades on Monday.
    ///
    /// CSV: timeMs,open,high,low,close,volume,delta  (header row optional)
    ///
    /// Fill model: market orders fill at the NEXT bar's open plus slippage, never
    /// at the close of the bar that triggered them. Filling at the signal bar's
    /// close is the single most common way a backtest lies to you.
    /// </summary>
    public sealed class ReplayLink : IMarketLink
    {
        private readonly string _symbol;
        private readonly string _path;
        private readonly decimal _slippagePerContract;
        private readonly List<OrderIntent> _pending = new List<OrderIntent>(8);
        private readonly Action<string> _log;

        public event Action<Bar>? BarReceived;
        public event Action<Tick>? TickReceived;
        public event Action<PositionReport>? PositionReceived;
        public event Action<Fill>? FillReceived;

        public int BarsReplayed { get; private set; }
        public int OrdersFilled { get; private set; }
        public decimal SlippagePaid { get; private set; }

        public ReplayLink(string symbol, string csvPath, decimal slippagePerContract = 0.25m, Action<string>? log = null)
        {
            _symbol = symbol;
            _path = csvPath;
            _slippagePerContract = slippagePerContract;
            _log = log ?? Console.WriteLine;
        }

        public void Send(in OrderIntent intent) => _pending.Add(intent);

        public void Stop() { }

        /// <summary>
        /// Runs the whole file synchronously on the calling thread. Synchronous on
        /// purpose: a replay that races the host's queue produces different results
        /// run to run, and a non-deterministic backtest is worse than none.
        /// </summary>
        public void Start()
        {
            foreach (var bar in ReadBars())
            {
                // Fill what last bar's logic asked for, at this bar's open.
                FillPending(bar.Open, bar.TimeMs);
                BarReceived?.Invoke(bar);
                BarsReplayed++;
            }
            _log($"[replay] {BarsReplayed} bars, {OrdersFilled} orders filled, {SlippagePaid:F2} slippage paid");
        }

        private void FillPending(decimal openPrice, long timeMs)
        {
            if (_pending.Count == 0) return;
            // Copy and clear first: filling raises events that can enqueue new
            // orders, and mutating the list mid-iteration would drop them.
            var batch = _pending.ToArray();
            _pending.Clear();

            foreach (var o in batch)
            {
                int qty = Math.Abs(o.SignedQty);
                decimal slip = _slippagePerContract * Math.Sign(o.SignedQty);
                decimal price = openPrice + slip;
                SlippagePaid += _slippagePerContract * qty;
                OrdersFilled++;
                FillReceived?.Invoke(new Fill(o.ClientOrderId, o.Symbol, o.SignedQty, price, timeMs));
            }
        }

        private IEnumerable<Bar> ReadBars()
        {
            var inv = CultureInfo.InvariantCulture;
            using var reader = new StreamReader(_path);
            string? line;
            bool first = true;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var f = line.Split(',');
                if (first)
                {
                    first = false;
                    if (!long.TryParse(f[0], out _)) continue;   // header row
                }
                if (f.Length < 5) continue;

                long t = long.Parse(f[0], inv);
                decimal o = decimal.Parse(f[1], NumberStyles.Float, inv);
                decimal h = decimal.Parse(f[2], NumberStyles.Float, inv);
                decimal l = decimal.Parse(f[3], NumberStyles.Float, inv);
                decimal c = decimal.Parse(f[4], NumberStyles.Float, inv);
                long v = f.Length > 5 ? long.Parse(f[5], inv) : 0;
                long d = f.Length > 6 ? long.Parse(f[6], inv) : 0;
                yield return new Bar(_symbol, t, o, h, l, c, v, d);
            }
        }
    }
}
