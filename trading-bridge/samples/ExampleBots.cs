using System;
using System.Collections.Generic;
using Bridge.Contracts;

namespace Bridge.Samples
{
    /// <summary>
    /// Deliberately trivial placeholder logic. What matters is the SHAPE: no ATAS
    /// types, no order calls, no broker awareness -- just "given what I have seen,
    /// where do I want to be". Port your real RAIN / delta / structure logic into
    /// this shape and it runs live, in replay, and under test unchanged.
    /// </summary>
    public sealed class DeltaMomentumBot : StrategyBase
    {
        private readonly string _symbol;
        private readonly long _threshold;
        private readonly int _size;

        public override string Id { get; }
        public override IReadOnlyList<string> Symbols { get; }

        public DeltaMomentumBot(string id, string symbol, long deltaThreshold = 500, int size = 2)
        {
            Id = id; _symbol = symbol; _threshold = deltaThreshold; _size = size;
            Symbols = new[] { symbol };
        }

        public override void OnBar(in Bar bar, IStrategyContext ctx)
        {
            if (bar.Delta > _threshold) ctx.SetTarget(_symbol, _size, $"delta {bar.Delta}");
            else if (bar.Delta < -_threshold) ctx.SetTarget(_symbol, -_size, $"delta {bar.Delta}");
            else ctx.SetTarget(_symbol, 0, "delta neutral");
        }
    }

    /// <summary>Mean-reversion counterpart -- exists to demonstrate internal crossing.</summary>
    public sealed class RangeFadeBot : StrategyBase
    {
        private readonly string _symbol;
        private readonly int _size;
        private decimal _high, _low;
        private int _seen;

        public override string Id { get; }
        public override IReadOnlyList<string> Symbols { get; }

        public RangeFadeBot(string id, string symbol, int size = 1)
        {
            Id = id; _symbol = symbol; _size = size;
            Symbols = new[] { symbol };
        }

        public override void OnBar(in Bar bar, IStrategyContext ctx)
        {
            if (_seen < 20)
            {
                _seen++;
                _high = Math.Max(_high, bar.High);
                _low = _low == 0 ? bar.Low : Math.Min(_low, bar.Low);
                return;
            }

            if (bar.Close >= _high) ctx.SetTarget(_symbol, -_size, "fading range high");
            else if (bar.Close <= _low) ctx.SetTarget(_symbol, _size, "fading range low");
            else ctx.SetTarget(_symbol, 0, "inside range");

            _high = Math.Max(_high * 0.999m, bar.High);
            _low = Math.Min(_low * 1.001m, bar.Low);
        }
    }
}
