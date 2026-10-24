namespace Edge.Atas.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Groups closed chart bars into longer clock-aligned bars (e.g. 5m into 15m blocks starting at
/// :00 :15 :30 :45), so a strategy can decide on 15-minute closes while drawn on a 5-minute chart.
/// A block is emitted as soon as its last bar closes (the :10 :25 :40 :55 bar for 5m into 15m),
/// or, if that bar is missing, when the next block's first bar arrives. The emitted bar carries the
/// block's start time and the index of its last source bar, so entries land on the bar where the
/// decision was made. Pure: no ATAS types, used by the bot and the research harness alike.
/// </summary>
public sealed class BarAggregator
{
	private readonly int _minutes, _sourceMinutes;
	private long _block = long.MinValue;
	private int _first, _last;
	private DateTime _start;
	private decimal _open, _high, _low, _close, _volume, _delta;

	public BarAggregator(int minutes, int sourceMinutes)
	{
		_minutes = minutes;
		_sourceMinutes = sourceMinutes;
	}

	public int Minutes => _minutes;

	/// <summary>Adds one closed source bar; returns 0, 1 or 2 completed blocks with their first source index.</summary>
	public List<(RangeBar Bar, int FirstIndex)> Add(RangeBar k)
	{
		var done = new List<(RangeBar, int)>(2);
		var minuteOfDay = (int)k.TimeEt.TimeOfDay.TotalMinutes;
		var block = k.TimeEt.Date.Ticks / TimeSpan.TicksPerDay * (1440 / _minutes) + minuteOfDay / _minutes;

		if (block != _block && _block != long.MinValue)
			done.Add(Flush());

		if (block != _block)
		{
			_block = block;
			_first = k.Index;
			_start = k.TimeEt.Date.AddMinutes(minuteOfDay / _minutes * _minutes);
			_open = k.Open;
			_high = k.High;
			_low = k.Low;
			_volume = _delta = 0;
		}

		_high = Math.Max(_high, k.High);
		_low = Math.Min(_low, k.Low);
		_close = k.Close;
		_volume += k.Volume;
		_delta += k.Delta;
		_last = k.Index;

		if (minuteOfDay % _minutes == _minutes - _sourceMinutes)
			done.Add(Flush());

		return done;
	}

	public void Reset() => _block = long.MinValue;

	private (RangeBar, int) Flush()
	{
		var bar = new RangeBar(_last, _start, _open, _high, _low, _close, _volume, _delta);
		var first = _first;
		_block = long.MinValue;
		return (bar, first);
	}
}
