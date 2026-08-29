using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Bridge.Contracts;
using Bridge.Netting;

namespace Bridge.Host
{
    /// <summary>
    /// Runs N strategies against one market link and one netting engine.
    ///
    /// Everything happens on a single thread drained from one queue. That is the
    /// reason NettingEngine needs no locks, and the reason a slow strategy shows up
    /// as latency rather than as corrupted state. The cost is that one bot blocking
    /// blocks all of them -- so strategies must not do I/O, and QueueDepth is
    /// exposed so you can see it if one does.
    /// </summary>
    public sealed class StrategyHost : IDisposable
    {
        private enum EvKind { Bar, Tick, Position, Fill, Terminated, StrategyFill }

        private readonly struct Ev
        {
            public readonly EvKind Kind;
            public readonly Bar Bar; public readonly Tick Tick;
            public readonly PositionReport Pos; public readonly Fill Fill;
            public readonly StrategyFill SFill;
            public readonly string A, B;

            private Ev(EvKind k, Bar bar = default, Tick tick = default, PositionReport pos = default,
                       Fill fill = default, StrategyFill sfill = default, string a = "", string b = "")
            { Kind = k; Bar = bar; Tick = tick; Pos = pos; Fill = fill; SFill = sfill; A = a; B = b; }

            public static Ev OfBar(in Bar b) => new Ev(EvKind.Bar, bar: b);
            public static Ev OfTick(in Tick t) => new Ev(EvKind.Tick, tick: t);
            public static Ev OfPos(in PositionReport p) => new Ev(EvKind.Position, pos: p);
            public static Ev OfFill(in Fill f) => new Ev(EvKind.Fill, fill: f);
            public static Ev OfStrategyFill(in StrategyFill f) => new Ev(EvKind.StrategyFill, sfill: f);
            public static Ev OfTerminated(string id, string sym) => new Ev(EvKind.Terminated, a: id, b: sym);
        }

        private readonly BlockingCollection<Ev> _queue = new BlockingCollection<Ev>(16384);
        private readonly List<IStrategy> _strategies = new List<IStrategy>();
        private readonly Dictionary<string, Context> _contexts = new Dictionary<string, Context>();
        private readonly Dictionary<string, List<IStrategy>> _bySymbol = new Dictionary<string, List<IStrategy>>();
        private readonly NettingEngine _engine;
        private readonly IMarketLink _link;
        private readonly ISignalSink? _signals;
        private readonly Action<string> _log;
        private readonly List<Ev> _deferred = new List<Ev>(16);
        private readonly bool _synchronous;
        private Thread? _loop;
        private volatile bool _running;
        private int _handleDepth;

        public int QueueDepth => _queue.Count;
        public NettingEngine Engine => _engine;

        /// <param name="synchronous">
        /// Process events on the caller's thread instead of a background loop. Used
        /// by replay so a backtest is deterministic: with a queue in the middle,
        /// the same CSV can produce different results run to run, and a
        /// non-deterministic backtest is worse than no backtest.
        /// </param>
        public StrategyHost(IMarketLink link, NettingEngine engine, ISignalSink? signals = null,
                            Action<string>? log = null, bool synchronous = false)
        {
            _link = link; _engine = engine; _signals = signals;
            _synchronous = synchronous;
            _log = log ?? Console.WriteLine;

            _link.BarReceived += b => Enqueue(Ev.OfBar(b));
            _link.TickReceived += t => Enqueue(Ev.OfTick(t));
            _link.PositionReceived += p => Enqueue(Ev.OfPos(p));
            _link.FillReceived += f => Enqueue(Ev.OfFill(f));

            _engine.OrderRequested += o => _link.Send(o);
            _engine.StrategyFilled += f => Enqueue(Ev.OfStrategyFill(f));
        }

        public void Add(IStrategy strategy)
        {
            if (_running) throw new InvalidOperationException("Add strategies before Start.");
            if (_contexts.ContainsKey(strategy.Id))
                throw new ArgumentException($"Duplicate strategy id '{strategy.Id}'. Ids key the netting book, so they must be unique.");

            _strategies.Add(strategy);
            _contexts[strategy.Id] = new Context(strategy.Id, _engine, _signals, _log);
            foreach (var sym in strategy.Symbols)
            {
                if (!_bySymbol.TryGetValue(sym, out var list))
                    _bySymbol[sym] = list = new List<IStrategy>();
                list.Add(strategy);
            }
        }

        /// <summary>Wire a link's termination event in, if it exposes one.</summary>
        public void OnOrderTerminated(string orderId, string symbol) => Enqueue(Ev.OfTerminated(orderId, symbol));

        public void Start()
        {
            _running = true;
            foreach (var s in _strategies) s.OnStart(_contexts[s.Id]);
            _engine.Flush();
            if (!_synchronous)
            {
                _loop = new Thread(Loop) { IsBackground = false, Name = "strategy-host" };
                _loop.Start();
            }
            _link.Start();   // in synchronous mode this drives the whole replay inline
            _log($"[host] started with {_strategies.Count} strategies over {_bySymbol.Count} symbols");
        }

        public void Stop()
        {
            _running = false;
            if (!_synchronous) _queue.CompleteAdding();
            _loop?.Join(2000);
            foreach (var s in _strategies) s.OnStop(_contexts[s.Id]);
            _link.Stop();
        }

        private void Enqueue(Ev ev)
        {
            if (!_running) return;

            if (_synchronous)
            {
                // Strategy fills are raised from inside the engine's allocation
                // loop. Dispatching one immediately would let a bot call SetTarget
                // while the engine is mid-mutation and corrupt the book, so defer
                // until the top-level event has finished unwinding.
                if (_handleDepth > 0) { _deferred.Add(ev); return; }
                DispatchSync(ev);
                return;
            }

            if (!_queue.TryAdd(ev))
                _log("[host] EVENT QUEUE FULL -- a strategy is too slow, dropping market data");
        }

        private void DispatchSync(Ev ev)
        {
            _handleDepth++;
            try { Handle(ev); }
            catch (Exception ex) { _log($"[host] strategy error on {ev.Kind}: {ex}"); }
            finally { _handleDepth--; }

            if (_handleDepth == 0 && _deferred.Count > 0)
            {
                // Drain iteratively; a deferred fill can itself defer more.
                for (int i = 0; i < _deferred.Count; i++)
                {
                    var next = _deferred[i];
                    _handleDepth++;
                    try { Handle(next); }
                    catch (Exception ex) { _log($"[host] strategy error on {next.Kind}: {ex}"); }
                    finally { _handleDepth--; }
                }
                _deferred.Clear();
            }
        }

        private void Loop()
        {
            try
            {
                foreach (var ev in _queue.GetConsumingEnumerable())
                {
                    try { Handle(ev); }
                    catch (Exception ex)
                    {
                        // One misbehaving bot must not take the other four down with
                        // it. Log loudly and keep the loop alive.
                        _log($"[host] strategy error on {ev.Kind}: {ex}");
                    }
                }
            }
            catch (InvalidOperationException) { /* CompleteAdding during shutdown */ }
        }

        private void Handle(in Ev ev)
        {
            switch (ev.Kind)
            {
                case EvKind.Bar:
                    _engine.OnPrice(ev.Bar.Symbol, ev.Bar.Close);
                    // Batch the fan-out: every bot sees this bar and declares its
                    // target before a single order is cut, so offsetting intent
                    // crosses internally instead of paying the broker twice.
                    _engine.BeginBatch();
                    if (_bySymbol.TryGetValue(ev.Bar.Symbol, out var bars))
                        foreach (var s in bars) s.OnBar(ev.Bar, _contexts[s.Id]);
                    _engine.EndBatch();
                    break;

                case EvKind.Tick:
                    _engine.OnPrice(ev.Tick.Symbol, ev.Tick.Price);
                    _engine.BeginBatch();
                    if (_bySymbol.TryGetValue(ev.Tick.Symbol, out var ticks))
                        foreach (var s in ticks) s.OnTick(ev.Tick, _contexts[s.Id]);
                    _engine.EndBatch();
                    break;

                case EvKind.Position: _engine.OnBrokerPosition(ev.Pos); break;
                case EvKind.Fill: _engine.OnFill(ev.Fill); break;
                case EvKind.Terminated: _engine.OnOrderTerminated(ev.B, ev.A); break;

                case EvKind.StrategyFill:
                    if (_contexts.TryGetValue(ev.SFill.StrategyId, out var ctx))
                        foreach (var s in _strategies)
                            if (s.Id == ev.SFill.StrategyId) s.OnFill(ev.SFill, ctx);
                    break;
            }
        }

        public void Dispose() { Stop(); _queue.Dispose(); }

        /// <summary>Per-strategy view. Scopes every call to that strategy's own leg.</summary>
        private sealed class Context : IStrategyContext
        {
            private readonly string _id;
            private readonly NettingEngine _engine;
            private readonly ISignalSink? _signals;
            private readonly Action<string> _log;

            public Context(string id, NettingEngine engine, ISignalSink? signals, Action<string> log)
            { _id = id; _engine = engine; _signals = signals; _log = log; }

            public void SetTarget(string symbol, int signedQty, string note = "")
            {
                if (_engine.GetDesired(_id, symbol) == signedQty) return;
                _engine.SetTarget(_id, symbol, signedQty);
                _signals?.Publish(new Signal
                {
                    StrategyId = _id,
                    Symbol = symbol,
                    TargetQty = signedQty,
                    TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Note = note
                });
            }

            public int GetTarget(string symbol) => _engine.GetDesired(_id, symbol);
            public int GetPosition(string symbol) => _engine.GetAllocated(_id, symbol);
            public decimal GetAvgPrice(string symbol) => _engine.GetAvgPrice(_id, symbol);
            public void Log(string message) => _log($"[{_id}] {message}");
        }
    }
}
