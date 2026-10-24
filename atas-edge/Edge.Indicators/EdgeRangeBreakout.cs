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
/// Opening-range / initial-balance breakout, simulated on closed bars. Places no orders.
/// Draws the range, entries and exits, and shows backtest stats for the loaded history
/// (or for a Market Replay as it plays). See docs/range-breakout-spec.md.
/// </summary>
[DisplayName("Edge Range Breakout (sim)")]
[Category("Edge")]
public class EdgeRangeBreakout : DashboardIndicator
{
	public enum EntryType { Immediate, Retest }

	public enum StopType { OppositeEdge, Midpoint, FixedTicks }

	public enum TargetType { RMultiple, RangeMultiple, FixedTicks }

	private enum DayState { Waiting, Building, Armed, Pending, InTrade, Done, Skipped }

	#region Fields

	private static readonly TimeZoneInfo Eastern = FindEastern();

	private readonly ClosedBarGate _gate = new();

	private readonly ValueDataSeries _rangeHi = new("RangeHi", "Range high") { VisualType = VisualMode.Square, Color = Color.FromArgb(220, 70, 160, 255).Convert(), ShowZeroValue = false, Width = 2 };
	private readonly ValueDataSeries _rangeLo = new("RangeLo", "Range low") { VisualType = VisualMode.Square, Color = Color.FromArgb(220, 70, 160, 255).Convert(), ShowZeroValue = false, Width = 2 };
	private readonly ValueDataSeries _stopLine = new("StopLine", "Stop") { VisualType = VisualMode.Square, Color = Color.FromArgb(200, 255, 80, 80).Convert(), ShowZeroValue = false, Width = 1 };
	private readonly ValueDataSeries _targetLine = new("TargetLine", "Target") { VisualType = VisualMode.Square, Color = Color.FromArgb(200, 0, 220, 130).Convert(), ShowZeroValue = false, Width = 1 };
	private readonly ValueDataSeries _longEntry = new("LongEntry", "Long entry") { VisualType = VisualMode.UpArrow, Color = Color.FromArgb(255, 0, 220, 130).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _shortEntry = new("ShortEntry", "Short entry") { VisualType = VisualMode.DownArrow, Color = Color.FromArgb(255, 255, 70, 70).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _winExit = new("WinExit", "Winning exit") { VisualType = VisualMode.Dots, Color = Color.FromArgb(255, 0, 255, 140).Convert(), ShowZeroValue = false, Width = 6 };
	private readonly ValueDataSeries _lossExit = new("LossExit", "Losing exit") { VisualType = VisualMode.Dots, Color = Color.FromArgb(255, 255, 60, 60).Convert(), ShowZeroValue = false, Width = 6 };

	// Settings
	private TimeSpan _rangeStart = new(9, 30, 0);
	private TimeSpan _rangeEnd = new(9, 45, 0);
	private TimeSpan _lastEntry = new(11, 30, 0);
	private TimeSpan _hardClose = new(15, 55, 0);
	private bool _chartTimeIsUtc = true;
	private int _minRangeTicks = 8;
	private int _maxRangeTicks = 80;
	private EntryType _entry = EntryType.Retest;
	private int _retestOffsetTicks = 1;
	private int _retestTimeoutBars = 6;
	private bool _useVolume;
	private decimal _volumeMult = 1.2m;
	private bool _useDelta;
	private bool _useVwap;
	private StopType _stopType = StopType.OppositeEdge;
	private int _stopTicks = 16;
	private TargetType _targetType = TargetType.RMultiple;
	private decimal _targetValue = 1.5m;
	private decimal _breakevenAtR;
	private int _maxTrades = 2;
	private int _maxLosers = 2;
	private decimal _maxDailyLoss = 500m;
	private int _contracts = 1;
	private decimal _tickValue = 12.5m;
	private decimal _commission = 4m;

	// Day state
	private DateTime _day;
	private DayState _state;
	private decimal _hi, _lo, _rangeVolume, _pv, _vol, _prevClose;
	private int _rangeBars, _tradesToday, _losersToday, _pendingSince;
	private decimal _dayPnl;
	private int _dir;
	private decimal _entryPrice, _stop, _target, _risk, _pendingPrice;
	private bool _beDone;
	private int _lastDayBar;
	private decimal _lastDayClose;

	// Backtest stats
	private int _trades, _wins;
	private decimal _grossWin, _grossLoss, _net, _peak, _maxDd;
	private string _lastBarEt = "-";

	#endregion

	#region Properties

	[Display(Name = "Range start (ET)", GroupName = "1. Session", Order = 10)]
	public TimeSpan RangeStart { get => _rangeStart; set { _rangeStart = value; RecalculateValues(); } }

	[Display(Name = "Range end (ET)  09:45 = ORB-15, 10:30 = IB", GroupName = "1. Session", Order = 20)]
	public TimeSpan RangeEnd { get => _rangeEnd; set { _rangeEnd = value; RecalculateValues(); } }

	[Display(Name = "No new entries after (ET)", GroupName = "1. Session", Order = 30)]
	public TimeSpan LastEntry { get => _lastEntry; set { _lastEntry = value; RecalculateValues(); } }

	[Display(Name = "Hard close (ET)", GroupName = "1. Session", Order = 40)]
	public TimeSpan HardClose { get => _hardClose; set { _hardClose = value; RecalculateValues(); } }

	[Display(Name = "Chart bar times are UTC (check dashboard 'Bar time ET')", GroupName = "1. Session", Order = 50)]
	public bool ChartTimeIsUtc { get => _chartTimeIsUtc; set { _chartTimeIsUtc = value; RecalculateValues(); } }

	[Display(Name = "Min range (ticks)", GroupName = "1. Session", Order = 60)]
	[Range(0, 10000)]
	public int MinRangeTicks { get => _minRangeTicks; set { _minRangeTicks = value; RecalculateValues(); } }

	[Display(Name = "Max range (ticks)", GroupName = "1. Session", Order = 70)]
	[Range(1, 10000)]
	public int MaxRangeTicks { get => _maxRangeTicks; set { _maxRangeTicks = value; RecalculateValues(); } }

	[Display(Name = "Entry", GroupName = "2. Entry", Order = 100)]
	public EntryType Entry { get => _entry; set { _entry = value; RecalculateValues(); } }

	[Display(Name = "Retest offset (ticks past edge)", GroupName = "2. Entry", Order = 110)]
	[Range(0, 100)]
	public int RetestOffsetTicks { get => _retestOffsetTicks; set { _retestOffsetTicks = value; RecalculateValues(); } }

	[Display(Name = "Retest timeout (bars)", GroupName = "2. Entry", Order = 120)]
	[Range(1, 500)]
	public int RetestTimeoutBars { get => _retestTimeoutBars; set { _retestTimeoutBars = value; RecalculateValues(); } }

	[Display(Name = "Volume filter", GroupName = "3. Filters", Order = 200)]
	public bool UseVolume { get => _useVolume; set { _useVolume = value; RecalculateValues(); } }

	[Display(Name = "Breakout volume >= x avg range bar", GroupName = "3. Filters", Order = 210)]
	[Range(0, 20)]
	public decimal VolumeMult { get => _volumeMult; set { _volumeMult = value; RecalculateValues(); } }

	[Display(Name = "Delta filter (bar delta with the break)", GroupName = "3. Filters", Order = 220)]
	public bool UseDelta { get => _useDelta; set { _useDelta = value; RecalculateValues(); } }

	[Display(Name = "VWAP filter (long above / short below)", GroupName = "3. Filters", Order = 230)]
	public bool UseVwap { get => _useVwap; set { _useVwap = value; RecalculateValues(); } }

	[Display(Name = "Stop", GroupName = "4. Exits", Order = 300)]
	public StopType Stop { get => _stopType; set { _stopType = value; RecalculateValues(); } }

	[Display(Name = "Stop ticks (Fixed mode)", GroupName = "4. Exits", Order = 310)]
	[Range(1, 10000)]
	public int StopTicks { get => _stopTicks; set { _stopTicks = value; RecalculateValues(); } }

	[Display(Name = "Target", GroupName = "4. Exits", Order = 320)]
	public TargetType Target { get => _targetType; set { _targetType = value; RecalculateValues(); } }

	[Display(Name = "Target value (R, x range, or ticks)", GroupName = "4. Exits", Order = 330)]
	[Range(0.1, 10000)]
	public decimal TargetValue { get => _targetValue; set { _targetValue = value; RecalculateValues(); } }

	[Display(Name = "Move stop to breakeven at R (0 = off)", GroupName = "4. Exits", Order = 340)]
	[Range(0, 100)]
	public decimal BreakevenAtR { get => _breakevenAtR; set { _breakevenAtR = value; RecalculateValues(); } }

	[Display(Name = "Max trades per day", GroupName = "5. Risk", Order = 400)]
	[Range(1, 50)]
	public int MaxTrades { get => _maxTrades; set { _maxTrades = value; RecalculateValues(); } }

	[Display(Name = "Max losing trades per day", GroupName = "5. Risk", Order = 410)]
	[Range(1, 50)]
	public int MaxLosers { get => _maxLosers; set { _maxLosers = value; RecalculateValues(); } }

	[Display(Name = "Max daily loss ($, 0 = off)", GroupName = "5. Risk", Order = 420)]
	[Range(0, 1000000)]
	public decimal MaxDailyLoss { get => _maxDailyLoss; set { _maxDailyLoss = value; RecalculateValues(); } }

	[Display(Name = "Contracts", GroupName = "5. Risk", Order = 430)]
	[Range(1, 1000)]
	public int Contracts { get => _contracts; set { _contracts = value; RecalculateValues(); } }

	[Display(Name = "Tick value ($)  ES 12.50, MES 1.25", GroupName = "5. Risk", Order = 440)]
	[Range(0.01, 10000)]
	public decimal TickValue { get => _tickValue; set { _tickValue = value; RecalculateValues(); } }

	[Display(Name = "Commission per contract round trip ($)", GroupName = "5. Risk", Order = 450)]
	[Range(0, 1000)]
	public decimal Commission { get => _commission; set { _commission = value; RecalculateValues(); } }

	#endregion

	public EdgeRangeBreakout()
	{
		DenyToChangePanel = true;

		DataSeries[0] = _rangeHi;
		DataSeries.Add(_rangeLo);
		DataSeries.Add(_stopLine);
		DataSeries.Add(_targetLine);
		DataSeries.Add(_longEntry);
		DataSeries.Add(_shortEntry);
		DataSeries.Add(_winExit);
		DataSeries.Add(_lossExit);
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
			ResetAll();

		if (_gate.IsNewBar(bar))
			Process(bar - 1);

		// Extend today's levels onto the forming bar so they're visible live.
		if (ToEt(GetCandle(bar).Time).Date == _day)
			DrawLevels(bar);
	}

	#region Simulation

	private void Process(int c)
	{
		var k = GetCandle(c);
		var et = ToEt(k.Time);
		var t = et.TimeOfDay;
		_lastBarEt = et.ToString("ddd HH:mm");

		if (et.Date != _day && t >= _rangeStart)
		{
			// Half-days end before the hard close; book any open trade at the last bar we saw.
			if (_state == DayState.InTrade)
				Close(_lastDayBar, _lastDayClose);

			StartDay(et.Date);
		}

		if (et.Date != _day || t < _rangeStart)
			return;

		// Session VWAP anchored at the range start, built from closed bars.
		_pv += (k.High + k.Low + k.Close) / 3m * k.Volume;
		_vol += k.Volume;

		if (t < _rangeEnd)
		{
			_hi = _rangeBars == 0 ? k.High : Math.Max(_hi, k.High);
			_lo = _rangeBars == 0 ? k.Low : Math.Min(_lo, k.Low);
			_rangeVolume += k.Volume;
			_rangeBars++;
			_state = DayState.Building;
			_prevClose = k.Close;
			return;
		}

		if (_state == DayState.Building)
		{
			var size = (_hi - _lo) / Tick;
			_state = _rangeBars > 0 && size >= _minRangeTicks && size <= _maxRangeTicks ? DayState.Armed : DayState.Skipped;
		}

		switch (_state)
		{
			case DayState.InTrade:
				ManageTrade(c, k, t);
				break;

			case DayState.Pending:
				ManagePending(c, k, t);
				break;

			case DayState.Armed:
				CheckBreakout(c, k, t);
				break;
		}

		DrawLevels(c);
		_prevClose = k.Close;
		_lastDayBar = c;
		_lastDayClose = k.Close;
	}

	private void CheckBreakout(int c, IndicatorCandle k, TimeSpan t)
	{
		if (t >= _lastEntry || t >= _hardClose)
			return;

		// Fresh close beyond the edge only; sitting outside the range doesn't re-trigger.
		var dir = k.Close > _hi && _prevClose <= _hi ? 1 : k.Close < _lo && _prevClose >= _lo ? -1 : 0;

		if (dir == 0 || !PassesFilters(k, dir))
			return;

		if (_entry == EntryType.Immediate)
		{
			Open(c, dir, k.Close);
			return;
		}

		_dir = dir;
		_pendingPrice = dir > 0 ? _hi + _retestOffsetTicks * Tick : _lo - _retestOffsetTicks * Tick;
		_pendingSince = c;
		_state = DayState.Pending;
	}

	private bool PassesFilters(IndicatorCandle k, int dir)
	{
		if (_useVolume && _rangeBars > 0 && k.Volume < _rangeVolume / _rangeBars * _volumeMult)
			return false;

		if (_useDelta && Math.Sign(k.Delta) != dir)
			return false;

		if (_useVwap && _vol > 0)
		{
			var vwap = _pv / _vol;

			if (dir > 0 ? k.Close <= vwap : k.Close >= vwap)
				return false;
		}

		return true;
	}

	private void ManagePending(int c, IndicatorCandle k, TimeSpan t)
	{
		if (t >= _lastEntry || t >= _hardClose || c - _pendingSince > _retestTimeoutBars)
		{
			_state = DayState.Armed;
			return;
		}

		var touched = _dir > 0 ? k.Low <= _pendingPrice : k.High >= _pendingPrice;

		if (!touched)
			return;

		Open(c, _dir, _pendingPrice);

		// Filled this bar and the stop is also inside it: we can't know the order, assume the worst.
		if (_dir > 0 ? k.Low <= _stop : k.High >= _stop)
			Close(c, _stop);
	}

	private void Open(int c, int dir, decimal price)
	{
		_dir = dir;
		_entryPrice = price;
		_beDone = false;

		_stop = _stopType switch
		{
			StopType.Midpoint => (_hi + _lo) / 2m,
			StopType.FixedTicks => price - dir * _stopTicks * Tick,
			_ => dir > 0 ? _lo - Tick : _hi + Tick
		};

		_risk = Math.Abs(price - _stop);

		// Midpoint stop can sit on the wrong side of a retest fill; fall back to a fixed stop.
		if (_risk <= 0 || Math.Sign(price - _stop) != dir)
		{
			_stop = price - dir * _stopTicks * Tick;
			_risk = _stopTicks * Tick;
		}

		_target = _targetType switch
		{
			TargetType.RangeMultiple => price + dir * (_hi - _lo) * _targetValue,
			TargetType.FixedTicks => price + dir * _targetValue * Tick,
			_ => price + dir * _risk * _targetValue
		};

		if (dir > 0)
			_longEntry[c] = price;
		else
			_shortEntry[c] = price;

		_tradesToday++;
		_state = DayState.InTrade;
	}

	private void ManageTrade(int c, IndicatorCandle k, TimeSpan t)
	{
		if (t >= _hardClose)
		{
			Close(c, k.Open);
			return;
		}

		var stopHit = _dir > 0 ? k.Low <= _stop : k.High >= _stop;
		var targetHit = _dir > 0 ? k.High >= _target : k.Low <= _target;

		// Both inside one bar: assume the stop came first (conservative).
		if (stopHit)
		{
			Close(c, _stop);
			return;
		}

		if (targetHit)
		{
			Close(c, _target);
			return;
		}

		if (_breakevenAtR > 0 && !_beDone)
		{
			var favorable = _dir > 0 ? k.High - _entryPrice : _entryPrice - k.Low;

			if (favorable >= _risk * _breakevenAtR)
			{
				_stop = _entryPrice + _dir * Tick;
				_beDone = true;
			}
		}
	}

	private void Close(int c, decimal exit)
	{
		var ticks = (exit - _entryPrice) * _dir / Tick;
		var pnl = ticks * _tickValue * _contracts - _commission * _contracts;

		if (pnl > 0)
		{
			_wins++;
			_grossWin += pnl;
			_winExit[c] = exit;
		}
		else
		{
			_losersToday++;
			_grossLoss -= pnl;
			_lossExit[c] = exit;
		}

		_trades++;
		_net += pnl;
		_dayPnl += pnl;
		_peak = Math.Max(_peak, _net);
		_maxDd = Math.Max(_maxDd, _peak - _net);

		var stopForDay = _tradesToday >= _maxTrades || _losersToday >= _maxLosers || (_maxDailyLoss > 0 && _dayPnl <= -_maxDailyLoss);
		_state = stopForDay ? DayState.Done : DayState.Armed;
	}

	private void DrawLevels(int bar)
	{
		if (_state is DayState.Waiting or DayState.Building or DayState.Skipped)
			return;

		_rangeHi[bar] = _hi;
		_rangeLo[bar] = _lo;

		if (_state == DayState.InTrade)
		{
			_stopLine[bar] = _stop;
			_targetLine[bar] = _target;
		}
		else
		{
			_stopLine[bar] = 0;
			_targetLine[bar] = 0;
		}
	}

	private void StartDay(DateTime day)
	{
		_day = day;
		_state = DayState.Waiting;
		_hi = _lo = _rangeVolume = _pv = _vol = _dayPnl = 0;
		_rangeBars = _tradesToday = _losersToday = 0;
	}

	private void ResetAll()
	{
		_gate.Reset();
		_day = default;
		_state = DayState.Waiting;
		_trades = _wins = 0;
		_grossWin = _grossLoss = _net = _peak = _maxDd = 0;
		_lastBarEt = "-";
	}

	private decimal Tick => InstrumentInfo.TickSize;

	private DateTime ToEt(DateTime time) =>
		_chartTimeIsUtc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), Eastern) : time;

	private static TimeZoneInfo FindEastern()
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
		catch (TimeZoneNotFoundException)
		{
			return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		}
	}

	#endregion

	#region Dashboard

	protected override string DashboardTitle => "Edge Range Breakout (sim)";

	protected override void BuildDashboard(List<PanelRow> rows)
	{
		if (Tick <= 0)
			return;

		rows.Add(new PanelRow("Bar time ET", _lastBarEt, Palette.Muted));
		rows.Add(new PanelRow("Today", _state.ToString(), _state == DayState.InTrade ? Palette.Of(_dir) : Palette.Muted));

		if (_state is not (DayState.Waiting or DayState.Building))
			rows.Add(new PanelRow("Range", $"{_lo} - {_hi} ({(_hi - _lo) / Tick:N0}t)", Palette.Muted));

		rows.Add(new PanelRow("Day P&L", _dayPnl.ToString("N0"), Palette.Of(_dayPnl)));
		rows.Add(new PanelRow("Trades", _trades.ToString(), Palette.Muted));
		rows.Add(new PanelRow("Win rate", _trades == 0 ? "-" : $"{100m * _wins / _trades:N0}%", Palette.Muted));
		rows.Add(new PanelRow("Profit factor", _grossLoss == 0 ? "-" : (_grossWin / _grossLoss).ToString("N2"), _grossWin >= _grossLoss ? Palette.Up : Palette.Down));
		rows.Add(new PanelRow("Net", _net.ToString("N0"), Palette.Of(_net)));
		rows.Add(new PanelRow("Avg trade", _trades == 0 ? "-" : (_net / _trades).ToString("N0"), Palette.Of(_net)));
		rows.Add(new PanelRow("Max drawdown", _maxDd.ToString("N0"), _maxDd > 0 ? Palette.Down : Palette.Muted));
	}

	#endregion
}
