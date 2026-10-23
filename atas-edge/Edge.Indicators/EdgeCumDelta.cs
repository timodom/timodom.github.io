namespace Edge.Atas;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;

using Edge.Atas.Core;

using OFT.Attributes;

/// <summary>
/// Cumulative delta from real bid/ask volume, drawn as candles, with a trigger EMA and
/// confirmed price/CVD divergences (price makes a new swing extreme, CVD doesn't follow).
/// </summary>
[DisplayName("Edge Cumulative Delta")]
[Category("Edge")]
public class EdgeCumDelta : Indicator
{
	public enum ResetMode
	{
		[Display(Name = "Never")]
		Never,

		[Display(Name = "Each session")]
		Session
	}

	#region Fields

	private readonly CvdCalc _cvd = new();
	private readonly ClosedBarGate _gate = new();

	private readonly ValueDataSeries _cvdLine = new("CvdLine", "CVD")
	{
		VisualType = VisualMode.Hide,
		IsHidden = true,
		ShowZeroValue = false
	};

	private readonly CandleDataSeries _candles = new("CvdCandles", "CVD candles");

	private readonly ValueDataSeries _signalLine = new("SignalLine", "Signal EMA")
	{
		VisualType = VisualMode.Line,
		Color = Color.FromArgb(255, 255, 193, 7).Convert(),
		Width = 1
	};

	private readonly ValueDataSeries _bullDiv = new("BullDiv", "Bullish divergence")
	{
		VisualType = VisualMode.UpArrow,
		Color = Color.FromArgb(255, 0, 200, 120).Convert(),
		ShowZeroValue = false,
		Width = 2
	};

	private readonly ValueDataSeries _bearDiv = new("BearDiv", "Bearish divergence")
	{
		VisualType = VisualMode.DownArrow,
		Color = Color.FromArgb(255, 240, 70, 70).Convert(),
		ShowZeroValue = false,
		Width = 2
	};

	private readonly Func<int, decimal> _high;
	private readonly Func<int, decimal> _low;

	private ResetMode _reset = ResetMode.Session;
	private int _pivotStrength = 3;
	private int _maxSwingGap = 80;
	private int _lastResetBar;
	private SwingMemory _lastHigh;
	private SwingMemory _lastLow;
	private Candle _liveCandle;
	private int _liveBar = -1;

	#endregion

	#region Properties

	[Parameter]
	[Display(Name = "Reset", GroupName = "Calculation", Order = 10)]
	public ResetMode Reset
	{
		get => _reset;
		set
		{
			_reset = value;
			RecalculateValues();
		}
	}

	[Parameter]
	[Display(Name = "Signal EMA period", GroupName = "Calculation", Order = 20)]
	[Range(1, 1000)]
	public int SignalPeriod
	{
		get => _cvd.SignalPeriod;
		set
		{
			_cvd.SignalPeriod = value;
			RecalculateValues();
		}
	}

	[Display(Name = "Pivot strength (bars each side)", GroupName = "Divergence", Order = 30)]
	[Range(1, 20)]
	public int PivotStrength
	{
		get => _pivotStrength;
		set
		{
			_pivotStrength = value;
			RecalculateValues();
		}
	}

	[Display(Name = "Max bars between swings", GroupName = "Divergence", Order = 40)]
	[Range(5, 1000)]
	public int MaxSwingGap
	{
		get => _maxSwingGap;
		set
		{
			_maxSwingGap = value;
			RecalculateValues();
		}
	}

	[Display(Name = "Alert on divergence", GroupName = "Alerts", Order = 100)]
	public bool AlertDivergence { get; set; }

	[Display(Name = "Alert on CVD / EMA cross", GroupName = "Alerts", Order = 110)]
	public bool AlertCross { get; set; }

	[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 120)]
	public string AlertFile { get; set; } = "alert1";

	#endregion

	public EdgeCumDelta()
		: base(true)
	{
		Panel = IndicatorDataProvider.NewPanel;
		DenyToChangePanel = true;

		DataSeries[0] = _cvdLine;
		DataSeries.Add(_candles);
		DataSeries.Add(_signalLine);
		DataSeries.Add(_bullDiv);
		DataSeries.Add(_bearDiv);

		_high = b => GetCandle(b).High;
		_low = b => GetCandle(b).Low;
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
			ResetState();

		var candle = GetCandle(bar);
		var reset = _reset == ResetMode.Session && IsNewSession(bar);

		if (reset)
			_lastResetBar = bar;

		var close = _cvd.Calculate(bar, candle.Delta, reset);
		var open = _cvd.OpenAt(bar);

		_cvdLine[bar] = close;
		_signalLine[bar] = _cvd.Signal(bar);

		if (reset && bar > 0)
			_signalLine.SetPointOfEndLine(bar - 1);

		// Reuse one candle object for the live bar instead of allocating per tick.
		if (_liveBar != bar || _liveCandle is null)
		{
			_liveCandle = new Candle();
			_candles[bar] = _liveCandle;
			_liveBar = bar;
		}

		_liveCandle.Open = open;
		_liveCandle.Close = close;
		_liveCandle.High = Math.Max(open + candle.MaxDelta, Math.Max(open, close));
		_liveCandle.Low = Math.Min(open + candle.MinDelta, Math.Min(open, close));

		if (_gate.IsNewBar(bar))
			OnBarClosed(bar - 1);
	}

	private void OnBarClosed(int closed)
	{
		if (AlertCross && closed == CurrentBar - 2 && closed > 0 && _cvd.Bias(closed) != _cvd.Bias(closed - 1) && _cvd.Bias(closed) != Regime.Neutral)
			Alert(_cvd.Bias(closed) == Regime.Bull ? "CVD crossed above its EMA" : "CVD crossed below its EMA");

		var pivot = closed - _pivotStrength;

		if (pivot <= _lastResetBar)
			return;

		if (Pivots.IsHigh(_high, pivot, _pivotStrength))
		{
			var price = GetCandle(pivot).High;
			var cvdHigh = _candles[pivot]?.High ?? _cvd[pivot];

			// Higher price high on lower CVD high: buyers stopped pushing the new extreme.
			if (IsComparable(_lastHigh, pivot) && price > _lastHigh.Price && cvdHigh < _lastHigh.Osc)
			{
				_bearDiv[pivot] = cvdHigh;
				AlertIfLive(closed, "Bearish CVD divergence");
			}

			_lastHigh.Set(pivot, price, cvdHigh);
		}

		if (Pivots.IsLow(_low, pivot, _pivotStrength))
		{
			var price = GetCandle(pivot).Low;
			var cvdLow = _candles[pivot]?.Low ?? _cvd[pivot];

			if (IsComparable(_lastLow, pivot) && price < _lastLow.Price && cvdLow > _lastLow.Osc)
			{
				_bullDiv[pivot] = cvdLow;
				AlertIfLive(closed, "Bullish CVD divergence");
			}

			_lastLow.Set(pivot, price, cvdLow);
		}
	}

	// CVD values from different sessions are not comparable once the series resets.
	private bool IsComparable(SwingMemory prev, int pivot) =>
		prev.HasValue && prev.Bar > _lastResetBar && pivot - prev.Bar <= _maxSwingGap;

	private void AlertIfLive(int closed, string text)
	{
		if (AlertDivergence && closed == CurrentBar - 2)
			Alert(text);
	}

	private void Alert(string text) =>
		AddAlert(AlertFile, InstrumentInfo.Instrument, text, Color.Black.Convert(), Color.White.Convert());

	private void ResetState()
	{
		_cvd.Clear();
		_gate.Reset();
		_lastResetBar = 0;
		_lastHigh = default;
		_lastLow = default;
		_liveCandle = null;
		_liveBar = -1;
	}
}
