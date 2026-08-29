using System;
using System.Collections.Generic;
using Bridge.Contracts;

namespace Bridge.Netting
{
    /// <summary>
    /// Lets N strategies share one broker account without corrupting each other's
    /// position view.
    ///
    /// Each strategy declares a target position. The engine keeps a per-strategy
    /// virtual book, crosses opposing intent internally (no broker order, no
    /// commission, no slippage), and sends only the residual delta to the broker.
    /// Broker fills are allocated back pro-rata so every strategy has a real
    /// position and a real average price.
    ///
    /// NOT thread-safe by design. Drive it from a single thread -- StrategyHost
    /// owns that loop. Locking here would be a bug magnet for no gain, because
    /// every mutation is already serialised by the event queue.
    ///
    /// Invariant, asserted in tests: sum(allocated per strategy) == BrokerActual.
    /// </summary>
    public sealed class NettingEngine
    {
        private sealed class Leg
        {
            public int Desired;
            public int Allocated;
            public decimal AvgPrice;
        }

        private sealed class Book
        {
            public readonly Dictionary<string, Leg> Legs = new Dictionary<string, Leg>(8);
            public int BrokerActual;
            public int WorkingQty;              // signed, live child orders
            public decimal LastPrice;
            public bool HasPrice;
            public int RejectStreak;            // consecutive terminations, no fill between
            public bool Dirty;
            public readonly Dictionary<string, int> OpenOrders = new Dictionary<string, int>(8);
        }

        private readonly Dictionary<string, Book> _books = new Dictionary<string, Book>(8);
        private readonly List<string> _dirty = new List<string>(8);
        private readonly IRiskGate _risk;
        private readonly Action<string> _log;
        private readonly int _maxRejectStreak;
        private long _orderSeq;
        private int _batchDepth;

        /// <summary>Raised when an order must go to the broker.</summary>
        public event Action<OrderIntent>? OrderRequested;

        /// <summary>Raised for every strategy-level fill, broker or internal cross.</summary>
        public event Action<StrategyFill>? StrategyFilled;

        public NettingEngine(IRiskGate risk, Action<string>? log = null, int maxRejectStreak = 3)
        {
            _risk = risk ?? throw new ArgumentNullException(nameof(risk));
            _log = log ?? (_ => { });
            _maxRejectStreak = maxRejectStreak;
        }

        // ------------------------------------------------------------- batching

        /// <summary>
        /// Defer order generation until EndBatch. The host wraps the fan-out of a
        /// single market event in a batch so every strategy has declared its target
        /// before any order is cut. Without this, a bot that exits on the same bar
        /// another bot enters produces two broker round-trips instead of a free
        /// internal cross -- correct, but it pays commission and slippage for
        /// nothing. Re-entrant so nesting is safe.
        /// </summary>
        public void BeginBatch() => _batchDepth++;

        public void EndBatch()
        {
            if (_batchDepth > 0) _batchDepth--;
            if (_batchDepth == 0) Flush();
        }

        /// <summary>Process every symbol whose intent changed since the last flush.</summary>
        public void Flush()
        {
            // Rebalance can dirty further symbols (it cannot today, but a future
            // hedge leg would), so drain rather than iterate a snapshot.
            while (_dirty.Count > 0)
            {
                string symbol = _dirty[_dirty.Count - 1];
                _dirty.RemoveAt(_dirty.Count - 1);
                var book = GetBook(symbol);
                book.Dirty = false;
                Rebalance(symbol, book);
            }
        }

        private void MarkDirty(string symbol, Book book)
        {
            if (!book.Dirty)
            {
                book.Dirty = true;
                _dirty.Add(symbol);
            }
            if (_batchDepth == 0) Flush();
        }

        // ---------------------------------------------------------------- inputs

        /// <summary>A strategy declares where it wants to be. Idempotent.</summary>
        public void SetTarget(string strategyId, string symbol, int signedQty)
        {
            var book = GetBook(symbol);
            var leg = GetLeg(book, strategyId);
            if (leg.Desired == signedQty) return;   // idempotent: no churn, no order
            leg.Desired = signedQty;
            MarkDirty(symbol, book);
        }

        /// <summary>Reference price for internal crosses. Cheap; called per tick.</summary>
        public void OnPrice(string symbol, decimal price)
        {
            var book = GetBook(symbol);
            book.LastPrice = price;
            if (!book.HasPrice)
            {
                book.HasPrice = true;
                MarkDirty(symbol, book);   // a cross may have been waiting on a price
            }
        }

        /// <summary>
        /// Broker truth wins. Called on connect and on every position update.
        /// Because targets are idempotent, a restart re-derives the correct delta
        /// from here without any persisted order state.
        /// </summary>
        public void OnBrokerPosition(in PositionReport report)
        {
            var book = GetBook(report.Symbol);
            if (book.BrokerActual == report.NetQty) return;

            int drift = report.NetQty - book.BrokerActual;
            _log($"[netting] {report.Symbol} broker position reconciled {book.BrokerActual} -> {report.NetQty} (drift {drift:+#;-#;0})");
            book.BrokerActual = report.NetQty;

            // Drift means someone traded outside the engine (manual click, a fill we
            // missed). Park it on the house leg so sum(allocated) == BrokerActual
            // stays true rather than silently mis-attributing it to a strategy.
            if (drift != 0)
            {
                var house = GetLeg(book, HouseLeg);
                house.Allocated += drift;
            }
            MarkDirty(report.Symbol, book);
        }

        public void OnFill(in Fill fill)
        {
            var book = GetBook(fill.Symbol);

            if (book.OpenOrders.TryGetValue(fill.ClientOrderId, out int remaining))
            {
                int consumed = Math.Sign(fill.SignedQty) == Math.Sign(remaining)
                    ? Math.Min(Math.Abs(fill.SignedQty), Math.Abs(remaining)) * Math.Sign(fill.SignedQty)
                    : fill.SignedQty;
                int left = remaining - consumed;
                if (left == 0) book.OpenOrders.Remove(fill.ClientOrderId);
                else book.OpenOrders[fill.ClientOrderId] = left;
                book.WorkingQty -= consumed;
            }

            book.BrokerActual += fill.SignedQty;
            book.LastPrice = fill.Price;
            book.HasPrice = true;

            book.RejectStreak = 0;   // a fill proves the path works again

            AllocateToStrategies(fill.Symbol, book, fill.SignedQty, fill.Price, fill.TimeMs, FillSource.Broker);
            MarkDirty(fill.Symbol, book);
        }

        /// <summary>
        /// Cancelled or rejected. Without this the symbol's WorkingQty never
        /// drains and the engine stops sending orders forever -- the single most
        /// likely way a system like this silently dies mid-session.
        /// </summary>
        public void OnOrderTerminated(string symbol, string clientOrderId)
        {
            var book = GetBook(symbol);
            if (!book.OpenOrders.TryGetValue(clientOrderId, out int remaining)) return;
            book.OpenOrders.Remove(clientOrderId);
            book.WorkingQty -= remaining;
            book.RejectStreak++;
            _log($"[netting] {symbol} order {clientOrderId} terminated, released {remaining:+#;-#;0}");

            // A broker that rejects every order would otherwise be met with an
            // infinite resend loop -- the fastest way to get an account throttled
            // or banned. Stop retrying and make a human look at it.
            if (book.RejectStreak >= _maxRejectStreak)
            {
                _log($"[netting] {symbol} SUSPENDED after {book.RejectStreak} consecutive rejects; " +
                     "call ResumeAfterRejects once the cause is fixed");
                return;
            }
            MarkDirty(symbol, book);
        }

        // ------------------------------------------------------------- accessors

        public int GetDesired(string strategyId, string symbol)
            => TryLeg(symbol, strategyId, out var leg) ? leg.Desired : 0;

        public int GetAllocated(string strategyId, string symbol)
            => TryLeg(symbol, strategyId, out var leg) ? leg.Allocated : 0;

        public decimal GetAvgPrice(string strategyId, string symbol)
            => TryLeg(symbol, strategyId, out var leg) ? leg.AvgPrice : 0m;

        public int GetBrokerPosition(string symbol)
            => _books.TryGetValue(symbol, out var b) ? b.BrokerActual : 0;

        public int GetWorkingQty(string symbol)
            => _books.TryGetValue(symbol, out var b) ? b.WorkingQty : 0;

        /// <summary>Position acquired outside the engine. Never owned by a strategy.</summary>
        public const string HouseLeg = "__house__";

        // ------------------------------------------------------------------ core

        /// <summary>Clear a reject suspension after an operator has fixed the cause.</summary>
        public void ResumeAfterRejects(string symbol)
        {
            var book = GetBook(symbol);
            book.RejectStreak = 0;
            MarkDirty(symbol, book);
        }

        public bool IsSuspended(string symbol)
            => _books.TryGetValue(symbol, out var b) && b.RejectStreak >= _maxRejectStreak;

        private void Rebalance(string symbol, Book book)
        {
            // Crossing is internal bookkeeping, so it stays available even while
            // the broker path is suspended or halted.
            CrossInternally(symbol, book);

            if (book.RejectStreak >= _maxRejectStreak) return;

            int netDesired = 0;
            foreach (var kv in book.Legs)
                if (kv.Key != HouseLeg) netDesired += kv.Value.Desired;

            // House position is real exposure the strategies never asked for, so it
            // must be carried into the broker target or the engine would try to
            // trade it away and fight the operator.
            if (book.Legs.TryGetValue(HouseLeg, out var houseLeg))
                netDesired += houseLeg.Allocated;

            int allowed = _risk.ClampTarget(symbol, netDesired);
            if (allowed != netDesired)
                _log($"[risk] {symbol} target clamped {netDesired} -> {allowed}");

            if (_risk.TradingHalted) return;

            int delta = allowed - book.BrokerActual - book.WorkingQty;
            if (delta == 0) return;

            string id = $"{symbol}-{++_orderSeq}";
            book.OpenOrders[id] = delta;
            book.WorkingQty += delta;
            OrderRequested?.Invoke(new OrderIntent(id, symbol, delta));
        }

        /// <summary>
        /// Match strategies that want opposite directions against each other.
        /// Net-neutral, so it runs before the risk clamp and never needs approval.
        /// This is where the commission saving lives: bot A's exit fills bot B's
        /// entry and the broker never sees either.
        /// </summary>
        private void CrossInternally(string symbol, Book book)
        {
            if (!book.HasPrice) return;   // no honest reference price yet

            int buyNeed = 0, sellNeed = 0;
            foreach (var kv in book.Legs)
            {
                if (kv.Key == HouseLeg) continue;
                int pending = kv.Value.Desired - kv.Value.Allocated;
                if (pending > 0) buyNeed += pending;
                else if (pending < 0) sellNeed += -pending;
            }

            int crossQty = Math.Min(buyNeed, sellNeed);
            if (crossQty == 0) return;

            DistributePending(symbol, book, crossQty, +1, book.LastPrice, FillSource.InternalCross);
            DistributePending(symbol, book, crossQty, -1, book.LastPrice, FillSource.InternalCross);
            _log($"[netting] {symbol} crossed {crossQty} internally @ {book.LastPrice} (no broker order)");
        }

        private void AllocateToStrategies(string symbol, Book book, int signedQty, decimal price, long timeMs, FillSource source)
        {
            int sign = Math.Sign(signedQty);
            int allocated = DistributePending(symbol, book, Math.Abs(signedQty), sign, price, source, timeMs);

            int leftover = Math.Abs(signedQty) - allocated;
            if (leftover > 0)
            {
                // Nobody was waiting for this. Park it on the house leg to protect
                // the invariant; a strategy would otherwise inherit a position it
                // never asked for and stop out on it.
                var house = GetLeg(book, HouseLeg);
                ApplyToLeg(house, leftover * sign, price);
                _log($"[netting] {symbol} {leftover * sign:+#;-#;0} unallocated -> house leg");
            }
        }

        /// <summary>
        /// Hand out <paramref name="qty"/> contracts to legs whose pending demand
        /// matches <paramref name="sign"/>, pro-rata by pending size using the
        /// largest-remainder method so integer contracts always sum exactly.
        /// Returns the quantity actually placed.
        /// </summary>
        private int DistributePending(string symbol, Book book, int qty, int sign, decimal price,
                                      FillSource source, long timeMs = 0)
        {
            if (qty <= 0) return 0;

            // Small N (one entry per strategy). Lists here are allocation-cheap and
            // keep the arithmetic readable; this is not the per-tick path.
            var ids = new List<string>(book.Legs.Count);
            var want = new List<int>(book.Legs.Count);
            int totalWant = 0;

            foreach (var kv in book.Legs)
            {
                if (kv.Key == HouseLeg) continue;
                int pending = kv.Value.Desired - kv.Value.Allocated;
                if (Math.Sign(pending) != sign) continue;
                int w = Math.Abs(pending);
                ids.Add(kv.Key); want.Add(w); totalWant += w;
            }
            if (totalWant == 0) return 0;

            int share = Math.Min(qty, totalWant);
            var give = new int[ids.Count];
            var rem = new long[ids.Count];
            int placed = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                long num = (long)share * want[i];
                give[i] = (int)(num / totalWant);
                rem[i] = num % totalWant;
                placed += give[i];
            }

            // Largest remainder: hand the rounding leftovers to the legs that were
            // shorted most by integer division.
            int leftover = share - placed;
            while (leftover > 0)
            {
                int best = -1; long bestRem = -1;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (give[i] >= want[i]) continue;       // never over-fill a leg
                    if (rem[i] > bestRem) { bestRem = rem[i]; best = i; }
                }
                if (best < 0) break;
                give[best]++; rem[best] = -1; leftover--;
            }

            int total = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (give[i] == 0) continue;
                int signed = give[i] * sign;
                var leg = book.Legs[ids[i]];
                ApplyToLeg(leg, signed, price);
                total += give[i];
                StrategyFilled?.Invoke(new StrategyFill(ids[i], symbol, signed, price, timeMs, source));
            }
            return total;
        }

        /// <summary>
        /// Weighted average that survives a flip through zero: adding to a position
        /// blends, reducing keeps the entry, reversing resets to the new price.
        /// Getting this wrong quietly poisons every P&L number downstream.
        /// </summary>
        private static void ApplyToLeg(Leg leg, int signedQty, decimal price)
        {
            int oldPos = leg.Allocated;
            int newPos = oldPos + signedQty;

            if (oldPos == 0 || Math.Sign(signedQty) == Math.Sign(oldPos))
            {
                int denom = Math.Abs(oldPos) + Math.Abs(signedQty);
                leg.AvgPrice = denom == 0 ? 0m
                    : (leg.AvgPrice * Math.Abs(oldPos) + price * Math.Abs(signedQty)) / denom;
            }
            else if (Math.Sign(newPos) != Math.Sign(oldPos) && newPos != 0)
            {
                leg.AvgPrice = price;   // flipped through flat
            }
            else if (newPos == 0)
            {
                leg.AvgPrice = 0m;
            }
            // reducing without flipping: average entry is unchanged

            leg.Allocated = newPos;
        }

        // ----------------------------------------------------------- book helpers

        private Book GetBook(string symbol)
        {
            if (!_books.TryGetValue(symbol, out var b))
            {
                b = new Book();
                _books[symbol] = b;
            }
            return b;
        }

        private static Leg GetLeg(Book book, string strategyId)
        {
            if (!book.Legs.TryGetValue(strategyId, out var leg))
            {
                leg = new Leg();
                book.Legs[strategyId] = leg;
            }
            return leg;
        }

        private bool TryLeg(string symbol, string strategyId, out Leg leg)
        {
            if (_books.TryGetValue(symbol, out var b) && b.Legs.TryGetValue(strategyId, out var found))
            {
                leg = found;
                return true;
            }
            leg = null!;
            return false;
        }
    }
}
