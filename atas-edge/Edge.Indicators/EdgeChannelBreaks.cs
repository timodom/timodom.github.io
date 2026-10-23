namespace Edge.Atas;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;

using Edge.Atas.Core;

using OFT.Attributes;

/// <summary>
/// Price channel (highest high / lowest low of the previous N bars) with two trade types:
/// breakouts (close beyond the channel on above-average volume and matching delta) and
/// inner-channel fades (wick through the edge, close back inside in the upper/lower half of the bar).
/// </summary>
[DisplayName("Edge Channel Breaks")]
[Category("Edge")]
public class EdgeChannelBreaks : DashboardIndicator
{
	public enum SignalMode
	{
		[Display(Name = "Breakouts")]
		Breakout,

		[Display(Name = "Inner-channel fades")]
		Fade,

		[Display(Name = "Both")]
		Both
	}

	#region Fields

	private readonly ClosedBarGate _gate = new();
	private readonly BarSeries _hi = new();
	private readonly BarSeries _lo = new();
	private readonly Ema _avgVolume = new(20);

	private readonly ValueDataSeries _upper = new("Upper", "Channel top") { VisualType = VisualMode.Line, Color = Color.FromArgb(200, 70, 160, 255).Convert(), Width = 2 };
	private readonly ValueDataSeries _lower = new("Lower", "Channel bottom") { VisualType = VisualMode.Line, Color = Color.FromArgb(200, 70, 160, 255).Convert(), Width = 2 };
	private readonly ValueDataSeries _mid = new("Mid", "Channel mid") { VisualType = VisualMode.Line, Color = Color.FromArgb(90, 70, 160, 255).Convert() };

	private readonly ValueDataSeries _breakUp = new("BreakUp", "Breakout long") { VisualType = VisualMode.UpArrow, Color = Color.FromArgb(255, 0, 220, 130).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _breakDown = new("BreakDown", "Breakout short") { VisualType = VisualMode.DownArrow, Color = Color.FromArgb(255, 255, 70, 70).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _fadeUp = new("FadeUp", "Fade long") { VisualType = VisualMode.UpArrow, Color = Color.FromArgb(255, 120, 200, 255).Convert(), ShowZeroValue = false, Width = 2 };
	private readonly ValueDataSeries _fadeDown = new("FadeDown", "Fade short") { VisualType = VisualMode.DownArrow, Color = Color.FromArgb(255, 255, 170, 60).Convert(), ShowZeroValue = false, Width = 2 };

	private int _length = 20;
	private SignalMode _mode = SignalMode.Both;
	private int _bufferTicks = 1;
	private decimal _volumeMultiplier = 1.2m;
	private bool _requireDelta = true;
	private int _offsetTicks = 4;
	private string _lastSignal = "-";
	private int _lastSignalBar = -1;

	#endregion

	#region Properties

	[Parameter]
	[Display(Name = "Channel length (bars)", GroupName = "Channel", Order = 10)]
	[Range(2, 1000)]
	public int Length { get => _length; set { _length = value; _avgVolume.Period = value; RecalculateValues(); } }

	[Display(Name = "Signals", GroupName = "Signals", Order = 20)]
	public SignalMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }

	[Display(Name = "Breakout buffer (ticks beyond channel)", GroupName = "Signals", Order = 30)]
	[Range(0, 500)]
	public int BufferTicks { get => _bufferTicks; set { _bufferTicks = value; RecalculateValues(); } }

	[Display(Name = "Breakout volume vs average (x)", GroupName = "Signals", Order = 40)]
	[Range(0, 20)]
	public decimal VolumeMultiplier { get => _volumeMultiplier; set { _volumeMultiplier = value; RecalculateValues(); } }

	[Display(Name = "Breakout needs delta in its direction", GroupName = "Signals", Order = 50)]
	public bool RequireDelta { get => _requireDelta; set { _requireDelta = value; RecalculateValues(); } }

	[Display(Name = "Arrow offset (ticks)", GroupName = "Signals", Order = 60)]
	[Range(0, 200)]
	public int OffsetTicks { get => _offsetTicks; set { _offsetTicks = value; RecalculateValues(); } }

	[Display(Name = "Alert on signal", GroupName = "Alerts", Order = 100)]
	public bool UseAlerts { get; set; }

	[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 110)]
	public string AlertFile { get; set; } = "alert1";

	#endregion

	public EdgeChannelBreaks()
	{
		DenyToChangePanel = true;

		DataSeries[0] = _upper;
		DataSeries.Add(_lower);
		DataSeries.Add(_mid);
		DataSeries.Add(_breakUp);
		DataSeries.Add(_breakDown);
		DataSeries.Add(_fadeUp);
		DataSeries.Add(_fadeDown);
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
		{
			_hi.Clear();
			_lo.Clear();
			_avgVolume.Clear();
			_gate.Reset();
			_lastSignal = "-";
			_lastSignalBar = -1;
		}

		var candle = GetCandle(bar);

		// Channel uses only bars before this one, so the live bar can actually break it.
		var hi = candle.High;
		var lo = candle.Low;
		var from = Math.Max(0, bar - _length);

		if (bar > 0)
		{
			hi = decimal.MinValue;
			lo = decimal.MaxValue;

			for (var i = from; i < bar; i++)
			{
				var c = GetCandle(i);
				hi = Math.Max(hi, c.High);
				lo = Math.Min(lo, c.Low);
			}
		}

		_hi[bar] = hi;
		_lo[bar] = lo;
		_upper[bar] = hi;
		_lower[bar] = lo;
		_mid[bar] = (hi + lo) / 2;
		_avgVolume.Calculate(bar, candle.Volume);

		if (_gate.IsNewBar(bar))
			Evaluate(bar - 1);
	}

	private void Evaluate(int c)
	{
		if (c < _length)
			return;

		var candle = GetCandle(c);
		var prev = GetCandle(c - 1);
		var tick = InstrumentInfo.TickSize;
		var buffer = _bufferTicks * tick;
		var offset = _offsetTicks * tick;
		var hi = _hi[c];
		var lo = _lo[c];

		if (_mode != SignalMode.Fade)
		{
			// Compare against the bar's own pre-break average so the breakout bar doesn't inflate it.
			var volumeOk = candle.Volume >= _avgVolume[c - 1] * _volumeMultiplier;
			var firstUp = candle.Close > hi + buffer && prev.Close <= _hi[c - 1] + buffer;
			var firstDown = candle.Close < lo - buffer && prev.Close >= _lo[c - 1] - buffer;

			if (firstUp && volumeOk && (!_requireDelta || candle.Delta > 0))
			{
				_breakUp[c] = candle.Low - offset;
				Signal(c, "Breakout long");
				return;
			}

			if (firstDown && volumeOk && (!_requireDelta || candle.Delta < 0))
			{
				_breakDown[c] = candle.High + offset;
				Signal(c, "Breakout short");
				return;
			}
		}

		if (_mode != SignalMode.Breakout)
		{
			var range = candle.High - candle.Low;

			if (range <= 0)
				return;

			var closeLocation = (candle.Close - candle.Low) / range;

			if (candle.Low <= lo && candle.Close > lo && closeLocation >= 0.5m)
			{
				_fadeUp[c] = candle.Low - offset;
				Signal(c, "Fade long (rejected channel bottom)");
			}
			else if (candle.High >= hi && candle.Close < hi && closeLocation <= 0.5m)
			{
				_fadeDown[c] = candle.High + offset;
				Signal(c, "Fade short (rejected channel top)");
			}
		}
	}

	private void Signal(int c, string text)
	{
		_lastSignal = text;
		_lastSignalBar = c;

		if (UseAlerts && c == CurrentBar - 2)
			AddAlert(AlertFile, InstrumentInfo.Instrument, text, Color.Black.Convert(), Color.White.Convert());
	}

	protected override string DashboardTitle => "Edge Channel";

	protected override void BuildDashboard(List<PanelRow> rows)
	{
		var bar = CurrentBar - 1;
		var tick = InstrumentInfo.TickSize;

		if (tick <= 0)
			return;

		var close = GetCandle(bar).Close;
		var hi = _hi[bar];
		var lo = _lo[bar];
		var position = close > hi ? "Above channel" : close < lo ? "Below channel" : "Inside";

		rows.Add(new PanelRow("Position", position, close > hi ? Palette.Up : close < lo ? Palette.Down : Palette.Muted));
		rows.Add(new PanelRow("Width", $"{(hi - lo) / tick:N0} ticks", Palette.Muted));
		rows.Add(new PanelRow("To top", $"{(hi - close) / tick:N0} ticks", Palette.Muted));
		rows.Add(new PanelRow("To bottom", $"{(close - lo) / tick:N0} ticks", Palette.Muted));
		rows.Add(new PanelRow("Last signal", _lastSignalBar < 0 ? "-" : $"{_lastSignal} ({bar - _lastSignalBar} bars ago)",
			_lastSignal.Contains("long") ? Palette.Up : _lastSignal.Contains("short") ? Palette.Down : Palette.Muted));
	}
}
