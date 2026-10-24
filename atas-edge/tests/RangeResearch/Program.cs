using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Edge.Atas.Core;

// Offline research on exported ES 5m bars. Three parts:
//  1. The bot's own engine, re-run on the bars (baseline must match the ATAS dashboard).
//  2. Engine variants: IB length, mid-IB momentum, entry type; plus results split by IB width
//     relative to the average daily range (regime).
//  3. Wide-IB rotation study: fade the first failed probe of IBH/IBL back toward IBM.
// Every table shows the whole sample and a train/test split (first 70% of days / last 30%),
// so a rule that only works in one half is visible.

var inv = CultureInfo.InvariantCulture;
var path = args.Length > 0 ? args[0] : throw new ArgumentException("pass the CSV path");
var chartIsUtc = !args.Contains("--chart-et");
var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
const decimal Tick = 0.25m, TickValue = 12.5m, Commission = 4m;

var bars = new List<Bar>();
foreach (var line in File.ReadLines(path).Skip(1))
{
	var p = line.Split(',');
	var t = DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm:ss", inv);
	var et = chartIsUtc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), eastern) : t;
	bars.Add(new Bar(bars.Count, et, decimal.Parse(p[1], inv), decimal.Parse(p[2], inv), decimal.Parse(p[3], inv),
		decimal.Parse(p[4], inv), decimal.Parse(p[5], inv), decimal.Parse(p[6], inv)));
}

var rth = bars.Where(b => b.Et.TimeOfDay >= new TimeSpan(9, 30, 0) && b.Et.TimeOfDay < new TimeSpan(16, 0, 0)).ToList();
var days = rth.GroupBy(b => b.Et.Date).Where(g => g.Count() >= 60).Select(g => g.Key).OrderBy(d => d).ToList();
var splitDay = days[(int)(days.Count * 0.7)];
Console.WriteLine($"Bars {bars.Count}, {bars[0].Et:yyyy-MM-dd} .. {bars[^1].Et:yyyy-MM-dd} ET, full RTH days {days.Count}, test period from {splitDay:yyyy-MM-dd}");

// Average daily range: mean RTH high-low of the 14 PRIOR days (no look-ahead).
var dayRange = days.ToDictionary(d => d, d => { var g = rth.Where(b => b.Et.Date == d); return g.Max(b => b.High) - g.Min(b => b.Low); });
var adr = new Dictionary<DateTime, decimal>();
for (var i = 14; i < days.Count; i++)
	adr[days[i]] = days.Skip(i - 14).Take(14).Average(d => dayRange[d]);

decimal IbRatio(DateTime day, TimeSpan end)
{
	if (!adr.TryGetValue(day, out var a) || a == 0) return -1;
	var ib = rth.Where(b => b.Et.Date == day && b.Et.TimeOfDay < end).ToList();
	return ib.Count == 0 ? -1 : (ib.Max(b => b.High) - ib.Min(b => b.Low)) / a;
}

// ---------- 1 + 2: the bot's engine ----------

List<Trade> RunEngine(Action<RangeBreakoutSettings> tweak)
{
	var s = new RangeBreakoutSettings();
	tweak?.Invoke(s);
	var e = new RangeBreakoutEngine(s) { TickSize = Tick };
	var trades = new List<Trade>();
	Trade open = null;
	e.Entered += (bar, dir, price, stop, target) => open = new Trade { Day = bars[bar].Et.Date, Dir = dir, Entry = price, Risk = Math.Abs(price - stop) };
	e.Exited += (bar, price, pnl, reason) => { if (open != null) { open.Pnl = pnl; open.Reason = reason; trades.Add(open); open = null; } };
	foreach (var b in bars)
		e.Process(new RangeBar(b.Index, b.Et, b.Open, b.High, b.Low, b.Close, b.Volume, b.Delta));
	return trades;
}

void Row(string name, List<Trade> t)
{
	string Stats(List<Trade> x)
	{
		if (x.Count == 0) return "      0         -       -          -";
		var wins = x.Where(y => y.Pnl > 0).Sum(y => y.Pnl);
		var losses = -x.Where(y => y.Pnl <= 0).Sum(y => y.Pnl);
		decimal peak = 0, cum = 0, dd = 0;
		foreach (var y in x) { cum += y.Pnl; peak = Math.Max(peak, cum); dd = Math.Max(dd, peak - cum); }
		var pf = losses == 0 ? 99 : wins / losses;
		return $"{x.Count,7} {100m * x.Count(y => y.Pnl > 0) / x.Count,6:0}% {pf,7:0.00} {x.Sum(y => y.Pnl),10:N0} {dd,8:N0}";
	}
	Console.WriteLine($"{name,-40}|{Stats(t)} |{Stats(t.Where(y => y.Day < splitDay).ToList())} |{Stats(t.Where(y => y.Day >= splitDay).ToList())}");
}

void Header(string title)
{
	Console.WriteLine();
	Console.WriteLine(title);
	Console.WriteLine($"{"",-40}|{"ALL: trades",11} {"win",6} {"PF",7} {"net $",10} {"maxDD",8} |{"TRAIN",11} {"win",6} {"PF",7} {"net $",10} {"maxDD",8} |{"TEST",11} {"win",6} {"PF",7} {"net $",10} {"maxDD",8}");
}

Header("ENGINE (same code as the bot). Baseline must match the ATAS dashboard on the same data.");
var baseline = RunEngine(null);
Row("Baseline ORB-15 retest (defaults)", baseline);
Row("ORB-15 immediate entry", RunEngine(s => s.Entry = RangeEntry.Immediate));
Row("IB-30 (09:30-10:00) retest", RunEngine(s => { s.RangeEnd = new(10, 0, 0); }));
Row("IB-30 immediate", RunEngine(s => { s.RangeEnd = new(10, 0, 0); s.Entry = RangeEntry.Immediate; }));
Row("IB-60 (09:30-10:30) retest, max 160t", RunEngine(s => { s.RangeEnd = new(10, 30, 0); s.MaxRangeTicks = 160; s.LastEntry = new(12, 30, 0); }));
Row("IB-60 immediate, max 160t", RunEngine(s => { s.RangeEnd = new(10, 30, 0); s.MaxRangeTicks = 160; s.LastEntry = new(12, 30, 0); s.Entry = RangeEntry.Immediate; }));
Row("Mid-IB momentum: ORB-5 immediate", RunEngine(s => { s.RangeEnd = new(9, 35, 0); s.Entry = RangeEntry.Immediate; }));
Row("Mid-IB momentum: ORB-10 immediate", RunEngine(s => { s.RangeEnd = new(9, 40, 0); s.Entry = RangeEntry.Immediate; }));

Header("BREAKOUT BY REGIME: ORB-15 trades bucketed by 15-min IB width / avg daily range (prior 14 days)");
foreach (var (label, entry) in new[] { ("retest", RangeEntry.Retest), ("immediate", RangeEntry.Immediate) })
{
	var tr = RunEngine(s => { s.Entry = entry; s.MaxRangeTicks = 10000; });
	foreach (var (lo, hi) in new[] { (0m, 0.15m), (0.15m, 0.25m), (0.25m, 0.35m), (0.35m, 9m) })
		Row($"{label}, IB/ADR {lo:0.00}-{(hi > 1 ? "up" : hi.ToString("0.00"))}", tr.Where(x => { var r = IbRatio(x.Day, new(9, 45, 0)); return r >= lo && r < hi; }).ToList());
}

// ---------- 3: wide-IB rotation ----------
// After the IB closes: the first bar that trades through IBH (or IBL) is the probe. If a bar
// within ConfirmBars closes back inside the IB, enter at that close, fading toward IBM.
// Stop: probe extreme + 2 ticks. Target: IBM, or a fraction of the way there. Out by 15:55.
// One trade per day, no entries after 13:00. Conservative: a bar touching both stop and target
// counts as the stop.

List<Trade> Rotation(TimeSpan ibEnd, decimal targetFrac, int confirmBars, decimal minRatio, decimal maxRatio)
{
	var result = new List<Trade>();
	foreach (var day in days)
	{
		var ratio = IbRatio(day, ibEnd);
		if (ratio < minRatio || ratio >= maxRatio) continue;
		var g = rth.Where(b => b.Et.Date == day).ToList();
		var ib = g.Where(b => b.Et.TimeOfDay < ibEnd).ToList();
		var post = g.Where(b => b.Et.TimeOfDay >= ibEnd).ToList();
		if (ib.Count == 0 || post.Count == 0) continue;
		decimal hi = ib.Max(b => b.High), lo = ib.Min(b => b.Low), mid = (hi + lo) / 2;

		for (var i = 0; i < post.Count; i++)
		{
			var b = post[i];
			if (b.Et.TimeOfDay >= new TimeSpan(13, 0, 0)) break;
			var dir = b.High > hi ? -1 : b.Low < lo ? 1 : 0;  // probe above -> short fade
			if (dir == 0) continue;
			if (b.High > hi && b.Low < lo) break;             // outside bar: ambiguous, skip day

			var extreme = dir < 0 ? b.High : b.Low;
			int entryIdx = -1;
			for (var j = i; j < Math.Min(post.Count, i + confirmBars); j++)
			{
				extreme = dir < 0 ? Math.Max(extreme, post[j].High) : Math.Min(extreme, post[j].Low);
				if (dir < 0 ? post[j].Close < hi : post[j].Close > lo) { entryIdx = j; break; }
			}
			if (entryIdx < 0) break;                           // accepted outside: no fade today

			var entry = post[entryIdx].Close;
			var stop = extreme - dir * 2 * Tick;
			var target = Math.Round((entry + (mid - entry) * targetFrac) / Tick) * Tick;
			if (dir * (target - entry) <= 0) break;            // already at/through target
			decimal exit = 0; string why = null;
			for (var k = entryIdx + 1; k < post.Count && why == null; k++)
			{
				var c = post[k];
				if (c.Et.TimeOfDay >= new TimeSpan(15, 55, 0)) { exit = c.Open; why = "time"; }
				else if (dir < 0 ? c.High >= stop : c.Low <= stop) { exit = stop; why = "stop"; }
				else if (dir < 0 ? c.Low <= target : c.High >= target) { exit = target; why = "target"; }
			}
			if (why == null) { exit = post[^1].Close; why = "eod"; }
			var ticks = dir * (exit - entry) / Tick;
			result.Add(new Trade { Day = day, Dir = dir, Entry = entry, Risk = Math.Abs(entry - stop), Pnl = ticks * TickValue - Commission, Reason = why });
			break;
		}
	}
	return result;
}

Header("ROTATION (fade first failed probe of IBH/IBL toward IBM), 1 ES, $4 commission");
foreach (var (ibName, ibEnd) in new[] { ("IB-15", new TimeSpan(9, 45, 0)), ("IB-30", new TimeSpan(10, 0, 0)), ("IB-60", new TimeSpan(10, 30, 0)) })
{
	foreach (var frac in new[] { 1.0m, 0.5m })
	{
		Row($"{ibName} all days, target {(frac == 1 ? "IBM" : "half to IBM")}", Rotation(ibEnd, frac, 2, 0, 9));
		foreach (var (lo, hi) in new[] { (0m, 0.25m), (0.25m, 0.40m), (0.40m, 9m) })
			Row($"  IB/ADR {lo:0.00}-{(hi > 1 ? "up" : hi.ToString("0.00"))}", Rotation(ibEnd, frac, 2, lo, hi));
	}
}

Console.WriteLine();
Console.WriteLine("IB/ADR distribution (share of days):");
foreach (var (ibName, ibEnd) in new[] { ("IB-15", new TimeSpan(9, 45, 0)), ("IB-30", new TimeSpan(10, 0, 0)), ("IB-60", new TimeSpan(10, 30, 0)) })
{
	var r = days.Select(d => IbRatio(d, ibEnd)).Where(x => x >= 0).OrderBy(x => x).ToList();
	if (r.Count > 0)
		Console.WriteLine($"  {ibName}: days {r.Count}, 25th pct {r[r.Count / 4]:0.00}, median {r[r.Count / 2]:0.00}, 75th pct {r[r.Count * 3 / 4]:0.00}");
}

record Bar(int Index, DateTime Et, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal Delta);

class Trade
{
	public DateTime Day;
	public int Dir;
	public decimal Entry, Risk, Pnl;
	public string Reason;
}
