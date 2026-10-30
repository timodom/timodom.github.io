namespace Edge.Atas;

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;

using Edge.Atas.Core;

using OFT.Attributes;

/// <summary>
/// ATR momentum band around an EMA. Candles are painted bull / bear above / below the band and
/// neutral inside it; a bull bar that also printed positive delta gets the "strong" shade.
/// </summary>
[DisplayName("Edge Trend Band")]
[Category("Edge")]
public class EdgeTrendBand : Indicator
{
	#region Fields

	private readonly TrendBandCalc _band = new();
	private readonly ClosedBarGate _gate = new();

	private readonly PaintbarsDataSeries _paint = new("Paint", "Candle colors") { IsHidden = true };

	private readonly ValueDataSeries _upper = new("Upper", "Upper") { VisualType = VisualMode.Line, Color = Color.FromArgb(160, 128, 128, 128).Convert() };
	private readonly ValueDataSeries _lower = new("Lower", "Lower") { VisualType = VisualMode.Line, Color = Color.FromArgb(160, 128, 128, 128).Convert() };
	private readonly ValueDataSeries _basis = new("Basis", "Basis") { VisualType = VisualMode.Line, Color = Color.FromArgb(120, 200, 200, 200).Convert() };

	private readonly RangeDataSeries _bullFill = new("BullFill", "Bull fill") { RangeColor = Color.FromArgb(45, 0, 200, 120).Convert(), DrawAbovePrice = false };
	private readonly RangeDataSeries _bearFill = new("BearFill", "Bear fill") { RangeColor = Color.FromArgb(45, 240, 70, 70).Convert(), DrawAbovePrice = false };
	private readonly RangeDataSeries _flatFill = new("FlatFill", "Neutral fill") { RangeColor = Color.FromArgb(30, 150, 150, 150).Convert(), DrawAbovePrice = false };

	private Color _bullColor = Color.FromArgb(255, 0, 170, 100);
	private Color _strongBullColor = Color.FromArgb(255, 0, 255, 150);
	private Color _bearColor = Color.FromArgb(255, 200, 50, 50);
	private Color _strongBearColor = Color.FromArgb(255, 255, 60, 90);
	private Color _neutralColor = Color.FromArgb(255, 130, 130, 140);
	private bool _paintCandles = true;

	#endregion

	#region Properties

	[Parameter]
	[Display(Name = "EMA period", GroupName = "Calculation", Order = 10)]
	[Range(1, 1000)]
	public int Period
	{
		get => _band.Period;
		set
		{
			_band.Period = value;
			RecalculateValues();
		}
	}

	[Parameter]
	[Display(Name = "ATR period", GroupName = "Calculation", Order = 20)]
	[Range(1, 1000)]
	public int AtrPeriod
	{
		get => _band.AtrPeriod;
		set
		{
			_band.AtrPeriod = value;
			RecalculateValues();
		}
	}

	[Parameter]
	[Display(Name = "ATR multiplier (band half-width)", GroupName = "Calculation", Order = 30)]
	[Range(0.1, 10)]
	public decimal Multiplier
	{
		get => _band.Multiplier;
		set
		{
			_band.Multiplier = value;
			RecalculateValues();
		}
	}

	[Display(Name = "Paint candles", GroupName = "Colors", Order = 40)]
	public bool PaintCandles
	{
		get => _paintCandles;
		set
		{
			_paintCandles = value;
			RecalculateValues();
		}
	}

	[Display(Name = "Bull", GroupName = "Colors", Order = 50)]
	public Color BullColor { get => _bullColor; set { _bullColor = value; RecalculateValues(); } }

	[Display(Name = "Bull + positive delta", GroupName = "Colors", Order = 60)]
	public Color StrongBullColor { get => _strongBullColor; set { _strongBullColor = value; RecalculateValues(); } }

	[Display(Name = "Bear", GroupName = "Colors", Order = 70)]
	public Color BearColor { get => _bearColor; set { _bearColor = value; RecalculateValues(); } }

	[Display(Name = "Bear + negative delta", GroupName = "Colors", Order = 80)]
	public Color StrongBearColor { get => _strongBearColor; set { _strongBearColor = value; RecalculateValues(); } }

	[Display(Name = "Neutral (inside band)", GroupName = "Colors", Order = 90)]
	public Color NeutralColor { get => _neutralColor; set { _neutralColor = value; RecalculateValues(); } }

	[Display(Name = "Alert on regime change", GroupName = "Alerts", Order = 100)]
	public bool UseAlerts { get; set; }

	[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 110)]
	public string AlertFile { get; set; } = "alert1";

	#endregion

	public EdgeTrendBand()
		: base(true)
	{
		DenyToChangePanel = true;

		DataSeries[0] = _paint;
		DataSeries.Add(_upper);
		DataSeries.Add(_lower);
		DataSeries.Add(_basis);
		DataSeries.Add(_bullFill);
		DataSeries.Add(_bearFill);
		DataSeries.Add(_flatFill);
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
		{
			_band.Clear();
			_gate.Reset();
		}

		var candle = GetCandle(bar);
		var prevClose = bar > 0 ? GetCandle(bar - 1).Close : candle.Close;

		_band.Calculate(bar, candle.High, candle.Low, candle.Close, prevClose);

		_upper[bar] = _band.Upper(bar);
		_lower[bar] = _band.Lower(bar);
		_basis[bar] = _band.Basis(bar);

		var regime = _band.RegimeAt(bar);
		FillBar(bar, regime, regime);

		// Repaint the previous bar's fill so the polygon of the new regime connects to it.
		if (bar > 0)
			FillBar(bar - 1, _band.RegimeAt(bar - 1), regime);

		_paint[bar] = _paintCandles ? CandleColor(regime, candle.Delta).Convert() : null;

		if (_gate.IsNewBar(bar) && UseAlerts && bar == CurrentBar - 1)
		{
			var closed = bar - 1;
			var now = _band.RegimeAt(closed);

			if (closed > 0 && now != _band.RegimeAt(closed - 1))
				AddAlert(AlertFile, InstrumentInfo.Instrument, $"Trend band: {now}", Color.Black.Convert(), Color.White.Convert());
		}
	}

	private void FillBar(int bar, Regime own, Regime bridge)
	{
		var upper = _band.Upper(bar);
		var lower = _band.Lower(bar);

		SetRange(_bullFill, bar, own == Regime.Bull || bridge == Regime.Bull, upper, lower);
		SetRange(_bearFill, bar, own == Regime.Bear || bridge == Regime.Bear, upper, lower);
		SetRange(_flatFill, bar, own == Regime.Neutral || bridge == Regime.Neutral, upper, lower);
	}

	private static void SetRange(RangeDataSeries series, int bar, bool on, decimal upper, decimal lower)
	{
		series[bar].Upper = on ? upper : 0m;
		series[bar].Lower = on ? lower : 0m;
	}

	private Color CandleColor(Regime regime, decimal delta) => regime switch
	{
		Regime.Bull => delta > 0 ? _strongBullColor : _bullColor,
		Regime.Bear => delta < 0 ? _strongBearColor : _bearColor,
		_ => _neutralColor
	};
}
