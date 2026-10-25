namespace Edge.Atas.Hub;

using System;
using System.Globalization;
using System.Text;

/// <summary>
/// The words members read. One method per event; no ATAS, no I/O, so every message is pinned by a
/// test. Prices print with two decimals, ticks and R with their sign, times as ET HH:mm.
/// </summary>
public static class SignalFormatter
{
	public const string Footer = "Model signal, simulated, not a live trade. Hypothetical results; not financial advice.";

	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

	public static string RangeSet(string sym, decimal low, decimal high, int ticks, TimeSpan lastEntry) =>
		$"{sym} opening range {P(low)}–{P(high)} ({ticks}t). Breakout armed until {lastEntry.ToString(@"hh\:mm", Inv)}.";

	public static string RangeSkipped(string sym, int ticks, int minTicks, int maxTicks) =>
		ticks < minTicks
			? $"{sym} skipped today: range {ticks}t, below the {minTicks}t minimum"
			: $"{sym} skipped today: range {ticks}t, above the {maxTicks}t maximum";

	public static string Entry(string sym, int dir, decimal price, decimal stop, decimal target, decimal rMultiple, int riskTicks) =>
		$"{sym} {Side(dir)} {P(price)} · stop {P(stop)} · target {P(target)} ({rMultiple.ToString("0.0", Inv)}R) · risk {riskTicks}t";

	public static string StopMoved(string sym, decimal stop) => $"{sym} stop → {P(stop)}";

	public static string Exit(string sym, int dir, string reason, decimal price, int ticks, decimal r, bool entryBeforeRestart) =>
		$"{sym} {Side(dir)} closed {Where(reason)} {P(price)}, {ticks.ToString("+0;-0;0", Inv)}t ({r.ToString("+0.0;-0.0;0.0", Inv)}R)"
		+ (entryBeforeRestart ? " (entry was before an ATAS restart)" : "");

	public static string WithFooter(string text) => text + "\n*" + Footer + "*";

	/// <summary>Discord webhook body. The username overrides the webhook's own name per message.</summary>
	public static string Payload(string username, string content) =>
		string.IsNullOrWhiteSpace(username)
			? "{\"content\":" + Json(content) + "}"
			: "{\"username\":" + Json(username) + ",\"content\":" + Json(content) + "}";

	private static string Side(int dir) => dir > 0 ? "LONG" : "SHORT";

	private static string P(decimal price) => price.ToString("0.00", Inv);

	private static string Where(string reason) => reason switch
	{
		"Target" => "at target",
		"Stop" => "at stop",
		"Protect" => "at the protect stop",
		"Hard close" => "at the 15:55 hard close",
		"Session ended" => "at session end",
		_ => "at " + (reason ?? "exit").ToLowerInvariant()
	};

	private static string Json(string s)
	{
		var sb = new StringBuilder("\"");

		foreach (var ch in s ?? "")
		{
			switch (ch)
			{
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", Inv));
					else sb.Append(ch);
					break;
			}
		}

		return sb.Append('"').ToString();
	}
}
