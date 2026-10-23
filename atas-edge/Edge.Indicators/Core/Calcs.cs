namespace Edge.Atas.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Fires once per newly closed bar. Signals are evaluated only on closed bars so nothing
/// repaints: an arrow that appears stays where it appeared.
/// </summary>
public sealed class ClosedBarGate
{
	private int _lastBar = -1;

	/// <summary>True the first time <paramref name="bar"/> is seen; bar - 1 is then final.</summary>
	public bool IsNewBar(int bar)
	{
		if (bar == _lastBar)
			return false;

		_lastBar = bar;
		return bar > 0;
	}

	public void Reset() => _lastBar = -1;
}

/// <summary>
/// ATR band around an EMA basis. Three states instead of two: inside the band is Neutral
/// (chop), which is where most whipsaw losses happen on a plain above/below band.
/// </summary>
public sealed class TrendBandCalc
{
	private readonly Ema _basis;
	private readonly AtrCalc _atr;
	private readonly BarSeries _upper = new();
	private readonly BarSeries _lower = new();
	private readonly List<Regime> _regime = new(4096);
	private readonly List<Regime> _trend = new(4096);

	public TrendBandCalc(int period = 21, int atrPeriod = 14, decimal multiplier = 1.0m)
	{
		_basis = new Ema(period);
		_atr = new AtrCalc(atrPeriod);
		Multiplier = multiplier;
	}

	public int Period { get => _basis.Period; set => _basis.Period = value; }

	public int AtrPeriod { get => _atr.Period; set => _atr.Period = value; }

	public decimal Multiplier { get; set; }

	public decimal Basis(int bar) => _basis[bar];

	public decimal Upper(int bar) => _upper[bar];

	public decimal Lower(int bar) => _lower[bar];

	/// <summary>Where the close sits relative to the band on this bar.</summary>
	public Regime RegimeAt(int bar) => bar >= 0 && bar < _regime.Count ? _regime[bar] : Regime.Neutral;

	/// <summary>Last non-neutral regime. Stays Bull through a pullback into the band.</summary>
	public Regime TrendAt(int bar) => bar >= 0 && bar < _trend.Count ? _trend[bar] : Regime.Neutral;

	public void Calculate(int bar, decimal high, decimal low, decimal close, decimal prevClose)
	{
		var basis = _basis.Calculate(bar, close);
		var width = _atr.Calculate(bar, high, low, prevClose) * Multiplier;
		var upper = _upper[bar] = basis + width;
		var lower = _lower[bar] = basis - width;

		var regime = close > upper ? Regime.Bull : close < lower ? Regime.Bear : Regime.Neutral;
		var trend = regime != Regime.Neutral ? regime : TrendAt(bar - 1);

		Set(_regime, bar, regime);
		Set(_trend, bar, trend);
	}

	public void Clear()
	{
		_basis.Clear();
		_atr.Clear();
		_upper.Clear();
		_lower.Clear();
		_regime.Clear();
		_trend.Clear();
	}

	private static void Set(List<Regime> list, int bar, Regime value)
	{
		while (list.Count <= bar)
			list.Add(Regime.Neutral);

		list[bar] = value;
	}
}

/// <summary>Cumulative delta built from ATAS's real bid/ask delta, with an EMA trigger line.</summary>
public sealed class CvdCalc
{
	private readonly BarSeries _cvd = new();
	private readonly BarSeries _open = new();
	private readonly Ema _signal;

	public CvdCalc(int signalPeriod = 20) => _signal = new Ema(signalPeriod);

	public int SignalPeriod { get => _signal.Period; set => _signal.Period = value; }

	public decimal this[int bar] => _cvd[bar];

	public decimal OpenAt(int bar) => _open[bar];

	public decimal Signal(int bar) => _signal[bar];

	/// <summary>CVD above its EMA = buyers in control of the recent flow.</summary>
	public Regime Bias(int bar) => _cvd[bar] > _signal[bar] ? Regime.Bull : _cvd[bar] < _signal[bar] ? Regime.Bear : Regime.Neutral;

	public decimal Calculate(int bar, decimal delta, bool resetHere)
	{
		var open = resetHere || bar == 0 ? 0m : _cvd[bar - 1];
		_open[bar] = open;
		var cvd = _cvd[bar] = open + delta;
		_signal.Calculate(bar, cvd);
		return cvd;
	}

	public void Clear()
	{
		_cvd.Clear();
		_open.Clear();
		_signal.Clear();
	}
}

/// <summary>
/// Confirmed swing points: a pivot at bar p needs <c>strength</c> closed bars on each side.
/// Confirmation lags by <c>strength</c> bars by design; that lag is the price of not repainting.
/// </summary>
public static class Pivots
{
	public static bool IsHigh(Func<int, decimal> high, int pivot, int strength)
	{
		if (pivot - strength < 0)
			return false;

		var h = high(pivot);

		for (var i = 1; i <= strength; i++)
		{
			// >= on the left, > on the right: equal highs resolve to the first one.
			if (high(pivot - i) >= h || high(pivot + i) > h)
				return false;
		}

		return true;
	}

	public static bool IsLow(Func<int, decimal> low, int pivot, int strength)
	{
		if (pivot - strength < 0)
			return false;

		var l = low(pivot);

		for (var i = 1; i <= strength; i++)
		{
			if (low(pivot - i) <= l || low(pivot + i) < l)
				return false;
		}

		return true;
	}
}

/// <summary>Remembers the previous confirmed swing so the next one can be compared for divergence.</summary>
public struct SwingMemory
{
	public int Bar;
	public decimal Price;
	public decimal Osc;

	public bool HasValue => Bar > 0;

	public void Set(int bar, decimal price, decimal osc)
	{
		Bar = bar;
		Price = price;
		Osc = osc;
	}
}
