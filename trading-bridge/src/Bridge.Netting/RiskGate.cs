using System;
using System.Collections.Generic;
using Bridge.Contracts;

namespace Bridge.Netting
{
    /// <summary>
    /// Account-level limits. Deliberately dumb and separate from strategy logic:
    /// a bug in any bot must not be able to talk its way past this.
    /// </summary>
    public sealed class RiskGate : IRiskGate
    {
        private readonly Dictionary<string, int> _maxNet;
        private readonly int _defaultMaxNet;
        private volatile bool _halted;

        public RiskGate(int defaultMaxNet = 5, Dictionary<string, int>? perSymbolMaxNet = null)
        {
            if (defaultMaxNet < 0) throw new ArgumentOutOfRangeException(nameof(defaultMaxNet));
            _defaultMaxNet = defaultMaxNet;
            _maxNet = perSymbolMaxNet ?? new Dictionary<string, int>();
        }

        public bool TradingHalted => _halted;

        /// <summary>
        /// Kill switch. Blocks new orders; it does not flatten. Flattening is an
        /// explicit operator decision, because auto-flatten on a data glitch is
        /// itself a way to lose money.
        /// </summary>
        public void Halt(string reason)
        {
            _halted = true;
            Console.WriteLine($"[risk] TRADING HALTED: {reason}");
        }

        public void Resume() => _halted = false;

        public int ClampTarget(string symbol, int desiredNet)
        {
            int max = _maxNet.TryGetValue(symbol, out int m) ? m : _defaultMaxNet;
            if (desiredNet > max) return max;
            if (desiredNet < -max) return -max;
            return desiredNet;
        }
    }
}
