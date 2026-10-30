namespace Edge.Atas;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using ATAS.Strategies.Chart;

using Edge.Atas.Core;

using OFT.Rendering;
using OFT.Rendering.Context;
using OFT.Rendering.Control;

/// <summary>
/// Manages an open position on this chart's instrument: breakeven, trailing profit lock,
/// optional fixed target and a daily loss cap. It never opens trades, it only exits them.
///
/// Exits are "virtual": the trail level lives here and a market order is sent when price
/// touches it. Keep a hard stop at the broker (ATM / order template) as the disaster stop,
/// because a virtual trail does nothing if ATAS or the connection goes down.
/// </summary>
[DisplayName("Edge Trail Manager")]
[Category("Edge")]
public class EdgeTrailManager : ChartStrategy
{
	public enum TrailMode
	{
		[Display(Name = "Fixed ticks")]
		Ticks,

		[Display(Name = "ATR multiple")]
		Atr
	}

	#region Fields

	private readonly AtrCalc _atr = new(14);
	private readonly DragPanel _dashboard = new();
	private readonly List<PanelRow> _rows = new(10);

	private readonly ValueDataSeries _trailLine = new("Trail", "Trail stop") { VisualType = VisualMode.Square, Color = Color.FromArgb(255, 255, 193, 7).Convert(), ShowZeroValue = false, Width = 2 };

	private decimal _lastPosition;
	private decimal _entryPrice;
	private decimal _bestPrice;
	private decimal _stop;
	private static readonly TimeSpan ExitRetryAfter = TimeSpan.FromSeconds(5);

	private bool _exitPending;
	private DateTime _exitSentAt;
	private bool _lockedOut;
	private string _status = "Flat";

	#endregion

	#region Properties

	[Display(Name = "Breakeven after (ticks profit, 0 = off)", GroupName = "Breakeven", Order = 10)]
	[Range(0, 10000)]
	public int BreakevenTrigger { get; set; } = 8;

	[Display(Name = "Breakeven offset (ticks locked)", GroupName = "Breakeven", Order = 20)]
	[Range(0, 10000)]
	public int BreakevenOffset { get; set; } = 1;

	[Display(Name = "Start trailing after (ticks profit)", GroupName = "Trailing", Order = 30)]
	[Range(1, 10000)]
	public int TrailTrigger { get; set; } = 12;

	[Display(Name = "Trail mode", GroupName = "Trailing", Order = 40)]
	public TrailMode Mode { get; set; } = TrailMode.Ticks;

	[Display(Name = "Trail distance (ticks)", GroupName = "Trailing", Order = 50)]
	[Range(1, 10000)]
	public int TrailTicks { get; set; } = 8;

	[Display(Name = "Trail distance (x ATR)", GroupName = "Trailing", Order = 60)]
	[Range(0.1, 20)]
	public decimal TrailAtr { get; set; } = 1.5m;

	[Display(Name = "ATR period", GroupName = "Trailing", Order = 70)]
	[Range(1, 500)]
	public int AtrPeriod { get => _atr.Period; set => _atr.Period = value; }

	[Display(Name = "Fixed profit target (ticks, 0 = off)", GroupName = "Target", Order = 80)]
	[Range(0, 100000)]
	public int TargetTicks { get; set; }

	[Display(Name = "Max daily loss (account currency, 0 = off)", GroupName = "Risk", Order = 90)]
	[Range(0, 10000000)]
	public decimal MaxDailyLoss { get; set; }

	[Display(Name = "Show dashboard", GroupName = "Dashboard", Order = 100)]
	public bool ShowDashboard { get; set; } = true;

	[Browsable(false)]
	public int DashboardX { get => _dashboard.OffsetX; set => _dashboard.OffsetX = value; }

	[Browsable(false)]
	public int DashboardY { get => _dashboard.OffsetY; set => _dashboard.OffsetY = value; }

	#endregion

	public EdgeTrailManager()
	{
		DenyToChangePanel = true;
		EnableCustomDrawing = true;
		SubscribeToDrawingEvents(DrawingLayouts.Final);
		DataSeries[0] = _trailLine;
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		var candle = GetCandle(bar);
		var prevClose = bar > 0 ? GetCandle(bar - 1).Close : candle.Close;
		_atr.Calculate(bar, candle.High, candle.Low, prevClose);

		// Only manage on the live bar; history is just for the ATR warm-up.
		if (bar != CurrentBar - 1)
			return;

		var position = CurrentPosition;
		var price = candle.Close;

		if (position != _lastPosition)
			OnPositionChanged(position);

		// A rejected or stuck exit leaves the position unchanged; retry after a few seconds.
		if (_exitPending && DateTime.UtcNow - _exitSentAt > ExitRetryAfter)
			_exitPending = false;

		if (CheckDailyLoss(position))
			return;

		if (position == 0 || _exitPending)
			return;

		Manage(bar, position, price);
	}

	private void OnPositionChanged(decimal position)
	{
		// New trade or flip: restart tracking from the fill price. Adds/partials keep the trail.
		var isNewTrade = _lastPosition == 0 || Math.Sign(position) != Math.Sign(_lastPosition);

		if (isNewTrade && position != 0)
		{
			_bestPrice = AveragePrice;
			_stop = 0m;
		}

		if (position == 0)
		{
			_stop = 0m;
			_status = _lockedOut ? "Daily loss hit - locked" : "Flat";
		}

		_entryPrice = position != 0 ? AveragePrice : _entryPrice;
		_exitPending = false;
		_lastPosition = position;
	}

	private void Manage(int bar, decimal position, decimal price)
	{
		var tick = InstrumentInfo.TickSize;
		var isLong = position > 0;
		var dir = isLong ? 1m : -1m;

		_bestPrice = isLong ? Math.Max(_bestPrice, price) : Math.Min(_bestPrice, price);

		var bestTicks = (_bestPrice - _entryPrice) * dir / tick;
		var openTicks = (price - _entryPrice) * dir / tick;

		if (TargetTicks > 0 && openTicks >= TargetTicks)
		{
			Exit(position, $"Target hit (+{openTicks:N0}t)");
			return;
		}

		var candidate = 0m;

		if (BreakevenTrigger > 0 && bestTicks >= BreakevenTrigger)
			candidate = _entryPrice + dir * BreakevenOffset * tick;

		if (bestTicks >= TrailTrigger)
		{
			var distance = Mode == TrailMode.Atr ? _atr[bar] * TrailAtr : TrailTicks * tick;
			var trail = _bestPrice - dir * distance;
			candidate = candidate == 0m ? trail : isLong ? Math.Max(candidate, trail) : Math.Min(candidate, trail);
		}

		// The stop only ever ratchets toward profit.
		if (candidate != 0m && (_stop == 0m || (isLong ? candidate > _stop : candidate < _stop)))
			_stop = candidate;

		if (_stop != 0m)
			_trailLine[bar] = _stop;

		_status = _stop == 0m ? $"Waiting ({bestTicks:N0}/{TrailTrigger}t)" : bestTicks >= TrailTrigger ? "Trailing" : "Breakeven armed";

		if (_stop != 0m && (isLong ? price <= _stop : price >= _stop))
			Exit(position, $"Trail stop hit at {price}");
	}

	private bool CheckDailyLoss(decimal position)
	{
		if (MaxDailyLoss <= 0 || Portfolio is null)
			return false;

		var dayPnl = Portfolio.ClosedPnL + Portfolio.OpenPnL;

		// Session P&L back above the cap means a new session started.
		if (dayPnl > -MaxDailyLoss)
		{
			_lockedOut = false;
			return false;
		}

		// Can't block manual orders from here; it flattens anything opened after the cap is hit.
		_lockedOut = true;

		if (position != 0 && !_exitPending)
			Exit(position, "Daily loss cap - flattening");

		return true;
	}

	private void Exit(decimal position, string reason)
	{
		_exitPending = true;
		_exitSentAt = DateTime.UtcNow;
		_status = reason;

		OpenOrder(new Order
		{
			Portfolio = Portfolio,
			Security = Security,
			Direction = position > 0 ? OrderDirections.Sell : OrderDirections.Buy,
			Type = OrderTypes.Market,
			QuantityToFill = Math.Abs(position),
			Comment = "Edge trail exit"
		});
	}

	#region Dashboard

	protected override void OnRender(RenderContext context, DrawingLayouts layout)
	{
		if (!ShowDashboard || ChartInfo is null || Container is null || CurrentBar < 1)
			return;

		var tick = InstrumentInfo.TickSize;
		var price = GetCandle(CurrentBar - 1).Close;
		var position = _lastPosition;
		var openTicks = position == 0 || tick <= 0 ? 0m : (price - _entryPrice) * Math.Sign(position) / tick;

		_rows.Clear();
		_rows.Add(new PanelRow("Position", position == 0 ? "Flat" : $"{(position > 0 ? "Long" : "Short")} {Math.Abs(position)}", Palette.Of(position)));

		if (position != 0)
		{
			_rows.Add(new PanelRow("Entry", _entryPrice.ToString(), Palette.Muted));
			_rows.Add(new PanelRow("Open", $"{openTicks:+0;-0;0} ticks", Palette.Of(openTicks)));
			_rows.Add(new PanelRow("Stop", _stop == 0m ? "-" : _stop.ToString(), Palette.Muted));
		}

		_rows.Add(new PanelRow("Status", _status, _lockedOut ? Palette.Down : Palette.Muted));

		if (Portfolio is not null)
			_rows.Add(new PanelRow("Day P&L", (Portfolio.ClosedPnL + Portfolio.OpenPnL).ToString("N2"), Palette.Of(Portfolio.ClosedPnL + Portfolio.OpenPnL)));

		_dashboard.Render(context, Container.Region, "Edge Trail Manager", _rows);
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
