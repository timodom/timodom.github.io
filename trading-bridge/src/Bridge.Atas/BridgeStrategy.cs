// ---------------------------------------------------------------------------
// THE ONLY FILE IN THIS SOLUTION THAT KNOWS ATAS EXISTS.
//
// It is deliberately dumb: publish market data out, accept orders in, report
// fills and position back. No trading logic lives here and none ever should.
// Every line of strategy logic belongs in a plain class in Bridge.Host, where
// it can be unit tested and replayed.
//
// That is the whole architectural bet: when ATAS ships a breaking update, you
// fix THIS file, once -- not five bots.
//
// !! VERIFY BEFORE MONDAY !!
// The ATAS plugin API differs between versions. Lines marked [ATAS-API] are the
// ones to check against the version you have installed (Help > About). The
// shape of this file is correct; the exact member names may need a nudge.
// Reference assemblies live in your ATAS install directory, typically:
//   C:\Program Files (x86)\ATAS Platform\ATAS.Indicators.dll
//   C:\Program Files (x86)\ATAS Platform\ATAS.Strategies.dll
//   C:\Program Files (x86)\ATAS Platform\OFT.Attributes.dll
// ---------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ATAS.Indicators;              // [ATAS-API]
using ATAS.Strategies.Chart;        // [ATAS-API]
using OFT.Attributes;               // [ATAS-API]

namespace Bridge.Atas
{
    [DisplayName("Bridge Link")]
    public class BridgeStrategy : ChartStrategy   // [ATAS-API] base type
    {
        private TcpClient? _client;
        private StreamWriter? _writer;
        private Thread? _reader;
        private volatile bool _running;
        private readonly ConcurrentQueue<string> _inbound = new ConcurrentQueue<string>();
        private int _lastPublishedBar = -1;
        private int _lastReportedPosition = int.MinValue;

        [Display(Name = "Host port", GroupName = "Bridge")]
        public int Port { get; set; } = 5599;

        [Display(Name = "Symbol override", GroupName = "Bridge")]
        public string SymbolOverride { get; set; } = "";

        private string Symbol =>
            string.IsNullOrWhiteSpace(SymbolOverride) ? (Security?.Code ?? "UNKNOWN") : SymbolOverride;  // [ATAS-API]

        // ------------------------------------------------------------ lifecycle

        protected override void OnStarted()      // [ATAS-API]
        {
            _running = true;
            Connect();
            base.OnStarted();
        }

        protected override void OnStopped()      // [ATAS-API]
        {
            _running = false;
            try { _client?.Close(); } catch { }
            base.OnStopped();
        }

        private void Connect()
        {
            try
            {
                _client = new TcpClient();
                _client.Connect("127.0.0.1", Port);
                _client.NoDelay = true;
                _writer = new StreamWriter(_client.GetStream(), Encoding.ASCII) { AutoFlush = true };

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "bridge-reader" };
                _reader.Start();
                this.LogInfo($"[bridge] connected to strategy host on port {Port}");   // [ATAS-API]
            }
            catch (Exception ex)
            {
                // Host not up yet is the normal case on a cold start, so this is a
                // retryable condition, not a fatal one. OnCalculate retries.
                this.LogWarn($"[bridge] connect failed: {ex.Message}");                // [ATAS-API]
                _client = null; _writer = null;
            }
        }

        // ------------------------------------------------------------ data out

        protected override void OnCalculate(int bar, decimal value)   // [ATAS-API]
        {
            if (!_running) return;
            if (_writer == null) { Connect(); if (_writer == null) return; }

            DrainInbound();

            // Publish a bar only once it has closed. Publishing the forming bar
            // would let strategies act on a candle that can still change, which is
            // the live-trading equivalent of lookahead bias.
            int closed = bar - 1;
            if (closed > _lastPublishedBar && closed >= 0)
            {
                for (int i = _lastPublishedBar + 1; i <= closed; i++) PublishBar(i);
                _lastPublishedBar = closed;
            }

            PublishPositionIfChanged();
        }

        private void PublishBar(int index)
        {
            var c = GetCandle(index);   // [ATAS-API]
            long timeMs = new DateTimeOffset(c.Time.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
            long delta = (long)(c.Delta);              // [ATAS-API] order-flow delta
            long volume = (long)(c.Volume);            // [ATAS-API]

            Send($"B|{Symbol}|{timeMs}|{c.Open}|{c.High}|{c.Low}|{c.Close}|{volume}|{delta}");
        }

        private void PublishPositionIfChanged()
        {
            // [ATAS-API] CurrentPosition / AveragePrice member names vary by version.
            int net = (int)CurrentPosition;
            if (net == _lastReportedPosition) return;
            _lastReportedPosition = net;
            Send($"P|{Symbol}|{net}|{AveragePrice}");
        }

        // ---------------------------------------------------------- orders in

        private void ReadLoop()
        {
            try
            {
                using var reader = new StreamReader(_client!.GetStream(), Encoding.ASCII);
                string? line;
                while (_running && (line = reader.ReadLine()) != null)
                    _inbound.Enqueue(line);
            }
            catch (IOException) { }
            catch (Exception ex) { this.LogError($"[bridge] read loop: {ex.Message}"); }   // [ATAS-API]
        }

        /// <summary>
        /// Orders are submitted from OnCalculate, never from the socket thread.
        /// ATAS expects order submission on its own calculation thread; doing it
        /// from a background thread is the kind of bug that works for a week and
        /// then loses money on a fast tape.
        /// </summary>
        private void DrainInbound()
        {
            while (_inbound.TryDequeue(out var line))
            {
                var f = line.Split('|');
                if (f.Length < 5 || f[0] != "O") continue;

                string clientId = f[1];
                if (!int.TryParse(f[3], out int signedQty) || signedQty == 0) continue;
                decimal.TryParse(f[4], System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out decimal limitPx);

                SubmitOrder(clientId, signedQty, limitPx);
            }
        }

        private void SubmitOrder(string clientId, int signedQty, decimal limitPx)
        {
            try
            {
                var order = new Order                       // [ATAS-API]
                {
                    Portfolio = Portfolio,
                    Security = Security,
                    Direction = signedQty > 0 ? OrderDirections.Buy : OrderDirections.Sell,
                    Type = limitPx == 0m ? OrderTypes.Market : OrderTypes.Limit,
                    QuantityToFill = Math.Abs(signedQty),
                    Price = limitPx,
                    Comment = clientId          // carries our id through to the fill
                };
                OpenOrder(order);               // [ATAS-API]
            }
            catch (Exception ex)
            {
                this.LogError($"[bridge] order {clientId} rejected: {ex.Message}");
                // Tell the host immediately. Without this the netting engine holds
                // working quantity for an order that will never fill and stops
                // trading that symbol entirely.
                Send($"X|{clientId}|{Symbol}");
            }
        }

        // ------------------------------------------------------------ fills out

        protected override void OnOrderChanged(Order order)   // [ATAS-API]
        {
            if (order == null) return;
            string clientId = order.Comment ?? "";

            if (order.Status() == OrderStatus.Filled)         // [ATAS-API]
            {
                int signed = order.Direction == OrderDirections.Buy
                    ? (int)order.QuantityToFill : -(int)order.QuantityToFill;
                long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Send($"F|{clientId}|{Symbol}|{signed}|{order.Price}|{t}");
            }
            else if (order.Status() == OrderStatus.Canceled)  // [ATAS-API]
            {
                Send($"X|{clientId}|{Symbol}");
            }
            base.OnOrderChanged(order);
        }

        // ---------------------------------------------------------------- wire

        private void Send(string line)
        {
            try { _writer?.WriteLine(line); }
            catch (Exception)
            {
                // Socket died. Null it out so OnCalculate reconnects rather than
                // throwing once per tick for the rest of the session.
                _writer = null;
                try { _client?.Close(); } catch { }
                _client = null;
            }
        }
    }
}
