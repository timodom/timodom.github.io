namespace Edge.Atas.Core;

/// <summary>
/// Per-instrument settings for the opening range setup. ES from the ~3-year ES test, NQ from the
/// NQ test (range-breakout-log.md); micros are the full contract ÷ 10. Shared by the Opening Range
/// Bot and the Edge Signal Hub so both always trade the same numbers.
/// </summary>
public sealed record InstrumentProfile(string Name, decimal TickValue, decimal Commission, int MinRange, int MaxRange, decimal DailyLoss)
{
	public static readonly InstrumentProfile Es = new("ES", 12.5m, 4m, 8, 80, 500m);
	public static readonly InstrumentProfile Mes = new("MES", 1.25m, 1m, 8, 80, 50m);
	public static readonly InstrumentProfile Nq = new("NQ", 5m, 4m, 32, 600, 1500m);
	public static readonly InstrumentProfile Mnq = new("MNQ", 0.5m, 1m, 32, 600, 150m);

	/// <summary>In-house fallback for symbols without a profile.</summary>
	public static readonly InstrumentProfile EsDefaults = Es with { Name = "ES defaults" };

	/// <summary>Profile for a chart symbol such as "ES", "ESZ6", "#MNQ"; null when unsupported.
	/// Same order as the bot's original checks: each micro before its full-size contract.</summary>
	public static InstrumentProfile Find(string symbol)
	{
		var s = (symbol ?? "").TrimStart('#').ToUpperInvariant();

		return s.StartsWith("MES") ? Mes
			: s.StartsWith("ES") ? Es
			: s.StartsWith("MNQ") ? Mnq
			: s.StartsWith("NQ") ? Nq
			: null;
	}
}
