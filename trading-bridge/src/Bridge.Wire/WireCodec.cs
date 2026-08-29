using System;
using System.Globalization;
using Bridge.Contracts;

namespace Bridge.Wire
{
    /// <summary>
    /// Pipe-delimited line protocol. One message per line, ASCII, no escaping
    /// (symbols and order ids are alphanumeric by construction).
    ///
    /// Why not JSON: this codec is compiled into an ATAS plugin DLL. ATAS loads
    /// plugins into the host process, so any NuGet dependency risks an assembly
    /// version conflict with whatever ATAS itself ships -- a class of failure that
    /// only appears on the live machine, never in the host. Zero dependencies
    /// removes that entirely. The wire is also greppable with netcat, which is
    /// worth more than elegance at 6am on a Monday.
    ///
    ///   B|sym|timeMs|o|h|l|c|vol|delta      bar          ATAS -> host
    ///   T|sym|timeMs|price|size|B|S         tick         ATAS -> host
    ///   P|sym|netQty|avgPrice               position     ATAS -> host
    ///   F|orderId|sym|signedQty|price|tMs   fill         ATAS -> host
    ///   X|orderId|sym                       cancel/reject ATAS -> host
    ///   O|orderId|sym|signedQty|limitPx     order        host -> ATAS
    ///   H|                                  heartbeat    both ways
    /// </summary>
    public static class WireCodec
    {
        private const char Sep = '|';
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Encode(in Bar b) =>
            $"B|{b.Symbol}|{b.TimeMs}|{F(b.Open)}|{F(b.High)}|{F(b.Low)}|{F(b.Close)}|{b.Volume}|{b.Delta}";

        public static string Encode(in Tick t) =>
            $"T|{t.Symbol}|{t.TimeMs}|{F(t.Price)}|{t.Size}|{(t.AggressorSide == Side.Buy ? 'B' : 'S')}";

        public static string Encode(in PositionReport p) =>
            $"P|{p.Symbol}|{p.NetQty}|{F(p.AvgPrice)}";

        public static string Encode(in Fill f) =>
            $"F|{f.ClientOrderId}|{f.Symbol}|{f.SignedQty}|{F(f.Price)}|{f.TimeMs}";

        public static string EncodeTerminated(string orderId, string symbol) => $"X|{orderId}|{symbol}";

        public static string Encode(in OrderIntent o) =>
            $"O|{o.ClientOrderId}|{o.Symbol}|{o.SignedQty}|{F(o.LimitPrice)}";

        public const string Heartbeat = "H|";

        private static string F(decimal d) => d.ToString(Inv);

        public enum Kind { Unknown, Bar, Tick, Position, Fill, Terminated, Order, Heartbeat }

        /// <summary>
        /// Parses a line into whichever payload matches its kind. A malformed line
        /// returns Unknown rather than throwing: one corrupt frame must never take
        /// down a live session.
        /// </summary>
        public static Kind Decode(string line, out Bar bar, out Tick tick, out PositionReport pos,
                                  out Fill fill, out OrderIntent order, out string orderId, out string symbol)
        {
            bar = default; tick = default; pos = default; fill = default; order = default;
            orderId = ""; symbol = "";
            if (string.IsNullOrEmpty(line)) return Kind.Unknown;

            var f = line.Split(Sep);
            try
            {
                switch (f[0])
                {
                    case "B":
                        if (f.Length < 9) return Kind.Unknown;
                        bar = new Bar(f[1], L(f[2]), D(f[3]), D(f[4]), D(f[5]), D(f[6]), L(f[7]), L(f[8]));
                        return Kind.Bar;

                    case "T":
                        if (f.Length < 6) return Kind.Unknown;
                        tick = new Tick(f[1], L(f[2]), D(f[3]), L(f[4]), f[5] == "B" ? Side.Buy : Side.Sell);
                        return Kind.Tick;

                    case "P":
                        if (f.Length < 4) return Kind.Unknown;
                        pos = new PositionReport(f[1], I(f[2]), D(f[3]));
                        return Kind.Position;

                    case "F":
                        if (f.Length < 6) return Kind.Unknown;
                        fill = new Fill(f[1], f[2], I(f[3]), D(f[4]), L(f[5]));
                        return Kind.Fill;

                    case "X":
                        if (f.Length < 3) return Kind.Unknown;
                        orderId = f[1]; symbol = f[2];
                        return Kind.Terminated;

                    case "O":
                        if (f.Length < 5) return Kind.Unknown;
                        order = new OrderIntent(f[1], f[2], I(f[3]), D(f[4]));
                        return Kind.Order;

                    case "H":
                        return Kind.Heartbeat;
                }
            }
            catch (FormatException) { return Kind.Unknown; }
            catch (OverflowException) { return Kind.Unknown; }

            return Kind.Unknown;
        }

        private static long L(string s) => long.Parse(s, Inv);
        private static int I(string s) => int.Parse(s, Inv);
        private static decimal D(string s) => decimal.Parse(s, NumberStyles.Float, Inv);
    }
}
