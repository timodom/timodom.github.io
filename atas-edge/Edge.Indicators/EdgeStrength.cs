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
/// Market strength band plus Bull/Bear prints.
/// Color = direction and force (smoothed RSI). Width = trend efficiency (Kaufman ER): a wide band
/// means price is travelling cleanly, a thin band means it is chopping.
/// Prints mark confirmed swing lows/highs that were either RSI-extreme or RSI-divergent.
/// </summary>
[DisplayName("Edge Strength")]
[Category("Edge")]
public class EdgeStrength : DashboardIndicator
{
	private enum Bucket
	{
		StrongBear,
		Bear,
		Neutral,
		Bull,
		StrongBull
	}

	#region Fields

	private readonly RsiCalc _rsi = new(14);
	private readonly Ema _strengthSmooth = new(3);
	private readonly Ema _basis = new(20);
	private readonly AtrCalc _atr = new(14);
	private readonly BarSeries _rsiValues = new();
	private readonly BarSeries _strength = new();
	private readonly BarSeries _efficiency = new();
	private readonly BarSeries _bandUp = new();
	private readonly BarSeries _bandDn = new();
	private readonly ClosedBarGate _gate = new();
	private readonly RangeDataSeries[] _fills;
	private readonly Func<int, decimal> _high;
	private readonly Func<int, decimal> _low;

	private readonly ValueDataSeries _bullPrint = new("BullPrint", "Bull print") { VisualType = VisualMode.UpArrow, Color = Color.FromArgb(255, 0, 230, 140).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _bearPrint = new("BearPrint", "Bear print") { VisualType = VisualMode.DownArrow, Color = Color.FromArgb(255, 255, 80, 80).Convert(), ShowZeroValue = false, Width = 3 };

	private int _erPeriod = 10;
	private decimal _minWidth = 0.3m;
	private decimal _maxWidth = 1.5m;
	private int _strongLevel = 40;
	private int _weakLevel = 10;
	private int _pivotStrength = 2;
	private int _oversold = 35;
	private int _overbought = 65;
	private int _offsetTicks = 4;
	private SwingMemory _lastHigh;
	private SwingMemory _lastLow;

	#endregion

	#region Properties

	[Parameter]
	[Display(Name = "RSI period", GroupName = "Strength", Order = 10)]
	[Range(2, 500)]
	public int RsiPeriod { get => _rsi.Period; set { _rsi.Period = value; RecalculateValues(); } }

	[Display(Name = "Strong level (0-100)", GroupName = "Strength", Order = 20)]
	[Range(1, 100)]
	public int StrongLevel { get => _strongLevel; set { _strongLevel = value; RecalculateValues(); } }

	[Display(Name = "Neutral zone (0-100)", GroupName = "Strength", Order = 30)]
	[Range(0, 100)]
	public int WeakLevel { get => _weakLevel; set { _weakLevel = value; RecalculateValues(); } }

	[Parameter]
	[Display(Name = "Band EMA period", GroupName = "Band", Order = 40)]
	[Range(1, 1000)]
	public int BandPeriod { get => _basis.Period; set { _basis.Period = value; RecalculateValues(); } }

	[Display(Name = "Efficiency lookback", GroupName = "Band", Order = 50)]
	[Range(2, 500)]
	public int ErPeriod { get => _erPeriod; set { _erPeriod = value; RecalculateValues(); } }

	[Display(Name = "Min width (x ATR, choppy)", GroupName = "Band", Order = 60)]
	[Range(0, 10)]
	public decimal MinWidth { get => _minWidth; set { _minWidth = value; RecalculateValues(); } }

	[Display(Name = "Max width (x ATR, trending)", GroupName = "Band", Order = 70)]
	[Range(0, 10)]
	public decimal MaxWidth { get => _maxWidth; set { _maxWidth = value; RecalculateValues(); } }

	[Display(Name = "Pivot strength (bars each side)", GroupName = "Prints", Order = 80)]
	[Range(1, 20)]
	public int PivotStrength { get => _pivotStrength; set { _pivotStrength = value; RecalculateValues(); } }

	[Display(Name = "RSI oversold for bull print", GroupName = "Prints", Order = 90)]
	[Range(1, 99)]
	public int Oversold { get => _oversold; set { _oversold = value; RecalculateValues(); } }

	[Display(Name = "RSI overbought for bear print", GroupName = "Prints", Order = 95)]
	[Range(1, 99)]
	public int Overbought { get => _overbought; set { _overbought = value; RecalculateValues(); } }

	[Display(Name = "Arrow offset (ticks)", GroupName = "Prints", Order = 98)]
	[Range(0, 200)]
	public int OffsetTicks { get => _offsetTicks; set { _offsetTicks = value; RecalculateValues(); } }

	[Display(Name = "Alert on print", GroupName = "Alerts", Order = 100)]
	public bool UseAlerts { get; set; }

	[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 110)]
	public string AlertFile { get; set; } = "alert1";

	#endregion

	public EdgeStrength()
	{
		DenyToChangePanel = true;

		_fills = new[]
		{
			Fill("StrongBear", "Strong bear", Color.FromArgb(90, 255, 40, 60)),
			Fill("Bear", "Bear", Color.FromArgb(45, 255, 90, 90)),
			Fill("Neutral", "Neutral", Color.FromArgb(30, 150, 150, 150)),
			Fill("Bull", "Bull", Color.FromArgb(45, 60, 220, 140)),
			Fill("StrongBull", "Strong bull", Color.FromArgb(90, 0, 255, 140))
		};

		DataSeries[0] = _bullPrint;
		DataSeries.Add(_bearPrint);

		foreach (var fill in _fills)
			DataSeries.Add(fill);

		_high = b => GetCandle(b).High;
		_low = b => GetCandle(b).Low;
	}

	private static RangeDataSeries Fill(string id, string name, Color color) =>
		new(id, name) { RangeColor = color.Convert(), DrawAbovePrice = false };

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
			ResetState();

		var candle = GetCandle(bar);
		var prevClose = bar > 0 ? GetCandle(bar - 1).Close : candle.Close;

		var rsi = _rsiValues[bar] = _rsi.Calculate(bar, candle.Close, prevClose);
		var strength = _strength[bar] = _strengthSmooth.Calculate(bar, (rsi - 50m) * 2m);
		var er = _efficiency[bar] = Efficiency(bar);

		var basis = _basis.Calculate(bar, candle.Close);
		var halfWidth = _atr.Calculate(bar, candle.High, candle.Low, prevClose) * (_minWidth + (_maxWidth - _minWidth) * er);
		_bandUp[bar] = basis + halfWidth;
		_bandDn[bar] = basis - halfWidth;

		var bucket = ToBucket(strength);
		FillBar(bar, bucket, bucket);

		if (bar > 0)
			FillBar(bar - 1, ToBucket(_strength[bar - 1]), bucket);

		if (_gate.IsNewBar(bar))
			DetectPrints(bar - 1);
	}

	// Net move / total path over the lookback: 1 = straight line, 0 = pure chop.
	private decimal Efficiency(int bar)
	{
		if (bar < _erPeriod)
			return 0m;

		var net = Math.Abs(GetCandle(bar).Close - GetCandle(bar - _erPeriod).Close);
		var path = 0m;

		for (var i = bar - _erPeriod + 1; i <= bar; i++)
			path += Math.Abs(GetCandle(i).Close - GetCandle(i - 1).Close);

		return path == 0m ? 0m : net / path;
	}

	private void DetectPrints(int closed)
	{
		var pivot = closed - _pivotStrength;

		if (pivot < 1)
			return;

		var offset = _offsetTicks * InstrumentInfo.TickSize;

		if (Pivots.IsLow(_low, pivot, _pivotStrength))
		{
			var price = GetCandle(pivot).Low;
			var rsi = _rsiValues[pivot];

			// Lower price low on a higher RSI low = selling pressure is fading.
			var divergent = _lastLow.HasValue && price < _lastLow.Price && rsi > _lastLow.Osc;

			if (rsi <= _oversold || divergent)
			{
				_bullPrint[pivot] = price - offset;
				AlertIfLive(closed, divergent ? "Bull print (RSI divergence)" : "Bull print (oversold swing low)");
			}

			_lastLow.Set(pivot, price, rsi);
		}

		if (Pivots.IsHigh(_high, pivot, _pivotStrength))
		{
			var price = GetCandle(pivot).High;
			var rsi = _rsiValues[pivot];
			var divergent = _lastHigh.HasValue && price > _lastHigh.Price && rsi < _lastHigh.Osc;

			if (rsi >= _overbought || divergent)
			{
				_bearPrint[pivot] = price + offset;
				AlertIfLive(closed, divergent ? "Bear print (RSI divergence)" : "Bear print (overbought swing high)");
			}

			_lastHigh.Set(pivot, price, rsi);
		}
	}

	private void AlertIfLive(int closed, string text)
	{
		if (UseAlerts && closed == CurrentBar - 2)
			AddAlert(AlertFile, InstrumentInfo.Instrument, text, Color.Black.Convert(), Color.White.Convert());
	}

	private Bucket ToBucket(decimal strength) =>
		strength >= _strongLevel ? Bucket.StrongBull
		: strength >= _weakLevel ? Bucket.Bull
		: strength <= -_strongLevel ? Bucket.StrongBear
		: strength <= -_weakLevel ? Bucket.Bear
		: Bucket.Neutral;

	// Same bridging trick as the trend band: the previous bar is also drawn in the new bucket
	// so adjacent color segments touch instead of leaving a one-bar gap.
	private void FillBar(int bar, Bucket own, Bucket bridge)
	{
		var up = _bandUp[bar];
		var dn = _bandDn[bar];

		for (var i = 0; i < _fills.Length; i++)
		{
			var on = i == (int)own || i == (int)bridge;
			_fills[i][bar].Upper = on ? up : 0m;
			_fills[i][bar].Lower = on ? dn : 0m;
		}
	}

	private void ResetState()
	{
		_rsi.Clear();
		_strengthSmooth.Clear();
		_basis.Clear();
		_atr.Clear();
		_rsiValues.Clear();
		_strength.Clear();
		_efficiency.Clear();
		_bandUp.Clear();
		_bandDn.Clear();
		_gate.Reset();
		_lastHigh = default;
		_lastLow = default;
	}

	protected override string DashboardTitle => "Edge Strength";

	protected override void BuildDashboard(List<PanelRow> rows)
	{
		var bar = CurrentBar - 1;
		var strength = _strength[bar];
		var rsi = _rsiValues[bar];

		rows.Add(new PanelRow("RSI", rsi.ToString("N1"), rsi >= _overbought ? Palette.Down : rsi <= _oversold ? Palette.Up : Palette.Muted));
		rows.Add(new PanelRow("Strength", $"{strength:+0;-0;0} ({ToBucket(strength)})", Palette.Of(Math.Abs(strength) < _weakLevel ? 0m : strength)));
		rows.Add(new PanelRow("Trend efficiency", $"{_efficiency[bar] * 100m:N0}%", _efficiency[bar] >= 0.5m ? Palette.Up : Palette.Muted));
	}
}
