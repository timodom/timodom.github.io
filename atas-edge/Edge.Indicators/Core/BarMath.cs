namespace Edge.Atas.Core;

using System;
using System.Collections.Generic;

// ATAS calls OnCalculate repeatedly for the live bar (once per tick). Every helper here stores
// its result per bar and derives it from bar - 1 only, so re-running the same bar is idempotent.
// That is the whole reason these exist instead of running totals.

/// <summary>Growable per-bar value store. Reads past the end return 0.</summary>
public sealed class BarSeries
{
	private readonly List<decimal> _values = new(4096);

	public decimal this[int bar]
	{
		get => bar >= 0 && bar < _values.Count ? _values[bar] : 0m;
		set
		{
			while (_values.Count <= bar)
				_values.Add(0m);

			_values[bar] = value;
		}
	}

	public void Clear() => _values.Clear();
}

/// <summary>Exponential moving average.</summary>
public sealed class Ema
{
	private readonly BarSeries _values = new();
	private int _period;

	public Ema(int period) => Period = period;

	public int Period
	{
		get => _period;
		set => _period = Math.Max(1, value);
	}

	public decimal this[int bar] => _values[bar];

	public decimal Calculate(int bar, decimal input)
	{
		if (bar == 0)
			return _values[0] = input;

		var k = 2m / (_period + 1);
		var prev = _values[bar - 1];
		return _values[bar] = prev + k * (input - prev);
	}

	public void Clear() => _values.Clear();
}

/// <summary>Wilder's smoothing (RMA), used by ATR and RSI so values match most platforms.</summary>
public sealed class Rma
{
	private readonly BarSeries _values = new();
	private int _period;

	public Rma(int period) => Period = period;

	public int Period
	{
		get => _period;
		set => _period = Math.Max(1, value);
	}

	public decimal this[int bar] => _values[bar];

	public decimal Calculate(int bar, decimal input)
	{
		if (bar == 0)
			return _values[0] = input;

		var prev = _values[bar - 1];
		return _values[bar] = prev + (input - prev) / _period;
	}

	public void Clear() => _values.Clear();
}

/// <summary>Average true range with Wilder smoothing.</summary>
public sealed class AtrCalc
{
	private readonly Rma _rma;

	public AtrCalc(int period) => _rma = new Rma(period);

	public int Period
	{
		get => _rma.Period;
		set => _rma.Period = value;
	}

	public decimal this[int bar] => _rma[bar];

	public decimal Calculate(int bar, decimal high, decimal low, decimal prevClose)
	{
		var tr = high - low;

		if (bar > 0)
			tr = Math.Max(tr, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));

		return _rma.Calculate(bar, tr);
	}

	public void Clear() => _rma.Clear();
}

/// <summary>Relative strength index (0..100) with Wilder smoothing.</summary>
public sealed class RsiCalc
{
	private readonly Rma _gain;
	private readonly Rma _loss;

	public RsiCalc(int period)
	{
		_gain = new Rma(period);
		_loss = new Rma(period);
	}

	public int Period
	{
		get => _gain.Period;
		set => _gain.Period = _loss.Period = value;
	}

	public decimal Calculate(int bar, decimal close, decimal prevClose)
	{
		var change = bar == 0 ? 0m : close - prevClose;
		var gain = _gain.Calculate(bar, Math.Max(change, 0m));
		var loss = _loss.Calculate(bar, Math.Max(-change, 0m));

		if (loss == 0m)
			return gain == 0m ? 50m : 100m;

		return 100m - 100m / (1m + gain / loss);
	}

	public void Clear()
	{
		_gain.Clear();
		_loss.Clear();
	}
}

/// <summary>Trend direction shared by every tool in the suite.</summary>
public enum Regime
{
	Neutral = 0,
	Bull = 1,
	Bear = -1
}
