namespace Edge.Atas;

using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;

using Edge.Atas.Core;

using OFT.Attributes;

/// <summary>
/// Long/short signals on closed bars only (no repaint). A long fires when price closes back
/// above the trend band, confirmed by the bar's own delta and by CVD sitting above its EMA.
/// Each signal also plots a suggested stop at the opposite band edge.
/// </summary>
[DisplayName("Edge Long-Short Signals")]
[Category("Edge")]
public class EdgeSignals : DashboardIndicator
{
	#region Fields

	private readonly TrendBandCalc _band = new();
	private readonly CvdCalc _cvd = new();
	private readonly ClosedBarGate _gate = new();

	private readonly ValueDataSeries _long = new("Long", "Long") { VisualType = VisualMode.UpArrow, Color = Color.FromArgb(255, 0, 220, 130).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _short = new("Short", "Short") { VisualType = VisualMode.DownArrow, Color = Color.FromArgb(255, 255, 70, 70).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _stop = new("Stop", "Suggested stop") { VisualType = VisualMode.Dots, Color = Color.FromArgb(255, 255, 193, 7).Convert(), ShowZeroValue = false, Width = 4 };

	private bool _requireDelta = true;
	private bool _requireCvd = true;
	private bool _allowReversals = true;
	private int _cooldown = 5;
	private int _offsetTicks = 4;
	private int _lastLongBar = -1000;
	private int _lastShortBar = -1000;

	#endregion

	#region Properties

	[Parameter]
	[Display(Name = "Band EMA period", GroupName = "Trend band", Order = 10)]
	[Range(1, 1000)]
	public int Period { get => _band.Period; set { _band.Period = value; RecalculateValues(); } }

	[Display(Name = "Band ATR period", GroupName = "Trend band", Order = 20)]
	[Range(1, 1000)]
	public int AtrPeriod { get => _band.AtrPeriod; set { _band.AtrPeriod = value; RecalculateValues(); } }

	[Parameter]
	[Display(Name = "Band ATR multiplier", GroupName = "Trend band", Order = 30)]
	[Range(0.1, 10)]
	public decimal Multiplier { get => _band.Multiplier; set { _band.Multiplier = value; RecalculateValues(); } }

	[Display(Name = "CVD EMA period", GroupName = "Filters", Order = 40)]
	[Range(1, 1000)]
	public int CvdPeriod { get => _cvd.SignalPeriod; set { _cvd.SignalPeriod = value; RecalculateValues(); } }

	[Display(Name = "Require bar delta in signal direction", GroupName = "Filters", Order = 50)]
	public bool RequireDelta { get => _requireDelta; set { _requireDelta = value; RecalculateValues(); } }

	[Display(Name = "Require CVD above/below its EMA", GroupName = "Filters", Order = 60)]
	public bool RequireCvd { get => _requireCvd; set { _requireCvd = value; RecalculateValues(); } }

	[Display(Name = "Allow reversal signals (off = pullbacks only)", GroupName = "Filters", Order = 70)]
	public bool AllowReversals { get => _allowReversals; set { _allowReversals = value; RecalculateValues(); } }

	[Display(Name = "Cooldown bars between same-side signals", GroupName = "Filters", Order = 80)]
	[Range(0, 500)]
	public int Cooldown { get => _cooldown; set { _cooldown = value; RecalculateValues(); } }

	[Display(Name = "Arrow offset (ticks)", GroupName = "Display", Order = 90)]
	[Range(0, 200)]
	public int OffsetTicks { get => _offsetTicks; set { _offsetTicks = value; RecalculateValues(); } }

	[Display(Name = "Alert on signal", GroupName = "Alerts", Order = 100)]
	public bool UseAlerts { get; set; }

	[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 110)]
	public string AlertFile { get; set; } = "alert1";

	#endregion

	public EdgeSignals()
	{
		DenyToChangePanel = true;

		DataSeries[0] = _long;
		DataSeries.Add(_short);
		DataSeries.Add(_stop);
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
		{
			_band.Clear();
			_cvd.Clear();
			_gate.Reset();
			_lastLongBar = _lastShortBar = -1000;
		}

		var candle = GetCandle(bar);
		var prevClose = bar > 0 ? GetCandle(bar - 1).Close : candle.Close;

		_band.Calculate(bar, candle.High, candle.Low, candle.Close, prevClose);
		_cvd.Calculate(bar, candle.Delta, IsNewSession(bar));

		if (_gate.IsNewBar(bar))
			Evaluate(bar - 1);
	}

	private void Evaluate(int c)
	{
		if (c < 1)
			return;

		var candle = GetCandle(c);
		var regime = _band.RegimeAt(c);
		var prevRegime = _band.RegimeAt(c - 1);
		var prevTrend = _band.TrendAt(c - 1);
		var offset = _offsetTicks * InstrumentInfo.TickSize;

		var isLong = regime == Regime.Bull && prevRegime != Regime.Bull
			&& (!_requireDelta || candle.Delta > 0)
			&& (!_requireCvd || _cvd.Bias(c) == Regime.Bull)
			&& (_allowReversals || prevTrend == Regime.Bull)
			&& c - _lastLongBar > _cooldown;

		var isShort = regime == Regime.Bear && prevRegime != Regime.Bear
			&& (!_requireDelta || candle.Delta < 0)
			&& (!_requireCvd || _cvd.Bias(c) == Regime.Bear)
			&& (_allowReversals || prevTrend == Regime.Bear)
			&& c - _lastShortBar > _cooldown;

		if (isLong)
		{
			_long[c] = candle.Low - offset;
			_stop[c] = _band.Lower(c);
			_lastLongBar = c;
			AlertIfLive(c, $"LONG signal at {candle.Close}");
		}
		else if (isShort)
		{
			_short[c] = candle.High + offset;
			_stop[c] = _band.Upper(c);
			_lastShortBar = c;
			AlertIfLive(c, $"SHORT signal at {candle.Close}");
		}
	}

	private void AlertIfLive(int closed, string text)
	{
		if (UseAlerts && closed == CurrentBar - 2)
			AddAlert(AlertFile, InstrumentInfo.Instrument, text, Color.Black.Convert(), Color.White.Convert());
	}

	#region Dashboard

	protected override string DashboardTitle => "Edge Signals";

	protected override void BuildDashboard(List<PanelRow> rows)
	{
		var bar = CurrentBar - 1;
		var delta = GetCandle(bar).Delta;
		var lastIsLong = _lastLongBar > _lastShortBar;
		var lastBar = lastIsLong ? _lastLongBar : _lastShortBar;

		rows.Add(new PanelRow("Trend", _band.TrendAt(bar).ToString(), Palette.Of(_band.TrendAt(bar))));
		rows.Add(new PanelRow("Price vs band", _band.RegimeAt(bar) == Regime.Neutral ? "Inside (chop)" : _band.RegimeAt(bar).ToString(), Palette.Of(_band.RegimeAt(bar))));
		rows.Add(new PanelRow("CVD bias", _cvd.Bias(bar).ToString(), Palette.Of(_cvd.Bias(bar))));
		rows.Add(new PanelRow("Bar delta", delta.ToString("N0"), Palette.Of(delta)));
		rows.Add(new PanelRow("Last signal", lastBar < 0 ? "-" : $"{(lastIsLong ? "Long" : "Short")} ({bar - lastBar} bars ago)",
			lastBar < 0 ? Palette.Muted : lastIsLong ? Palette.Up : Palette.Down));
	}

	#endregion
}
