using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Bridge.Contracts;

namespace Bridge.Wire
{
    /// <summary>
    /// Host-side end of the bridge. Listens on loopback; the ATAS plugin dials in.
    ///
    /// The host listens (rather than connects) so ATAS can be restarted freely
    /// without taking the strategy host down with it -- which is the entire point
    /// of splitting them. Reconnect is expected and cheap.
    /// </summary>
    public sealed class TcpLineLink : IMarketLink, IDisposable
    {
        private readonly int _port;
        private readonly Action<string> _log;
        private readonly BlockingCollection<string> _outbound = new BlockingCollection<string>(1024);
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private volatile TcpClient? _client;

        public event Action<Bar>? BarReceived;
        public event Action<Tick>? TickReceived;
        public event Action<PositionReport>? PositionReceived;
        public event Action<Fill>? FillReceived;
        public event Action<string, string>? OrderTerminated;   // orderId, symbol
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _client?.Connected == true;

        public TcpLineLink(int port = 5599, Action<string>? log = null)
        {
            _port = port;
            _log = log ?? Console.WriteLine;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "bridge-accept" };
            _acceptThread.Start();
            _log($"[link] listening on 127.0.0.1:{_port}");
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _client?.Close(); } catch { }
        }

        public void Send(in OrderIntent intent)
        {
            // Copy out of the `in` parameter before it crosses a lambda/collection
            // boundary -- readonly structs cannot be captured by reference.
            string line = WireCodec.Encode(intent);
            if (!_outbound.TryAdd(line))
                _log($"[link] OUTBOUND QUEUE FULL, dropped order {intent.ClientOrderId} -- broker link is stalled");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    client.NoDelay = true;   // orders are latency-sensitive and tiny
                    _client = client;
                    ConnectionChanged?.Invoke(true);
                    _log("[link] ATAS connected");

                    var writer = new Thread(() => WriteLoop(client)) { IsBackground = true, Name = "bridge-write" };
                    writer.Start();
                    ReadLoop(client);

                    ConnectionChanged?.Invoke(false);
                    _log("[link] ATAS disconnected");
                }
                catch (SocketException) when (!_running) { return; }
                catch (Exception ex) { _log($"[link] accept error: {ex.Message}"); Thread.Sleep(500); }
            }
        }

        private void ReadLoop(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192);
                string? line;
                while (_running && (line = reader.ReadLine()) != null)
                    Dispatch(line);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _log($"[link] read error: {ex.Message}"); }
            finally { _client = null; try { client.Close(); } catch { } }
        }

        private void WriteLoop(TcpClient client)
        {
            try
            {
                using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { AutoFlush = true };
                foreach (var line in _outbound.GetConsumingEnumerable())
                {
                    if (!client.Connected) return;
                    writer.WriteLine(line);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _log($"[link] write error: {ex.Message}"); }
        }

        private void Dispatch(string line)
        {
            var kind = WireCodec.Decode(line, out var bar, out var tick, out var pos,
                                        out var fill, out _, out var orderId, out var symbol);
            switch (kind)
            {
                case WireCodec.Kind.Bar: BarReceived?.Invoke(bar); break;
                case WireCodec.Kind.Tick: TickReceived?.Invoke(tick); break;
                case WireCodec.Kind.Position: PositionReceived?.Invoke(pos); break;
                case WireCodec.Kind.Fill: FillReceived?.Invoke(fill); break;
                case WireCodec.Kind.Terminated: OrderTerminated?.Invoke(orderId, symbol); break;
                case WireCodec.Kind.Heartbeat: break;
                default: _log($"[link] unparsed frame: {line}"); break;
            }
        }

        public void Dispose() { Stop(); _outbound.Dispose(); }
    }
}
