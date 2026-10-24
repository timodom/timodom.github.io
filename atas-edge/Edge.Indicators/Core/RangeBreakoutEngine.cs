namespace Edge.Atas.Core;

using System;

public enum RangeEntry { Immediate, Retest }

public enum RangeStop { OppositeEdge, Midpoint, FixedTicks }

public enum RangeTarget { RMultiple, RangeMultiple, FixedTicks }

public enum RangeDayState { Waiting, Building, Armed, Pending, InTrade, Done, Skipped }

/// <summary>Every rule setting for the range breakout. Defaults are ORB-15 on ES.</summary>
public sealed class RangeBreakoutSettings
{
	public TimeSpan RangeStart = new(9, 30, 0);
	public TimeSpan RangeEnd = new(9, 45, 0);
	public TimeSpan LastEntry = new(11, 30, 0);
	public TimeSpan HardClose = new(15, 55, 0);
	public int MinRangeTicks = 8;
	public int MaxRangeTicks = 80;
	public RangeEntry Entry = RangeEntry.Retest;
	public int RetestOffsetTicks = 1;
	public int RetestTimeoutBars = 6;
	public bool UseVolume;
	public decimal VolumeMult = 1.2m;
	public bool UseDelta;
	public bool UseVwap;
	public RangeStop Stop = RangeStop.OppositeEdge;
	public int StopTicks = 16;
	public RangeTarget Target = RangeTarget.RMultiple;
	public decimal TargetValue = 1.5m;
	public decimal BreakevenAtR;
	public decimal ProtectAtPct = 80m;
	public int ProtectPullbackTicks = 6;
	public int MaxTrades = 2;
	public int MaxLosers = 2;
	public decimal MaxDailyLoss = 500m;
	public int Contracts = 1;
	public decimal TickValue = 12.5m;
	public decimal Commission = 4m;
}

/// <summary>One closed bar, already converted to exchange (ET) time.</summary>
public readonly record struct RangeBar(int Index, DateTime TimeEt, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal Delta);

/// <summary>
/// The range-breakout rules, independent of ATAS. Fed one closed bar at a time; raises events
/// for every decision. The chart strategy uses the same engine for backtest and live, so the
/// backtest is exactly the logic that trades.
/// </summary>
public sealed class RangeBreakoutEngine
{
	private readonly RangeBreakoutSettings _s;

	private DateTime _day;
	private decimal _rangeVolume, _pv, _vol, _prevClose, _lastDayClose;
	private int _rangeBars, _tradesToday, _losersToday, _pendingSince, _lastDayBar;
	private bool _beDone, _protectOn;
	private decimal _best;

	public RangeBreakoutEngine(RangeBreakoutSettings settings) => _s = settings;

	#region Events

	/// <summary>Retest limit armed: (bar, direction, limit price).</summary>
	public event Action<int, int, decimal> PendingPlaced;

	public event Action<int> PendingCancelled;

	/// <summary>Position opened: (bar, direction, fill price, stop, target).</summary>
	public event Action<int, int, decimal, decimal, decimal> Entered;

	/// <summary>Stop moved (breakeven): (bar, new stop).</summary>
	public event Action<int, decimal> StopMoved;

	/// <summary>Position closed: (bar, exit price, P&amp;L in $, reason).</summary>
	public event Action<int, decimal, decimal, string> Exited;

	#endregion

	#region State

	public decimal TickSize { get; set; } = 0.25m;

	public RangeDayState State { get; private set; }

	public DateTime Day => _day;

	public decimal High { get; private set; }

	public decimal Low { get; private set; }

	public int Direction { get; private set; }

	public decimal EntryPrice { get; private set; }

	public decimal StopPrice { get; private set; }

	public decimal TargetPrice { get; private set; }

	public decimal PendingPrice { get; private set; }

	public bool ProtectOn => _protectOn;

	public decimal DayPnl { get; private set; }

	public int Trades { get; private set; }

	public int Wins { get; private set; }

	public decimal GrossWin { get; private set; }

	public decimal GrossLoss { get; private set; }

	public decimal Net { get; private set; }

	public decimal MaxDrawdown { get; private set; }

	private decimal _peak;

	private decimal Risk => Math.Abs(EntryPrice - StopPrice);

	#endregion

	public void Reset()
	{
		_day = default;
		State = RangeDayState.Waiting;
		Trades = Wins = 0;
		GrossWin = GrossLoss = Net = _peak = MaxDrawdown = 0;
	}

	public void Process(RangeBar k)
	{
		var t = k.TimeEt.TimeOfDay;

		if (k.TimeEt.Date != _day && t >= _s.RangeStart)
		{
			// Half-days end before the hard close; book any open trade at the last bar we saw.
			if (State == RangeDayState.InTrade)
				Close(_lastDayBar, _lastDayClose, "Session ended");

			if (State == RangeDayState.Pending)
				PendingCancelled?.Invoke(_lastDayBar);

			StartDay(k.TimeEt.Date);
		}

		if (k.TimeEt.Date != _day || t < _s.RangeStart)
			return;

		// Session VWAP anchored at the range start.
		_pv += (k.High + k.Low + k.Close) / 3m * k.Volume;
		_vol += k.Volume;

		if (t < _s.RangeEnd)
		{
			High = _rangeBars == 0 ? k.High : Math.Max(High, k.High);
			Low = _rangeBars == 0 ? k.Low : Math.Min(Low, k.Low);
			_rangeVolume += k.Volume;
			_rangeBars++;
			State = RangeDayState.Building;
			Remember(k);
			return;
		}

		if (State == RangeDayState.Building)
		{
			var size = (High - Low) / TickSize;
			State = _rangeBars > 0 && size >= _s.MinRangeTicks && size <= _s.MaxRangeTicks ? RangeDayState.Armed : RangeDayState.Skipped;
		}

		switch (State)
		{
			case RangeDayState.InTrade:
				ManageTrade(k, t);
				break;

			case RangeDayState.Pending:
				ManagePending(k, t);
				break;

			case RangeDayState.Armed:
				CheckBreakout(k, t);
				break;
		}

		Remember(k);
	}

	/// <summary>
	/// Live mode: the broker filled or exited before the bar closed. Keeps the engine in step
	/// with the real position instead of its own closed-bar estimate.
	/// </summary>
	public void SyncExternalExit(int bar, decimal price, string reason)
	{
		if (State == RangeDayState.InTrade)
			Close(bar, price, reason);
	}

	public void SyncExternalFill(int bar, decimal price)
	{
		if (State == RangeDayState.Pending)
			Open(bar, Direction, price);
	}

	private void Remember(RangeBar k)
	{
		_prevClose = k.Close;
		_lastDayBar = k.Index;
		_lastDayClose = k.Close;
	}

	private void CheckBreakout(RangeBar k, TimeSpan t)
	{
		if (t >= _s.LastEntry || t >= _s.HardClose)
			return;

		// Fresh close beyond the edge only; sitting outside the range doesn't re-trigger.
		var dir = k.Close > High && _prevClose <= High ? 1 : k.Close < Low && _prevClose >= Low ? -1 : 0;

		if (dir == 0 || !PassesFilters(k, dir))
			return;

		if (_s.Entry == RangeEntry.Immediate)
		{
			Open(k.Index, dir, k.Close);
			return;
		}

		Direction = dir;
		PendingPrice = dir > 0 ? High + _s.RetestOffsetTicks * TickSize : Low - _s.RetestOffsetTicks * TickSize;
		_pendingSince = k.Index;
		State = RangeDayState.Pending;
		PendingPlaced?.Invoke(k.Index, dir, PendingPrice);
	}

	private bool PassesFilters(RangeBar k, int dir)
	{
		if (_s.UseVolume && _rangeBars > 0 && k.Volume < _rangeVolume / _rangeBars * _s.VolumeMult)
			return false;

		if (_s.UseDelta && Math.Sign(k.Delta) != dir)
			return false;

		if (_s.UseVwap && _vol > 0)
		{
			var vwap = _pv / _vol;

			if (dir > 0 ? k.Close <= vwap : k.Close >= vwap)
				return false;
		}

		return true;
	}

	private void ManagePending(RangeBar k, TimeSpan t)
	{
		if (t >= _s.LastEntry || t >= _s.HardClose || k.Index - _pendingSince > _s.RetestTimeoutBars)
		{
			State = RangeDayState.Armed;
			PendingCancelled?.Invoke(k.Index);
			return;
		}

		var touched = Direction > 0 ? k.Low <= PendingPrice : k.High >= PendingPrice;

		if (!touched)
			return;

		Open(k.Index, Direction, PendingPrice);

		// Filled this bar and the stop is also inside it: the order is unknown, assume the worst.
		if (Direction > 0 ? k.Low <= StopPrice : k.High >= StopPrice)
			Close(k.Index, StopPrice, "Stop");
	}

	private void Open(int bar, int dir, decimal price)
	{
		Direction = dir;
		EntryPrice = price;
		_beDone = false;
		_protectOn = false;
		_best = price;

		StopPrice = _s.Stop switch
		{
			RangeStop.Midpoint => (High + Low) / 2m,
			RangeStop.FixedTicks => price - dir * _s.StopTicks * TickSize,
			_ => dir > 0 ? Low - TickSize : High + TickSize
		};

		// Midpoint stop can sit on the wrong side of a fill; fall back to the fixed stop.
		if (Risk <= 0 || Math.Sign(price - StopPrice) != dir)
			StopPrice = price - dir * _s.StopTicks * TickSize;

		TargetPrice = _s.Target switch
		{
			RangeTarget.RangeMultiple => price + dir * (High - Low) * _s.TargetValue,
			RangeTarget.FixedTicks => price + dir * _s.TargetValue * TickSize,
			_ => price + dir * Risk * _s.TargetValue
		};

		TargetPrice = Round(TargetPrice);
		_tradesToday++;
		State = RangeDayState.InTrade;
		Entered?.Invoke(bar, dir, price, StopPrice, TargetPrice);
	}

	private void ManageTrade(RangeBar k, TimeSpan t)
	{
		if (t >= _s.HardClose)
		{
			Close(k.Index, k.Open, "Hard close");
			return;
		}

		// Both inside one bar: assume the stop came first (conservative).
		if (Direction > 0 ? k.Low <= StopPrice : k.High >= StopPrice)
		{
			Close(k.Index, StopPrice, "Stop");
			return;
		}

		if (Direction > 0 ? k.High >= TargetPrice : k.Low <= TargetPrice)
		{
			Close(k.Index, TargetPrice, "Target");
			return;
		}

		// Protect owns the stop once on; breakeven measures risk from the current stop and
		// would pull a locked-in stop back to entry.
		if (_s.BreakevenAtR > 0 && !_beDone && !_protectOn)
		{
			var favorable = Direction > 0 ? k.High - EntryPrice : EntryPrice - k.Low;

			if (favorable >= Risk * _s.BreakevenAtR)
			{
				StopPrice = EntryPrice + Direction * TickSize;
				_beDone = true;
				StopMoved?.Invoke(k.Index, StopPrice);
			}
		}

		if (_s.ProtectAtPct > 0)
			Protect(k);
	}

	// Once price has covered ProtectAtPct of the way to the target, the stop trails
	// ProtectPullbackTicks behind the best price and only moves in the trade's favor.
	private void Protect(RangeBar k)
	{
		_best = Direction > 0 ? Math.Max(_best, k.High) : Math.Min(_best, k.Low);

		var trigger = EntryPrice + (TargetPrice - EntryPrice) * _s.ProtectAtPct / 100m;

		if (!_protectOn && (Direction > 0 ? _best >= trigger : _best <= trigger))
			_protectOn = true;

		if (!_protectOn)
			return;

		var stop = Round(_best - Direction * _s.ProtectPullbackTicks * TickSize);

		if (Direction > 0 ? stop > StopPrice : stop < StopPrice)
		{
			StopPrice = stop;
			StopMoved?.Invoke(k.Index, StopPrice);
		}

		// The close is the bar's last print, so a close past the new stop means price came
		// back through it after making the best price.
		if (Direction > 0 ? k.Close <= StopPrice : k.Close >= StopPrice)
			Close(k.Index, StopPrice, "Protect");
	}

	private void Close(int bar, decimal exit, string reason)
	{
		var ticks = (exit - EntryPrice) * Direction / TickSize;
		var pnl = ticks * _s.TickValue * _s.Contracts - _s.Commission * _s.Contracts;

		if (pnl > 0)
		{
			Wins++;
			GrossWin += pnl;
		}
		else
		{
			_losersToday++;
			GrossLoss -= pnl;
		}

		Trades++;
		Net += pnl;
		DayPnl += pnl;
		_peak = Math.Max(_peak, Net);
		MaxDrawdown = Math.Max(MaxDrawdown, _peak - Net);

		var doneForDay = _tradesToday >= _s.MaxTrades || _losersToday >= _s.MaxLosers || (_s.MaxDailyLoss > 0 && DayPnl <= -_s.MaxDailyLoss);
		State = doneForDay ? RangeDayState.Done : RangeDayState.Armed;
		Exited?.Invoke(bar, exit, pnl, reason);
	}

	private void StartDay(DateTime day)
	{
		_day = day;
		State = RangeDayState.Waiting;
		High = Low = _rangeVolume = _pv = _vol = DayPnl = 0;
		_rangeBars = _tradesToday = _losersToday = 0;
	}

	// Orders must sit on a valid tick; R-multiple targets rarely land on one.
	private decimal Round(decimal price) => TickSize > 0 ? Math.Round(price / TickSize) * TickSize : price;
}
