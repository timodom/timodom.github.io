using System;
using System.Collections.Generic;

namespace Bridge.Contracts
{
    /// <summary>
    /// What a bot sees. Deliberately tiny: a strategy can set a target, read its
    /// own position, and log. It cannot send orders, cancel, or see other
    /// strategies -- that is the netting engine's job, and keeping it out of here
    /// is what makes bots safe to run five-at-a-time in one process.
    /// </summary>
    public interface IStrategyContext
    {
        /// <summary>Idempotent. Calling with the same value twice is a no-op.</summary>
        void SetTarget(string symbol, int signedQty, string note = "");

        int GetTarget(string symbol);
        int GetPosition(string symbol);      // this strategy's allocated position
        decimal GetAvgPrice(string symbol);  // this strategy's average entry
        void Log(string message);
    }

    /// <summary>
    /// A bot. No ATAS types anywhere in this interface -- that is the whole point.
    /// The same class runs live, in replay, and in unit tests.
    /// </summary>
    public interface IStrategy
    {
        string Id { get; }
        IReadOnlyList<string> Symbols { get; }

        void OnStart(IStrategyContext ctx);
        void OnBar(in Bar bar, IStrategyContext ctx);
        void OnTick(in Tick tick, IStrategyContext ctx);
        void OnFill(in StrategyFill fill, IStrategyContext ctx);
        void OnStop(IStrategyContext ctx);
    }

    /// <summary>Convenience base so bots only override what they use.</summary>
    public abstract class StrategyBase : IStrategy
    {
        public abstract string Id { get; }
        public abstract IReadOnlyList<string> Symbols { get; }
        public virtual void OnStart(IStrategyContext ctx) { }
        public virtual void OnBar(in Bar bar, IStrategyContext ctx) { }
        public virtual void OnTick(in Tick tick, IStrategyContext ctx) { }
        public virtual void OnFill(in StrategyFill fill, IStrategyContext ctx) { }
        public virtual void OnStop(IStrategyContext ctx) { }
    }

    /// <summary>
    /// Anything that can carry market data in and orders out: the live ATAS
    /// socket, a CSV replay, or a test double.
    /// </summary>
    public interface IMarketLink
    {
        event Action<Bar> BarReceived;
        event Action<Tick> TickReceived;
        event Action<PositionReport> PositionReceived;
        event Action<Fill> FillReceived;

        void Send(in OrderIntent intent);
        void Start();
        void Stop();
    }

    /// <summary>
    /// Last line of defence before an order leaves the building. Separate from the
    /// netting engine on purpose: netting decides what you *want*, risk decides
    /// what you are *allowed*.
    /// </summary>
    public interface IRiskGate
    {
        /// <summary>Clamp a desired net position. Return value is what may be sought.</summary>
        int ClampTarget(string symbol, int desiredNet);
        bool TradingHalted { get; }
    }

    public interface ISignalSink
    {
        void Publish(Signal signal);
    }
}
