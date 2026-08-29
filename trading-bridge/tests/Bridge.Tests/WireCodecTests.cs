using Bridge.Contracts;
using Bridge.Wire;
using Xunit;

namespace Bridge.Tests
{
    public class WireCodecTests
    {
        [Fact]
        public void Bar_RoundTrips()
        {
            var src = new Bar("ES", 1717200000000, 5000.25m, 5002.50m, 4999.75m, 5001.00m, 1234, -567);
            var kind = WireCodec.Decode(WireCodec.Encode(src), out var b, out _, out _, out _, out _, out _, out _);

            Assert.Equal(WireCodec.Kind.Bar, kind);
            Assert.Equal(src.Symbol, b.Symbol);
            Assert.Equal(src.TimeMs, b.TimeMs);
            Assert.Equal(src.Open, b.Open);
            Assert.Equal(src.Close, b.Close);
            Assert.Equal(src.Delta, b.Delta);
        }

        [Fact]
        public void Order_RoundTrips_IncludingSign()
        {
            var src = new OrderIntent("ES-42", "ES", -3, 5000.25m);
            var kind = WireCodec.Decode(WireCodec.Encode(src), out _, out _, out _, out _, out var o, out _, out _);

            Assert.Equal(WireCodec.Kind.Order, kind);
            Assert.Equal(-3, o.SignedQty);
            Assert.Equal("ES-42", o.ClientOrderId);
            Assert.False(o.IsMarket);
        }

        [Fact]
        public void Fill_RoundTrips()
        {
            var src = new Fill("ES-7", "ES", 2, 5001.75m, 1717200000000);
            var kind = WireCodec.Decode(WireCodec.Encode(src), out _, out _, out _, out var f, out _, out _, out _);

            Assert.Equal(WireCodec.Kind.Fill, kind);
            Assert.Equal(2, f.SignedQty);
            Assert.Equal(5001.75m, f.Price);
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("B|ES|notanumber|1|2|3|4|5|6")]
        [InlineData("B|ES|1")]                       // truncated
        [InlineData("O|ES-1|ES")]                    // truncated order
        public void MalformedLines_ReturnUnknown_NeverThrow(string line)
        {
            // A corrupt frame on a live socket must not take the session down.
            var kind = WireCodec.Decode(line, out _, out _, out _, out _, out _, out _, out _);
            Assert.Equal(WireCodec.Kind.Unknown, kind);
        }

        [Fact]
        public void DecimalsUseInvariantCulture()
        {
            // A German-locale machine writing "5000,25" would silently corrupt every
            // price on the wire.
            var src = new Bar("ES", 1, 5000.25m, 1m, 1m, 1m, 0, 0);
            Assert.Contains("5000.25", WireCodec.Encode(src));
        }
    }
}
