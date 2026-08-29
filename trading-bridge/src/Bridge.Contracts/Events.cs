using System;

namespace Bridge.Contracts
{
    public enum Side { Buy = 1, Sell = -1 }

    /// <summary>Where a fill came from. Internal crosses never touch the broker.</summary>
    public enum FillSource { Broker = 0, InternalCross = 1 }

    /// <summary>
    /// Structs, not classes: bars and ticks are the hot path and these are copied
    /// into per-strategy callbacks. Keeping them on the stack avoids a GC churn
    /// problem that only shows up on a fast tape, which is exactly when it hurts.
    /// </summary>
    public readonly struct Bar
    {
        public readonly string Symbol;
        public readonly long TimeMs;      // unix ms, UTC
        public readonly decimal Open, High, Low, Close;
        public readonly long Volume;
        public readonly long Delta;       // buy volume - sell volume, order-flow native

        public Bar(string symbol, long timeMs, decimal o, decimal h, decimal l, decimal c, long volume, long delta)
        {
            Symbol = symbol; TimeMs = timeMs;
            Open = o; High = h; Low = l; Close = c;
            Volume = volume; Delta = delta;
        }
    }

    public readonly struct Tick
    {
        public readonly string Symbol;
        public readonly long TimeMs;
        public readonly decimal Price;
        public readonly long Size;
        public readonly Side AggressorSide;

        public Tick(string symbol, long timeMs, decimal price, long size, Side aggressor)
        {
            Symbol = symbol; TimeMs = timeMs; Price = price; Size = size; AggressorSide = aggressor;
        }
    }

    /// <summary>Broker's authoritative net position. Drives reconciliation on reconnect.</summary>
    public readonly struct PositionReport
    {
        public readonly string Symbol;
        public readonly int NetQty;         // signed
        public readonly decimal AvgPrice;

        public PositionReport(string symbol, int netQty, decimal avgPrice)
        {
            Symbol = symbol; NetQty = netQty; AvgPrice = avgPrice;
        }
    }

    /// <summary>An order the netting engine wants sent to the broker.</summary>
    public readonly struct OrderIntent
    {
        public readonly string ClientOrderId;
        public readonly string Symbol;
        public readonly int SignedQty;      // +buy / -sell. Never zero.
        public readonly decimal LimitPrice; // 0 => market

        public OrderIntent(string clientOrderId, string symbol, int signedQty, decimal limitPrice = 0m)
        {
            ClientOrderId = clientOrderId; Symbol = symbol; SignedQty = signedQty; LimitPrice = limitPrice;
        }

        public bool IsMarket => LimitPrice == 0m;
    }

    /// <summary>A broker fill against an OrderIntent.</summary>
    public readonly struct Fill
    {
        public readonly string ClientOrderId;
        public readonly string Symbol;
        public readonly int SignedQty;
        public readonly decimal Price;
        public readonly long TimeMs;

        public Fill(string clientOrderId, string symbol, int signedQty, decimal price, long timeMs)
        {
            ClientOrderId = clientOrderId; Symbol = symbol; SignedQty = signedQty; Price = price; TimeMs = timeMs;
        }
    }

    /// <summary>
    /// What a strategy actually receives back: its own share of a fill, at its own
    /// price. Attribution is per-strategy so each bot's P&L is real even though
    /// they share one broker account.
    /// </summary>
    public readonly struct StrategyFill
    {
        public readonly string StrategyId;
        public readonly string Symbol;
        public readonly int SignedQty;
        public readonly decimal Price;
        public readonly long TimeMs;
        public readonly FillSource Source;

        public StrategyFill(string strategyId, string symbol, int signedQty, decimal price, long timeMs, FillSource source)
        {
            StrategyId = strategyId; Symbol = symbol; SignedQty = signedQty;
            Price = price; TimeMs = timeMs; Source = source;
        }
    }

    /// <summary>A published trade call. Same object feeds Discord and the journal.</summary>
    public sealed class Signal
    {
        public string StrategyId = "";
        public string Symbol = "";
        public int TargetQty;
        public decimal ReferencePrice;
        public long TimeMs;
        public string Note = "";
    }
}
