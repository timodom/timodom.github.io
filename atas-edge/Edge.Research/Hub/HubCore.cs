namespace Edge.Atas.Hub;

using System;
using System.Collections.Generic;

using Edge.Atas.Core;

public enum HubEvent { RangeSet, RangeSkipped, Entry, StopMoved, Exit }

public readonly record struct HubMessage(HubEvent Kind, string Text);

/// <summary>
/// Runs the Opening Range Bot's engine with the market edition's locked setup and turns its
/// decisions into member posts. No ATAS dependency. Only bars processed with live = true produce
/// posts, so loading a chart or restarting ATAS never replays history into the channel.
/// </summary>
public sealed class HubCore
{
	private readonly InstrumentProfile _p;
	private readonly RangeBreakoutSettings _s;
	private readonly TimeSpan _barLength;
	private List<HubMessage> _out = new();
	private bool _live, _entryPosted;
	private decimal _entry;
	private int _dir, _riskTicks;
	private DateTime _rangeDay;

	public HubCore(InstrumentProfile profile, decimal tickSize, TimeSpan barLength)
	{
		_p = profile;
		_barLength = barLength;

		// The validated setup the market edition locks: engine defaults + immediate entry + the profile.
		_s = new RangeBreakoutSettings
		{
			Entry = RangeEntry.Immediate,
			TickValue = profile.TickValue,
			Commission = profile.Commission,
			MinRangeTicks = profile.MinRange,
			MaxRangeTicks = profile.MaxRange,
			MaxDailyLoss = profile.DailyLoss
		};

		Engine = new RangeBreakoutEngine(_s) { TickSize = tickSize };
		Engine.Entered += OnEntered;
		Engine.StopMoved += OnStopMoved;
		Engine.Exited += OnExited;
	}

	public RangeBreakoutEngine Engine { get; }

	/// <summary>Feed one closed bar (ET time). Returns the posts it produced, a new list each call.</summary>
	public IReadOnlyList<HubMessage> Process(RangeBar bar, bool live)
	{
		_out = new List<HubMessage>();
		_live = live;
		Engine.Process(bar);

		// The engine only decides Armed/Skipped on the first bar after the range; members hear it as
		// soon as the last range bar closes. Same rule as the engine: MinRange <= size <= MaxRange.
		var t = bar.TimeEt.TimeOfDay;

		if (Engine.State == RangeDayState.Building && bar.TimeEt.Date == Engine.Day && t < _s.RangeEnd && t + _barLength >= _s.RangeEnd && _rangeDay != Engine.Day)
		{
			_rangeDay = Engine.Day;
			var size = (Engine.High - Engine.Low) / Engine.TickSize;

			if (live)
				_out.Add(size >= _s.MinRangeTicks && size <= _s.MaxRangeTicks
					? new HubMessage(HubEvent.RangeSet, SignalFormatter.RangeSet(_p.Name, Engine.Low, Engine.High, (int)size, _s.LastEntry))
					: new HubMessage(HubEvent.RangeSkipped, SignalFormatter.RangeSkipped(_p.Name, (int)size, _s.MinRangeTicks, _s.MaxRangeTicks)));
		}

		return _out;
	}

	private void OnEntered(int bar, int dir, decimal price, decimal stop, decimal target)
	{
		_dir = dir;
		_entry = price;
		_riskTicks = (int)Math.Round(Math.Abs(price - stop) / Engine.TickSize);
		_entryPosted = _live;

		if (!_live)
			return;

		var r = Math.Round(Math.Abs(target - price) / Math.Abs(price - stop), 1);
		_out.Add(new HubMessage(HubEvent.Entry, SignalFormatter.Entry(_p.Name, dir, price, stop, target, r, _riskTicks)));
	}

	private void OnStopMoved(int bar, decimal stop)
	{
		if (_live)
			_out.Add(new HubMessage(HubEvent.StopMoved, SignalFormatter.StopMoved(_p.Name, stop)));
	}

	private void OnExited(int bar, decimal price, decimal pnl, string reason)
	{
		if (!_live)
			return;

		var ticks = (int)Math.Round((price - _entry) * _dir / Engine.TickSize);
		var r = _riskTicks > 0 ? (decimal)ticks / _riskTicks : 0m;
		_out.Add(new HubMessage(HubEvent.Exit, SignalFormatter.Exit(_p.Name, _dir, reason, price, ticks, r, !_entryPosted)));
	}
}
