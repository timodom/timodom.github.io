using System;
using System.Collections.Generic;
using Edge.Atas.Core;

int fails = 0;
void Check(string name, bool ok, string detail) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}"); if (!ok) fails++; }

// Range 09:30-09:45: high 7670, low 7660 (40t). Bar 3 closes below -> sell limit 7659.75.
// Short entry 7659.75, stop 7670 (41t), target 1.5R = 61.5t -> 7644.50 (rounded). 80% trigger ~ 7647.45.
(string log, decimal net, List<decimal> stops, string reason) Run(RangeBreakoutSettings s, (decimal o, decimal h, decimal l, decimal c)[] after)
{
    var e = new RangeBreakoutEngine(s);
    var stops = new List<decimal>(); string reason = "-"; var log = "";
    e.StopMoved += (b, p) => stops.Add(p);
    e.Exited += (b, p, pnl, r) => { reason = r; log += $"exit {r} @{p} pnl {pnl}; "; };
    e.Entered += (b, d, p, st, tg) => log += $"enter {d} @{p} stop {st} tgt {tg}; ";
    var t0 = new DateTime(2026, 10, 23, 9, 30, 0); int i = 0;
    void Bar(decimal o, decimal h, decimal l, decimal c) { e.Process(new RangeBar(i, t0.AddMinutes(5 * i), o, h, l, c, 1000, 0)); i++; }
    Bar(7665, 7670, 7662, 7664); Bar(7664, 7668, 7660, 7662); Bar(7662, 7666, 7661, 7663);   // range
    Bar(7663, 7663, 7656, 7657);                                                            // 09:45 close below -> pending
    Bar(7657, 7660, 7655, 7656);                                                            // retest fills 7659.75
    foreach (var b in after) Bar(b.o, b.h, b.l, b.c);
    return (log, e.Net, stops, reason);
}

var on = new RangeBreakoutSettings();
var off = new RangeBreakoutSettings { ProtectAtPct = 0 };

// 1. Runs to 8 points-ish (low 7647.25, past 80%), pulls back -> trail stop = 7647.25 + 6t = 7648.75, next bar hits it.
var a = Run(on, new[] { (7656m, 7657m, 7647m, 7648m), (7648m, 7652m, 7647.75m, 7651m) });
Check("trail after 80% tap, pullback exits at best+6t", a.reason == "Protect" || a.reason == "Stop", a.log + $" stops[{string.Join(",", a.stops)}]");
Check("exit price is best+6t = 7648.50", a.log.Contains("Stop @7648.50"), "");

// 2. Same path with protect off -> no stop moves, trade stays open (no exit).
var b2 = Run(off, new[] { (7656m, 7657m, 7647.25m, 7649m), (7649m, 7652m, 7648m, 7651m) });
Check("protect off: unchanged behavior, no stop move", b2.stops.Count == 0 && !b2.log.Contains("exit"), b2.log);

// 3. Never reaches 80% (low 7650) -> protect stays off.
var c = Run(on, new[] { (7656m, 7657m, 7650m, 7652m), (7652m, 7655m, 7651m, 7654m) });
Check("below 80%: no protect move", c.stops.Count == 0, c.log);

// 4. Stop only ratchets: new best 7646 -> stop 7647.50, then weaker bar must not move it back.
var d = Run(on, new[] { (7656m, 7657m, 7647m, 7647.5m), (7647.5m, 7648m, 7646m, 7646.5m), (7646.5m, 7647.25m, 7646.25m, 7647m) });
Check("ratchets down only", d.stops.Count == 2 && d.stops[0] == 7648.50m && d.stops[1] == 7647.50m, $"stops[{string.Join(",", d.stops)}] {d.log}");

// 5. Tap bar closes back past the new stop -> exits that bar at the stop.
var f = Run(on, new[] { (7656m, 7657m, 7647m, 7650m) });
Check("same-bar close back through stop exits at stop", f.reason == "Protect" && f.log.Contains("@7648.50"), f.log);

// 6. Target still wins when hit.
var g = Run(on, new[] { (7656m, 7657m, 7644m, 7645m) });
Check("target still taken", g.reason == "Target", g.log);

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
