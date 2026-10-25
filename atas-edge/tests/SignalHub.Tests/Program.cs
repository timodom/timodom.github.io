using System;
using System.Collections.Generic;
using Edge.Atas.Hub;

int fails = 0;
void Check(string name, bool ok, string detail) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}"); if (!ok) fails++; }
void Eq(string name, string actual, string expected) => Check(name, actual == expected, actual == expected ? "" : $"\n   got:  {actual}\n   want: {expected}");

// ---------- SignalFormatter ----------
Eq("range set", SignalFormatter.RangeSet("ES", 5812.25m, 5821.75m, 38, new TimeSpan(11, 30, 0)),
	"ES opening range 5812.25–5821.75 (38t). Breakout armed until 11:30.");
Eq("skipped below", SignalFormatter.RangeSkipped("ES", 6, 8, 80), "ES skipped today: range 6t, below the 8t minimum");
Eq("skipped above", SignalFormatter.RangeSkipped("ES", 81, 8, 80), "ES skipped today: range 81t, above the 80t maximum");
Eq("entry long", SignalFormatter.Entry("ES", 1, 5822m, 5812m, 5837m, 1.5m, 40),
	"ES LONG 5822.00 · stop 5812.00 · target 5837.00 (1.5R) · risk 40t");
Eq("entry short", SignalFormatter.Entry("NQ", -1, 21000m, 21010m, 20985m, 1.5m, 40),
	"NQ SHORT 21000.00 · stop 21010.00 · target 20985.00 (1.5R) · risk 40t");
Eq("stop moved", SignalFormatter.StopMoved("ES", 5822m), "ES stop → 5822.00");
Eq("exit target", SignalFormatter.Exit("ES", 1, "Target", 5837m, 60, 1.5m, false), "ES LONG closed at target 5837.00, +60t (+1.5R)");
Eq("exit stop after restart", SignalFormatter.Exit("NQ", -1, "Stop", 21010m, -40, -1m, true),
	"NQ SHORT closed at stop 21010.00, -40t (-1.0R) (entry was before an ATAS restart)");
Eq("exit protect", SignalFormatter.Exit("ES", 1, "Protect", 5834m, 48, 1.2m, false), "ES LONG closed at the protect stop 5834.00, +48t (+1.2R)");
Eq("exit hard close", SignalFormatter.Exit("ES", 1, "Hard close", 5830m, 32, 0.8m, false), "ES LONG closed at the 15:55 hard close 5830.00, +32t (+0.8R)");
Eq("exit session end", SignalFormatter.Exit("ES", 1, "Session ended", 5825m, 12, 0.3m, false), "ES LONG closed at session end 5825.00, +12t (+0.3R)");
Eq("footer", SignalFormatter.WithFooter("x"), "x\n*Model signal, simulated, not a live trade. Hypothetical results; not financial advice.*");
Eq("payload with name", SignalFormatter.Payload("Name", "a \"b\"\nc"), "{\"username\":\"Name\",\"content\":\"a \\\"b\\\"\\nc\"}");
Eq("payload without name", SignalFormatter.Payload("", "x"), "{\"content\":\"x\"}");

// ---- end of checks ----
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
