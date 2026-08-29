using System;
using System.Collections.Generic;
using Bridge.Contracts;
using Bridge.Netting;
using Xunit;

namespace Bridge.Tests
{
    public class NettingEngineTests
    {
        private readonly List<OrderIntent> _orders = new();
        private readonly List<StrategyFill> _fills = new();
        private readonly NettingEngine _e;

        public NettingEngineTests()
        {
            _e = new NettingEngine(new RiskGate(defaultMaxNet: 10));
            _e.OrderRequested += o => _orders.Add(o);
            _e.StrategyFilled += f => _fills.Add(f);
            _e.OnPrice("ES", 5000m);
        }

        private void FillLast(decimal price = 5000m)
        {
            var o = _orders[^1];
            _e.OnFill(new Fill(o.ClientOrderId, o.Symbol, o.SignedQty, price, 0));
        }

        /// <summary>The invariant everything else depends on.</summary>
        private void AssertInvariant(string symbol, params string[] strategies)
        {
            int sum = 0;
            foreach (var s in strategies) sum += _e.GetAllocated(s, symbol);
            sum += _e.GetAllocated(NettingEngine.HouseLeg, symbol);
            Assert.Equal(_e.GetBrokerPosition(symbol), sum);
        }

        [Fact]
        public void SingleStrategy_SendsDeltaAndAllocatesFill()
        {
            _e.SetTarget("A", "ES", 2);

            Assert.Single(_orders);
            Assert.Equal(2, _orders[0].SignedQty);

            FillLast(5001m);
            Assert.Equal(2, _e.GetAllocated("A", "ES"));
            Assert.Equal(5001m, _e.GetAvgPrice("A", "ES"));
            AssertInvariant("ES", "A");
        }

        [Fact]
        public void SetTarget_IsIdempotent_NoDuplicateOrders()
        {
            _e.SetTarget("A", "ES", 2);
            FillLast();
            _e.SetTarget("A", "ES", 2);
            _e.SetTarget("A", "ES", 2);

            Assert.Single(_orders);   // the whole point of target semantics
        }

        [Fact]
        public void OpposingStrategies_NetToSmallerBrokerOrder()
        {
            _e.BeginBatch();
            _e.SetTarget("A", "ES", 3);
            _e.SetTarget("B", "ES", -1);
            _e.EndBatch();

            // A alone would send 3; B's -1 must reduce the broker order, not add one.
            int net = 0;
            foreach (var o in _orders) net += o.SignedQty;
            Assert.Equal(2, net);
        }

        [Fact]
        public void InternalCross_TransfersPositionWithNoBrokerOrder()
        {
            _e.SetTarget("A", "ES", 2);
            FillLast(5000m);
            int ordersBefore = _orders.Count;

            // A exits, B enters the same size on the SAME bar: the position should
            // change hands internally and the broker should see nothing at all.
            _e.BeginBatch();
            _e.SetTarget("A", "ES", 0);
            _e.SetTarget("B", "ES", 2);
            _e.EndBatch();

            Assert.Equal(ordersBefore, _orders.Count);
            Assert.Equal(0, _e.GetAllocated("A", "ES"));
            Assert.Equal(2, _e.GetAllocated("B", "ES"));
            Assert.Equal(2, _e.GetBrokerPosition("ES"));
            Assert.Contains(_fills, f => f.Source == FillSource.InternalCross);
            AssertInvariant("ES", "A", "B");
        }

        [Fact]
        public void PartialFill_AllocatesProRataAndSumsExactly()
        {
            _e.BeginBatch();
            _e.SetTarget("A", "ES", 2);
            _e.SetTarget("B", "ES", 4);
            _e.EndBatch();

            var o = _orders[^1];
            // 3 of 6 fill: integer split must not lose or invent a contract.
            _e.OnFill(new Fill(o.ClientOrderId, "ES", 3, 5000m, 0));

            int a = _e.GetAllocated("A", "ES"), b = _e.GetAllocated("B", "ES");
            Assert.Equal(3, a + b);
            Assert.True(a >= 1 && b >= 1, $"expected pro-rata split, got A={a} B={b}");
            AssertInvariant("ES", "A", "B");
        }

        [Fact]
        public void RejectedOrder_ReleasesWorkingQty_AndSystemRecovers()
        {
            _e.SetTarget("A", "ES", 2);
            var o = _orders[0];

            _e.OnOrderTerminated("ES", o.ClientOrderId);

            // Must retry rather than sit deadlocked forever, and the retry must be
            // for the same size -- not doubled by stale working quantity.
            Assert.Equal(2, _orders.Count);
            Assert.Equal(2, _orders[^1].SignedQty);
            Assert.Equal(2, _e.GetWorkingQty("ES"));
        }

        [Fact]
        public void RiskGate_ClampsAggregateNotStrategy()
        {
            var e = new NettingEngine(new RiskGate(defaultMaxNet: 3));
            var seen = new List<OrderIntent>();
            e.OrderRequested += seen.Add;
            e.OnPrice("ES", 5000m);

            e.SetTarget("A", "ES", 5);
            Assert.Equal(3, seen[^1].SignedQty);   // clamped to account limit
        }

        [Fact]
        public void Halt_StopsNewOrders()
        {
            var risk = new RiskGate(defaultMaxNet: 10);
            var e = new NettingEngine(risk);
            var seen = new List<OrderIntent>();
            e.OrderRequested += seen.Add;

            risk.Halt("test");
            e.SetTarget("A", "ES", 2);
            Assert.Empty(seen);
        }

        [Fact]
        public void ManualTrade_LandsOnHouseLeg_NotOnAStrategy()
        {
            _e.SetTarget("A", "ES", 2);
            FillLast();

            // Operator clicks a manual buy of 1 in the platform.
            _e.OnBrokerPosition(new PositionReport("ES", 3, 5000m));

            Assert.Equal(2, _e.GetAllocated("A", "ES"));   // A is untouched
            Assert.Equal(1, _e.GetAllocated(NettingEngine.HouseLeg, "ES"));
            AssertInvariant("ES", "A");
        }

        [Fact]
        public void FlipThroughZero_ResetsAveragePrice()
        {
            _e.SetTarget("A", "ES", 2);
            FillLast(5000m);
            _e.SetTarget("A", "ES", -2);
            FillLast(5010m);

            Assert.Equal(-2, _e.GetAllocated("A", "ES"));
            Assert.Equal(5010m, _e.GetAvgPrice("A", "ES"));
        }

        [Fact]
        public void RepeatedRejects_TripBreaker_AndStopTheOrderStorm()
        {
            _e.SetTarget("A", "ES", 2);

            // A broker rejecting everything must not be met with infinite resends.
            for (int i = 0; i < 10; i++)
            {
                if (_e.IsSuspended("ES")) break;
                _e.OnOrderTerminated("ES", _orders[^1].ClientOrderId);
            }

            Assert.True(_e.IsSuspended("ES"));
            int afterTrip = _orders.Count;
            _e.SetTarget("A", "ES", 4);
            Assert.Equal(afterTrip, _orders.Count);   // stays quiet until a human resumes

            _e.ResumeAfterRejects("ES");
            Assert.True(_orders.Count > afterTrip);
        }

        [Fact]
        public void BatchedCross_CostsNoBrokerOrderAtAll()
        {
            // Three bots rotating a position between themselves on one bar should
            // net to zero broker activity.
            _e.SetTarget("A", "ES", 2);
            FillLast();
            int before = _orders.Count;

            _e.BeginBatch();
            _e.SetTarget("A", "ES", 0);
            _e.SetTarget("B", "ES", 1);
            _e.SetTarget("C", "ES", 1);
            _e.EndBatch();

            Assert.Equal(before, _orders.Count);
            Assert.Equal(0, _e.GetAllocated("A", "ES"));
            Assert.Equal(1, _e.GetAllocated("B", "ES"));
            Assert.Equal(1, _e.GetAllocated("C", "ES"));
            AssertInvariant("ES", "A", "B", "C");
        }

        [Fact]
        public void FiveStrategies_MixedTargets_HoldInvariant()
        {
            var rnd = new Random(42);
            var ids = new[] { "A", "B", "C", "D", "E" };

            for (int i = 0; i < 500; i++)
            {
                _e.BeginBatch();
                foreach (var id in ids)
                    if (rnd.Next(3) == 0) _e.SetTarget(id, "ES", rnd.Next(-3, 4));
                _e.EndBatch();
                _e.OnPrice("ES", 5000m + rnd.Next(-20, 20));
                if (_orders.Count > 0 && rnd.Next(2) == 0)
                {
                    var o = _orders[^1];
                    if (_e.GetWorkingQty("ES") != 0)
                        _e.OnFill(new Fill(o.ClientOrderId, "ES", o.SignedQty, 5000m, i));
                }
                AssertInvariant("ES", ids);
            }
        }
    }
}
