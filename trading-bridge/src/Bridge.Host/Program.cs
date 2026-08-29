using System;
using System.Collections.Generic;
using Bridge.Contracts;
using Bridge.Netting;
using Bridge.Replay;
using Bridge.Samples;
using Bridge.Wire;

namespace Bridge.Host
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "replay";
            return mode switch
            {
                "replay" => RunReplay(args),
                "live"   => RunLive(args),
                _        => Usage()
            };
        }

        private static int Usage()
        {
            Console.WriteLine("usage:");
            Console.WriteLine("  dotnet run --project src/Bridge.Host -- replay <csv> [symbol]");
            Console.WriteLine("  dotnet run --project src/Bridge.Host -- live [port]");
            return 1;
        }

        private static List<IStrategy> BuildStrategies(string symbol) => new List<IStrategy>
        {
            // Five bots, one process. This list is the thing that used to be five
            // machines.
            new DeltaMomentumBot("delta-fast", symbol, deltaThreshold: 300, size: 2),
            new DeltaMomentumBot("delta-slow", symbol, deltaThreshold: 900, size: 1),
            new RangeFadeBot("range-fade", symbol, size: 1),
        };

        private static int RunReplay(string[] args)
        {
            if (args.Length < 2) return Usage();
            string csv = args[1];
            string symbol = args.Length > 2 ? args[2] : "ES";

            var risk = new RiskGate(defaultMaxNet: 5);
            var engine = new NettingEngine(risk, Console.WriteLine);
            var link = new ReplayLink(symbol, csv, slippagePerContract: 0.25m);
            using var signals = new ConsoleSignalSink("signals.csv");

            int brokerOrders = 0, crossedContracts = 0;
            engine.OrderRequested += _ => brokerOrders++;
            engine.StrategyFilled += f => { if (f.Source == FillSource.InternalCross) crossedContracts += Math.Abs(f.SignedQty); };

            using var host = new StrategyHost(link, engine, signals, synchronous: true);
            foreach (var s in BuildStrategies(symbol)) host.Add(s);
            host.Start();   // synchronous: returns when the CSV is exhausted
            host.Stop();

            Console.WriteLine();
            Console.WriteLine($"bars replayed     : {link.BarsReplayed}");
            Console.WriteLine($"broker orders     : {brokerOrders}");
            Console.WriteLine($"contracts crossed : {crossedContracts}  <- these never hit the broker");
            Console.WriteLine($"final net position: {engine.GetBrokerPosition(symbol)}");
            return 0;
        }

        private static int RunLive(string[] args)
        {
            int port = args.Length > 1 ? int.Parse(args[1]) : 5599;
            string symbol = args.Length > 2 ? args[2] : "ES";

            var risk = new RiskGate(defaultMaxNet: 5);
            var engine = new NettingEngine(risk, Console.WriteLine);
            var link = new TcpLineLink(port);
            using var signals = new ConsoleSignalSink("signals.csv");
            using var host = new StrategyHost(link, engine, signals);

            link.OrderTerminated += (id, sym) => host.OnOrderTerminated(id, sym);
            link.ConnectionChanged += connected =>
            {
                if (!connected)
                {
                    // ATAS went away. Do not auto-flatten: a dropped socket is not a
                    // reason to market out of a good position. Stop taking NEW risk
                    // and make it loud.
                    risk.Halt("ATAS link lost");
                    Console.WriteLine("!! link lost -- new orders halted, existing position untouched");
                }
                else
                {
                    risk.Resume();
                    Console.WriteLine("link restored -- trading resumed");
                }
            };

            foreach (var s in BuildStrategies(symbol)) host.Add(s);
            host.Start();

            Console.WriteLine("running. press enter to stop.");
            Console.ReadLine();
            host.Stop();
            return 0;
        }
    }
}
