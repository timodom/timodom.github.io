using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Edge.Atas.Core;
using Edge.Atas.Hub;

/// <summary>
/// Replays an Edge Bar Export CSV (time,open,high,low,close,volume,delta; UTC unless --chart-et)
/// through HubCore as if every bar were live, and counts the posts. Compare "Engine trades" and
/// "net" with RangeResearch's "ORB-15 immediate entry" row on the same file.
/// Usage: --replay &lt;csv&gt; [--symbol ES|MES|NQ|MNQ] [--chart-et]
/// </summary>
static class Replay
{
	public static int Run(string[] args)
	{
		var inv = CultureInfo.InvariantCulture;
		var path = args[1];
		var symIdx = Array.IndexOf(args, "--symbol");
		var profile = InstrumentProfile.Find(symIdx > 0 ? args[symIdx + 1] : Path.GetFileName(path));

		if (profile is null)
		{
			Console.WriteLine("Unknown instrument: pass --symbol ES, MES, NQ or MNQ");
			return 1;
		}

		var chartIsUtc = !args.Contains("--chart-et");
		var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		var core = new HubCore(profile, 0.25m, TimeSpan.FromMinutes(15));
		var counts = new Dictionary<HubEvent, int>();
		var i = 0;

		foreach (var line in File.ReadLines(path).Skip(1))
		{
			var p = line.Split(',');
			var t = DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm:ss", inv);
			var et = chartIsUtc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), eastern) : t;
			var bar = new RangeBar(i++, et, decimal.Parse(p[1], inv), decimal.Parse(p[2], inv), decimal.Parse(p[3], inv),
				decimal.Parse(p[4], inv), decimal.Parse(p[5], inv), decimal.Parse(p[6], inv));

			foreach (var m in core.Process(bar, live: true))
				counts[m.Kind] = counts.GetValueOrDefault(m.Kind) + 1;
		}

		Console.WriteLine($"Profile {profile.Name}, {i} bars");

		foreach (var kind in Enum.GetValues<HubEvent>())
			Console.WriteLine($"  {kind,-14}{counts.GetValueOrDefault(kind)}");

		Console.WriteLine($"Engine trades {core.Engine.Trades}, net {core.Engine.Net.ToString("N0", inv)}");

		var entries = counts.GetValueOrDefault(HubEvent.Entry);
		var exits = counts.GetValueOrDefault(HubEvent.Exit);
		var ok = exits == core.Engine.Trades && (entries == exits || entries == exits + 1);
		Console.WriteLine(ok ? "REPLAY OK: one Entry and one Exit post per engine trade" : "REPLAY MISMATCH");
		return ok ? 0 : 1;
	}
}
