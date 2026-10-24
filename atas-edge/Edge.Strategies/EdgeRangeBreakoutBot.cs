namespace Edge.Atas;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Threading.Tasks;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;

using Edge.Atas.Core;

using OFT.Rendering;
using OFT.Rendering.Context;
using OFT.Rendering.Control;

/// <summary>
/// Opening range / initial balance breakout bot (default ORB-15 on ES 5m).
/// Live trading OFF: simulates every trade on the chart and shows backtest stats.
/// Live trading ON: the same engine places real orders, with a stop and a target resting at
/// the broker on every position. Live orders only start once real-time (or replay) ticks
/// arrive, never from the historical load.
/// Built on Indicator (not ChartStrategy) because current ATAS only lists Indicator types;
/// orders go through the chart's TradingManager.
/// </summary>
[DisplayName("Edge Range Breakout Bot")]
[Category("Edge")]
public class EdgeRangeBreakoutBot : Indicator
{
	#region Fields

	private static readonly TimeZoneInfo Eastern = FindEastern();

	private readonly RangeBreakoutSettings _s = new();
	private readonly RangeBreakoutEngine _engine;
	private readonly ClosedBarGate _gate = new();
	private readonly DragPanel _dashboard = new();
	private readonly List<PanelRow> _rows = new(14);

	private readonly ValueDataSeries _rangeHi = new("RangeHi", "Range high") { VisualType = VisualMode.Square, Color = Color.FromArgb(220, 70, 160, 255).Convert(), ShowZeroValue = false, Width = 2 };
	private readonly ValueDataSeries _rangeLo = new("RangeLo", "Range low") { VisualType = VisualMode.Square, Color = Color.FromArgb(220, 70, 160, 255).Convert(), ShowZeroValue = false, Width = 2 };
	private readonly ValueDataSeries _stopLine = new("StopLine", "Stop") { VisualType = VisualMode.Square, Color = Color.FromArgb(200, 255, 80, 80).Convert(), ShowZeroValue = false, Width = 1 };
	private readonly ValueDataSeries _targetLine = new("TargetLine", "Target") { VisualType = VisualMode.Square, Color = Color.FromArgb(200, 0, 220, 130).Convert(), ShowZeroValue = false, Width = 1 };
	private readonly ValueDataSeries _longEntry = new("LongEntry", "Long entry") { VisualType = VisualMode.UpArrow, Color = Color.FromArgb(255, 0, 220, 130).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _shortEntry = new("ShortEntry", "Short entry") { VisualType = VisualMode.DownArrow, Color = Color.FromArgb(255, 255, 70, 70).Convert(), ShowZeroValue = false, Width = 3 };
	private readonly ValueDataSeries _winExit = new("WinExit", "Winning exit") { VisualType = VisualMode.Dots, Color = Color.FromArgb(255, 0, 255, 140).Convert(), ShowZeroValue = false, Width = 6 };
	private readonly ValueDataSeries _lossExit = new("LossExit", "Losing exit") { VisualType = VisualMode.Dots, Color = Color.FromArgb(255, 255, 60, 60).Convert(), ShowZeroValue = false, Width = 6 };

	private bool _liveTrading;
	private bool _chartTimeIsUtc = true;
	private bool _realtime;
	private int _lastCalcBar = -1;
	private bool _killedForDay;
	private decimal _lastPosition;
	private Order _entryOrder;
	private Order _stopOrder;
	private Order _targetOrder;
	private string _lastBarEt = "-";
	private string _lastEvent = "-";
	private int _pendingDir;
	private decimal _riskTicks;

	#endregion

	#region Properties

	[Display(Name = "LIVE TRADING (off = simulate only)", GroupName = "0. Mode", Order = 1)]
	public bool LiveTrading
	{
		get => _liveTrading;
		set
		{
			_liveTrading = value;
			_lastEvent = value ? "Live trading ON" : "Simulation";
		}
	}

	[Display(Name = "Flatten when strategy stops", GroupName = "0. Mode", Order = 2)]
	public bool FlattenOnStop { get; set; } = true;

	[Display(Name = "Range start (ET)", GroupName = "1. Session", Order = 10)]
	public TimeSpan RangeStart { get => _s.RangeStart; set { _s.RangeStart = value; RecalculateValues(); } }

	[Display(Name = "Range end (ET)  09:45 = ORB-15, 10:30 = IB", GroupName = "1. Session", Order = 20)]
	public TimeSpan RangeEnd { get => _s.RangeEnd; set { _s.RangeEnd = value; RecalculateValues(); } }

	[Display(Name = "No new entries after (ET)", GroupName = "1. Session", Order = 30)]
	public TimeSpan LastEntry { get => _s.LastEntry; set { _s.LastEntry = value; RecalculateValues(); } }

	[Display(Name = "Hard close (ET)", GroupName = "1. Session", Order = 40)]
	public TimeSpan HardClose { get => _s.HardClose; set { _s.HardClose = value; RecalculateValues(); } }

	[Display(Name = "Chart bar times are UTC (check dashboard 'Bar time ET')", GroupName = "1. Session", Order = 50)]
	public bool ChartTimeIsUtc { get => _chartTimeIsUtc; set { _chartTimeIsUtc = value; RecalculateValues(); } }

	[Display(Name = "Min range (ticks)", GroupName = "1. Session", Order = 60)]
	[Range(0, 10000)]
	public int MinRangeTicks { get => _s.MinRangeTicks; set { _s.MinRangeTicks = value; RecalculateValues(); } }

	[Display(Name = "Max range (ticks)", GroupName = "1. Session", Order = 70)]
	[Range(1, 10000)]
	public int MaxRangeTicks { get => _s.MaxRangeTicks; set { _s.MaxRangeTicks = value; RecalculateValues(); } }

	[Display(Name = "Entry", GroupName = "2. Entry", Order = 100)]
	public RangeEntry Entry { get => _s.Entry; set { _s.Entry = value; RecalculateValues(); } }

	[Display(Name = "Retest offset (ticks past edge)", GroupName = "2. Entry", Order = 110)]
	[Range(0, 100)]
	public int RetestOffsetTicks { get => _s.RetestOffsetTicks; set { _s.RetestOffsetTicks = value; RecalculateValues(); } }

	[Display(Name = "Retest timeout (bars)", GroupName = "2. Entry", Order = 120)]
	[Range(1, 500)]
	public int RetestTimeoutBars { get => _s.RetestTimeoutBars; set { _s.RetestTimeoutBars = value; RecalculateValues(); } }

	[Display(Name = "Volume filter", GroupName = "3. Filters", Order = 200)]
	public bool UseVolume { get => _s.UseVolume; set { _s.UseVolume = value; RecalculateValues(); } }

	[Display(Name = "Breakout volume >= x avg range bar", GroupName = "3. Filters", Order = 210)]
	[Range(0, 20)]
	public decimal VolumeMult { get => _s.VolumeMult; set { _s.VolumeMult = value; RecalculateValues(); } }

	[Display(Name = "Delta filter (bar delta with the break)", GroupName = "3. Filters", Order = 220)]
	public bool UseDelta { get => _s.UseDelta; set { _s.UseDelta = value; RecalculateValues(); } }

	[Display(Name = "VWAP filter (long above / short below)", GroupName = "3. Filters", Order = 230)]
	public bool UseVwap { get => _s.UseVwap; set { _s.UseVwap = value; RecalculateValues(); } }

	[Display(Name = "Stop", GroupName = "4. Exits", Order = 300)]
	public RangeStop StopMode { get => _s.Stop; set { _s.Stop = value; RecalculateValues(); } }

	[Display(Name = "Stop ticks (Fixed mode / fallback)", GroupName = "4. Exits", Order = 310)]
	[Range(1, 10000)]
	public int StopTicks { get => _s.StopTicks; set { _s.StopTicks = value; RecalculateValues(); } }

	[Display(Name = "Target", GroupName = "4. Exits", Order = 320)]
	public RangeTarget Target { get => _s.Target; set { _s.Target = value; RecalculateValues(); } }

	[Display(Name = "Target value (R, x range, or ticks)", GroupName = "4. Exits", Order = 330)]
	[Range(0.1, 10000)]
	public decimal TargetValue { get => _s.TargetValue; set { _s.TargetValue = value; RecalculateValues(); } }

	[Display(Name = "Move stop to breakeven at R (0 = off)", GroupName = "4. Exits", Order = 340)]
	[Range(0, 100)]
	public decimal BreakevenAtR { get => _s.BreakevenAtR; set { _s.BreakevenAtR = value; RecalculateValues(); } }

	[Display(Name = "Protect at % of target (0 = off)", GroupName = "4. Exits", Order = 350)]
	[Range(0, 100)]
	public decimal ProtectAtPct { get => _s.ProtectAtPct; set { _s.ProtectAtPct = value; RecalculateValues(); } }

	[Display(Name = "Protect: pullback allowed (ticks)", GroupName = "4. Exits", Order = 360)]
	[Range(1, 1000)]
	public int ProtectPullbackTicks { get => _s.ProtectPullbackTicks; set { _s.ProtectPullbackTicks = value; RecalculateValues(); } }

	[Display(Name = "Max trades per day", GroupName = "5. Risk", Order = 400)]
	[Range(1, 50)]
	public int MaxTrades { get => _s.MaxTrades; set { _s.MaxTrades = value; RecalculateValues(); } }

	[Display(Name = "Max losing trades per day", GroupName = "5. Risk", Order = 410)]
	[Range(1, 50)]
	public int MaxLosers { get => _s.MaxLosers; set { _s.MaxLosers = value; RecalculateValues(); } }

	[Display(Name = "Max daily loss ($, 0 = off)", GroupName = "5. Risk", Order = 420)]
	[Range(0, 1000000)]
	public decimal MaxDailyLoss { get => _s.MaxDailyLoss; set { _s.MaxDailyLoss = value; RecalculateValues(); } }

	[Display(Name = "Contracts", GroupName = "5. Risk", Order = 430)]
	[Range(1, 1000)]
	public int Contracts { get => _s.Contracts; set { _s.Contracts = value; RecalculateValues(); } }

	[Display(Name = "Tick value ($)  ES 12.50, MES 1.25", GroupName = "5. Risk", Order = 440)]
	[Range(0.01, 10000)]
	public decimal TickValue { get => _s.TickValue; set { _s.TickValue = value; RecalculateValues(); } }

	[Display(Name = "Commission per contract round trip ($)", GroupName = "5. Risk", Order = 450)]
	[Range(0, 1000)]
	public decimal Commission { get => _s.Commission; set { _s.Commission = value; RecalculateValues(); } }

	[Display(Name = "Show dashboard", GroupName = "6. Dashboard", Order = 500)]
	public bool ShowDashboard { get; set; } = true;

	[Browsable(false)]
	public int DashboardX { get => _dashboard.OffsetX; set => _dashboard.OffsetX = value; }

	[Browsable(false)]
	public int DashboardY { get => _dashboard.OffsetY; set => _dashboard.OffsetY = value; }

	#endregion

	public EdgeRangeBreakoutBot()
	{
		DenyToChangePanel = true;
		EnableCustomDrawing = true;
		SubscribeToDrawingEvents(DrawingLayouts.Final);

		DataSeries[0] = _rangeHi;
		DataSeries.Add(_rangeLo);
		DataSeries.Add(_stopLine);
		DataSeries.Add(_targetLine);
		DataSeries.Add(_longEntry);
		DataSeries.Add(_shortEntry);
		DataSeries.Add(_winExit);
		DataSeries.Add(_lossExit);

		_engine = new RangeBreakoutEngine(_s);
		_engine.PendingPlaced += OnPendingPlaced;
		_engine.PendingCancelled += OnPendingCancelled;
		_engine.Entered += OnEntered;
		_engine.StopMoved += OnStopMoved;
		_engine.Exited += OnExited;
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
		{
			_engine.Reset();
			_gate.Reset();
			_realtime = false;
		}

		// History is calculated once per bar; a repeat call for the same bar means ticks are
		// streaming (live or Market Replay). Only from then on may anything reach the broker.
		if (bar == _lastCalcBar && bar == CurrentBar - 1)
			_realtime = true;

		_lastCalcBar = bar;
		_engine.TickSize = InstrumentInfo.TickSize;

		if (_gate.IsNewBar(bar))
		{
			var k = GetCandle(bar - 1);
			var et = ToEt(k.Time);
			_lastBarEt = et.ToString("ddd HH:mm");
			_engine.Process(new RangeBar(bar - 1, et, k.Open, k.High, k.Low, k.Close, k.Volume, k.Delta));
			DrawLevels(bar - 1);
		}

		if (ToEt(GetCandle(bar).Time).Date == _engine.Day)
			DrawLevels(bar);

		if (IsLive && bar == CurrentBar - 1)
			ManageLive(bar);
	}

	private bool IsLive => _liveTrading && _realtime && !_killedForDay;

	#region Engine events

	private void OnPendingPlaced(int bar, int dir, decimal price)
	{
		_pendingDir = dir;
		_lastEvent = $"Retest {(dir > 0 ? "buy" : "sell")} limit {price}";

		if (IsLive)
			_entryOrder = Send(dir > 0 ? OrderDirections.Buy : OrderDirections.Sell, OrderTypes.Limit, price, Contracts, "Edge ORB entry");
	}

	private void OnPendingCancelled(int bar)
	{
		_lastEvent = "Retest expired";
		Cancel(ref _entryOrder);
	}

	private void OnEntered(int bar, int dir, decimal price, decimal stop, decimal target)
	{
		if (dir > 0)
			_longEntry[bar] = price;
		else
			_shortEntry[bar] = price;

		// Initial risk, kept so R still means something after the stop moves to breakeven.
		_riskTicks = InstrumentInfo.TickSize > 0 ? Math.Abs(price - stop) / InstrumentInfo.TickSize : 0;
		_lastEvent = $"{(dir > 0 ? "Long" : "Short")} {price}";

		// Immediate entries are decided on the bar close; send them now. Retest fills already
		// happened at the broker (that's what told the engine), so nothing to send.
		if (IsLive && _s.Entry == RangeEntry.Immediate && Math.Sign(CurrentPosition) != dir)
			Send(dir > 0 ? OrderDirections.Buy : OrderDirections.Sell, OrderTypes.Market, 0, Contracts, "Edge ORB entry");
	}

	private void OnStopMoved(int bar, decimal stop)
	{
		_lastEvent = $"Stop moved to {stop}";

		if (IsLive && CurrentPosition != 0)
		{
			Cancel(ref _stopOrder);
			_stopOrder = Send(CurrentPosition > 0 ? OrderDirections.Sell : OrderDirections.Buy, OrderTypes.Stop, stop, Math.Abs(CurrentPosition), "Edge ORB stop");
		}
	}

	private void OnExited(int bar, decimal price, decimal pnl, string reason)
	{
		if (pnl > 0)
			_winExit[bar] = price;
		else
			_lossExit[bar] = price;

		_lastEvent = $"{reason}: {pnl:N0}";

		// The engine closed on a bar close (hard close, or a stop/target it saw first): make
		// the broker match. If the broker already exited, the position is flat and this no-ops.
		if (IsLive)
			Flatten();
	}

	#endregion

	#region Live execution

	private void ManageLive(int bar)
	{
		var position = CurrentPosition;
		var price = GetCandle(bar).Close;

		// Real loss cap on the account, not just the engine's estimate.
		if (MaxDailyLoss > 0 && Portfolio is not null && Portfolio.ClosedPnL + Portfolio.OpenPnL <= -MaxDailyLoss)
		{
			Flatten();
			_killedForDay = true;
			_lastEvent = "Daily loss cap hit - live trading paused";
			return;
		}

		// Retest limit filled at the broker.
		if (position != 0 && _engine.State == RangeDayState.Pending)
		{
			_entryOrder = null;
			_engine.SyncExternalFill(bar, AveragePrice);
		}

		// Exits rest at the broker so the trade is protected even if ATAS goes down.
		if (position != 0 && _engine.State == RangeDayState.InTrade && _stopOrder is null)
		{
			var exitSide = position > 0 ? OrderDirections.Sell : OrderDirections.Buy;
			var qty = Math.Abs(position);
			_stopOrder = Send(exitSide, OrderTypes.Stop, _engine.StopPrice, qty, "Edge ORB stop");
			_targetOrder = Send(exitSide, OrderTypes.Limit, _engine.TargetPrice, qty, "Edge ORB target");
		}

		// Broker stop or target filled: cancel the other side, tell the engine.
		if (position == 0 && _lastPosition != 0)
		{
			Cancel(ref _stopOrder);
			Cancel(ref _targetOrder);
			_engine.SyncExternalExit(bar, price, "Broker exit");
		}

		// Hard close mid-bar instead of waiting for the bar to finish.
		if (position != 0 && ToEt(GetCandle(bar).Time).TimeOfDay >= _s.HardClose)
			Flatten();

		_lastPosition = position;
	}

	private void Flatten()
	{
		Cancel(ref _entryOrder);
		Cancel(ref _stopOrder);
		Cancel(ref _targetOrder);

		var position = CurrentPosition;

		if (position != 0)
			Send(position > 0 ? OrderDirections.Sell : OrderDirections.Buy, OrderTypes.Market, 0, Math.Abs(position), "Edge ORB flatten");
	}

	private decimal CurrentPosition => TradingManager?.Position?.Volume ?? 0m;

	private decimal AveragePrice => TradingManager?.Position?.AveragePrice ?? 0m;

	private Portfolio Portfolio => TradingManager?.Portfolio;

	private Order Send(OrderDirections side, OrderTypes type, decimal price, decimal qty, string comment)
	{
		var tm = TradingManager;
		var security = tm?.Security;

		if (security is null || tm.Portfolio is null)
		{
			_lastEvent = $"No account/instrument - {comment} not sent";
			return null;
		}

		var order = new Order
		{
			Portfolio = tm.Portfolio,
			Security = security,
			SecurityId = security.SecurityId,
			Direction = side,
			Type = type,
			QuantityToFill = qty,
			Comment = comment
		};

		if (type == OrderTypes.Limit)
			order.Price = price;
		else if (type == OrderTypes.Stop)
			order.TriggerPrice = order.Price = price;

		// Never block the calculate thread on the broker call; it can deadlock ATAS.
		Task.Run(async () =>
		{
			try { await tm.OpenOrderAsync(order, false, false, false); }
			catch (Exception ex) { _lastEvent = $"{comment} rejected: {ex.Message}"; }
		});

		return order;
	}

	private void Cancel(ref Order order)
	{
		var tm = TradingManager;
		var o = order;

		if (o is not null && o.State == OrderStates.Active && tm is not null)
		{
			Task.Run(async () =>
			{
				try { await tm.CancelOrderAsync(o, false, false); }
				catch (Exception ex) { _lastEvent = $"Cancel failed: {ex.Message}"; }
			});
		}

		order = null;
	}

	protected override void OnDispose()
	{
		if (FlattenOnStop && _liveTrading)
			Flatten();

		base.OnDispose();
	}

	#endregion

	#region Drawing and time

	private void DrawLevels(int bar)
	{
		if (_engine.State is RangeDayState.Waiting or RangeDayState.Building or RangeDayState.Skipped)
			return;

		_rangeHi[bar] = _engine.High;
		_rangeLo[bar] = _engine.Low;
		var inTrade = _engine.State == RangeDayState.InTrade;
		_stopLine[bar] = inTrade ? _engine.StopPrice : 0;
		_targetLine[bar] = inTrade ? _engine.TargetPrice : 0;
	}

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

	protected override void OnRender(RenderContext context, DrawingLayouts layout)
	{
		if (!ShowDashboard || ChartInfo is null || Container is null || InstrumentInfo.TickSize <= 0)
			return;

		var e = _engine;
		var mode = !_liveTrading ? "SIMULATION" : _killedForDay ? "LIVE - paused (loss cap)" : _realtime ? "LIVE" : "LIVE - waiting for ticks";

		_rows.Clear();
		_rows.Add(new PanelRow("Mode", mode, _liveTrading ? Palette.Down : Palette.Muted));
		_rows.Add(new PanelRow("Bar time ET", _lastBarEt, Palette.Muted));
		_rows.Add(new PanelRow("Today", e.State.ToString(), e.State == RangeDayState.InTrade ? Palette.Of(e.Direction) : Palette.Muted));

		if (e.State is not (RangeDayState.Waiting or RangeDayState.Building))
			_rows.Add(new PanelRow("Range", $"{e.Low} - {e.High} ({(e.High - e.Low) / InstrumentInfo.TickSize:N0}t)", Palette.Muted));

		if (e.State == RangeDayState.Pending)
			_rows.Add(new PanelRow("Retest limit", $"{(_pendingDir > 0 ? "Buy" : "Sell")} {e.PendingPrice:0.00}", Palette.Of(_pendingDir)));

		if (e.State == RangeDayState.InTrade)
			AddTradeRows(e);

		if (_liveTrading)
			_rows.Add(new PanelRow("Position", CurrentPosition.ToString("N0"), Palette.Of(CurrentPosition)));

		_rows.Add(new PanelRow("Last event", _lastEvent, Palette.Muted));
		_rows.Add(new PanelRow("Day P&L (sim)", e.DayPnl.ToString("N0"), Palette.Of(e.DayPnl)));
		_rows.Add(new PanelRow("Trades", e.Trades.ToString(), Palette.Muted));
		_rows.Add(new PanelRow("Win rate", e.Trades == 0 ? "-" : $"{100m * e.Wins / e.Trades:N0}%", Palette.Muted));
		_rows.Add(new PanelRow("Profit factor", e.GrossLoss == 0 ? "-" : (e.GrossWin / e.GrossLoss).ToString("N2"), e.GrossWin >= e.GrossLoss ? Palette.Up : Palette.Down));
		_rows.Add(new PanelRow("Net", e.Net.ToString("N0"), Palette.Of(e.Net)));
		_rows.Add(new PanelRow("Avg trade", e.Trades == 0 ? "-" : (e.Net / e.Trades).ToString("N0"), Palette.Of(e.Net)));
		_rows.Add(new PanelRow("Max drawdown", e.MaxDrawdown.ToString("N0"), e.MaxDrawdown > 0 ? Palette.Down : Palette.Muted));

		_dashboard.Render(context, Container.Region, "Edge Range Breakout Bot", _rows);
	}

	private void AddTradeRows(RangeBreakoutEngine e)
	{
		var tick = InstrumentInfo.TickSize;
		var last = GetCandle(CurrentBar - 1).Close;
		var ticks = e.Direction * (last - e.EntryPrice) / tick;
		var dollars = ticks * _s.TickValue * _s.Contracts;
		var r = _riskTicks > 0 ? ticks / _riskTicks : 0;

		_rows.Add(new PanelRow("Side", $"{(e.Direction > 0 ? "LONG" : "SHORT")} {_s.Contracts}", Palette.Of(e.Direction)));
		_rows.Add(new PanelRow("Entry", e.EntryPrice.ToString("0.00"), Palette.Muted));
		_rows.Add(new PanelRow("Stop", $"{e.StopPrice:0.00} ({(e.StopPrice - e.EntryPrice) / tick:+0;-0;0}t)", Palette.Down));
		_rows.Add(new PanelRow("Target", $"{e.TargetPrice:0.00} ({(e.TargetPrice - e.EntryPrice) / tick:+0;-0;0}t)", Palette.Up));

		if (_s.ProtectAtPct > 0)
		{
			var trigger = e.EntryPrice + (e.TargetPrice - e.EntryPrice) * _s.ProtectAtPct / 100m;
			_rows.Add(e.ProtectOn
				? new PanelRow("Protect", $"ON - trailing {_s.ProtectPullbackTicks}t", Palette.Up)
				: new PanelRow("Protect", $"at {trigger:0.00} ({_s.ProtectAtPct:0}%)", Palette.Muted));
		}
		_rows.Add(new PanelRow("Open P&L", $"{ticks:+0;-0;0}t  {dollars:+$#,0;-$#,0;$0}  {r:+0.00;-0.00;0.00}R", Palette.Of(ticks)));
	}

	public override bool ProcessMouseDown(RenderControlMouseEventArgs e) =>
		ShowDashboard && _dashboard.MouseDown(e.Location) || base.ProcessMouseDown(e);

	public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
	{
		if (Container is null || !_dashboard.MouseMove(e.Location, Container.Region))
			return base.ProcessMouseMove(e);

		RedrawChart();
		return true;
	}

	public override bool ProcessMouseUp(RenderControlMouseEventArgs e) =>
		_dashboard.MouseUp() || base.ProcessMouseUp(e);

	public override StdCursor GetCursor(RenderControlMouseEventArgs e) =>
		ShowDashboard && (_dashboard.IsDragging || _dashboard.Contains(e.Location)) ? StdCursor.Hand : base.GetCursor(e);

	#endregion
}
