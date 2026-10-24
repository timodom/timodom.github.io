#if MARKET
namespace OpeningRange.Market;
#else
namespace OpeningRange;
#endif

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;

using Edge.Atas.Core;

using OFT.Rendering;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
using OFT.Rendering.Tools;

/// <summary>Which bars the strategy decides on; Auto follows the instrument's validated setup.</summary>
public enum DecisionBars
{
	[Display(Name = "Auto")] Auto,
	[Display(Name = "Chart bars")] ChartBars,
	[Display(Name = "15-minute closes")] FifteenMinute
}

/// <summary>
/// Opening range / initial balance breakout bot (default ORB-15 on ES 5m).
/// Live trading OFF: simulates every trade on the chart and shows backtest stats.
/// Live trading ON: the same engine places real orders, with a stop and a target resting at
/// the broker on every position. Live orders only start once real-time (or replay) ticks
/// arrive, never from the historical load.
/// Built on Indicator (not ChartStrategy) because current ATAS only lists Indicator types;
/// orders go through the chart's TradingManager.
/// The same file builds two editions: in-house (every setting) and MARKET (Edge.Market project):
/// the validated setup locked, settings reduced, instrument detected, unsupported charts refused.
/// </summary>
#if MARKET
[DisplayName("Opening Range Bot")]
#else
[DisplayName("Opening Range Bot (in-house)")]
#endif
[Category("Edge")]
public class OpeningRangeBot : Indicator
{
	#region Fields

	private static readonly TimeZoneInfo Eastern = FindEastern();

	private readonly RangeBreakoutSettings _s = new();
	private readonly RangeBreakoutEngine _engine;
	private readonly ClosedBarGate _gate = new();
	private readonly DragPanel _dashboard = new();
	private readonly List<PanelRow> _rows = new(14);

	// Chart drawing is custom-rendered (range box, IB lines, trade tags); these hold what to draw.
	private readonly List<DayBox> _boxes = new();
	private readonly List<TradeMark> _trades = new();
	private readonly RenderFont _tagFont = new("Arial", 9, FontStyle.Bold);
	private readonly RenderFont _labelFont = new("Arial", 9);

	private bool _liveTrading;
	private bool _chartTimeIsUtc = true;
	private bool _realtime;
	private int _lastCalcBar = -1;
	private bool _killedForDay;
	private const string OrderTag = "OpeningRange";
	private readonly List<Order> _sent = new();
	private string _tradeId = "-";
	private decimal _entryPrice;
	private bool _hadPosition, _bracketSent, _flattenRequested;
	private DateTime _flattenAt, _flattenRetryAt;
	private string _lastBarEt = "-";
	private string _lastEvent = "-";
	private int _pendingDir;
	private decimal _riskTicks;
	private decimal _liveBest;
	private bool _liveProtectOn;
	private decimal _brokerStop;
	private volatile bool _modifyBusy;
	// User overrides; 0 = use the instrument profile picked from the chart's symbol.
	// The market edition hides the range/tick/commission overrides, so they stay 0 there.
#pragma warning disable CS0649
	private decimal _userDailyLoss, _userTickValue, _userCommission;
	private int _userMinRange, _userMaxRange;
#pragma warning restore CS0649
	private string _profile = "-";
	private BarAggregator _agg;
	private DecisionBars _decision = DecisionBars.Auto;
	private string _unsupported;
	private string _chartLabel = "-";

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

#if !MARKET
	[Display(Name = "Range start (ET)", GroupName = "1. Session", Order = 10)]
	public TimeSpan RangeStart { get => _s.RangeStart; set { _s.RangeStart = value; RecalculateValues(); } }

	[Display(Name = "Range end (ET)  09:45 = ORB-15, 10:30 = IB", GroupName = "1. Session", Order = 20)]
	public TimeSpan RangeEnd { get => _s.RangeEnd; set { _s.RangeEnd = value; RecalculateValues(); } }

	[Display(Name = "No new entries after (ET)", GroupName = "1. Session", Order = 30)]
	public TimeSpan LastEntry { get => _s.LastEntry; set { _s.LastEntry = value; RecalculateValues(); } }

	[Display(Name = "Hard close (ET)", GroupName = "1. Session", Order = 40)]
	public TimeSpan HardClose { get => _s.HardClose; set { _s.HardClose = value; RecalculateValues(); } }
#endif

	[Display(Name = "Chart bar times are UTC (check dashboard 'Bar time ET')", GroupName = "1. Session", Order = 50)]
	public bool ChartTimeIsUtc { get => _chartTimeIsUtc; set { _chartTimeIsUtc = value; RecalculateValues(); } }

#if !MARKET
	[Display(Name = "Decision bars (Auto: NQ/MNQ on 15m closes, ES/MES on chart bars)", GroupName = "2. Entry", Order = 105)]
	public DecisionBars Decision { get => _decision; set { _decision = value; RecalculateValues(); } }
#endif

#if !MARKET
	[Display(Name = "Min range (ticks, 0 = instrument default)", GroupName = "1. Session", Order = 60)]
	[Range(0, 10000)]
	public int MinRangeTicks { get => _userMinRange; set { _userMinRange = value; RecalculateValues(); } }

	[Display(Name = "Max range (ticks, 0 = instrument default)", GroupName = "1. Session", Order = 70)]
	[Range(0, 100000)]
	public int MaxRangeTicks { get => _userMaxRange; set { _userMaxRange = value; RecalculateValues(); } }

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
#endif

	[Display(Name = "Max daily loss ($, 0 = instrument default)", GroupName = "5. Risk", Order = 420)]
	[Range(0, 1000000)]
	public decimal MaxDailyLoss { get => _userDailyLoss; set { _userDailyLoss = value; RecalculateValues(); } }

	[Display(Name = "Max risk per trade ($, 0 = off): skip trades whose stop would lose more", GroupName = "5. Risk", Order = 425)]
	[Range(0, 1000000)]
	public decimal MaxRiskPerTrade { get => _s.MaxRiskPerTrade; set { _s.MaxRiskPerTrade = value; RecalculateValues(); } }

	[Display(Name = "Contracts", GroupName = "5. Risk", Order = 430)]
	[Range(1, 1000)]
	public int Contracts { get => _s.Contracts; set { _s.Contracts = value; RecalculateValues(); } }

#if !MARKET
	[Display(Name = "Tick value ($, 0 = instrument default)", GroupName = "5. Risk", Order = 440)]
	[Range(0, 10000)]
	public decimal TickValue { get => _userTickValue; set { _userTickValue = value; RecalculateValues(); } }

	[Display(Name = "Commission per contract round trip ($, 0 = instrument default)", GroupName = "5. Risk", Order = 450)]
	[Range(0, 1000)]
	public decimal Commission { get => _userCommission; set { _userCommission = value; RecalculateValues(); } }
#endif

	[Display(Name = "Show dashboard", GroupName = "6. Dashboard", Order = 500)]
	public bool ShowDashboard { get; set; } = true;

	[Display(Name = "Range box color", GroupName = "7. Chart", Order = 600)]
	public CrossColor BoxColor { get; set; } = CrossColor.FromRgb(158, 158, 158);

	[Display(Name = "Range box opacity (0-255)", GroupName = "7. Chart", Order = 610)]
	[Range(0, 255)]
	public int BoxOpacity { get; set; } = 60;

	[Display(Name = "IBH color", GroupName = "7. Chart", Order = 620)]
	public CrossColor HighColor { get; set; } = CrossColor.FromRgb(47, 143, 224);

	[Display(Name = "IBM color", GroupName = "7. Chart", Order = 630)]
	public CrossColor MidColor { get; set; } = CrossColor.FromRgb(26, 165, 143);

	[Display(Name = "IBL color", GroupName = "7. Chart", Order = 640)]
	public CrossColor LowColor { get; set; } = CrossColor.FromRgb(226, 71, 90);

	[Display(Name = "Previous days IB opacity (0 = hide, 255 = full)", GroupName = "7. Chart", Order = 645)]
	[Range(0, 255)]
	public int PastLevelOpacity { get; set; } = 70;

	[Display(Name = "Stop / short color", GroupName = "7. Chart", Order = 650)]
	public CrossColor StopColor { get; set; } = CrossColor.FromRgb(226, 71, 90);

	[Display(Name = "Target / long color", GroupName = "7. Chart", Order = 660)]
	public CrossColor TargetColor { get; set; } = CrossColor.FromRgb(60, 207, 142);

	[Browsable(false)]
	public int DashboardX { get => _dashboard.OffsetX; set => _dashboard.OffsetX = value; }

	[Browsable(false)]
	public int DashboardY { get => _dashboard.OffsetY; set => _dashboard.OffsetY = value; }

	#endregion

	public OpeningRangeBot()
	{
		DenyToChangePanel = true;
		EnableCustomDrawing = true;
		SubscribeToDrawingEvents(DrawingLayouts.Final);

		((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;

		// The validated setup (ES, ~3 years): everything at its default except immediate entry.
		// Market locks it; in-house starts there and can still be changed.
		_s.Entry = RangeEntry.Immediate;
		_engine = new RangeBreakoutEngine(_s);
		_engine.PendingPlaced += OnPendingPlaced;
		_engine.PendingCancelled += OnPendingCancelled;
		_engine.Entered += OnEntered;
		_engine.StopMoved += OnStopMoved;
		_engine.Exited += OnExited;
		_engine.Skipped += (bar, dir, risk) => _lastEvent = $"Skipped {(dir > 0 ? "long" : "short")}: risk ${risk:N0} over limit";
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
		{
			_engine.Reset();
			_gate.Reset();
			_realtime = false;
			_boxes.Clear();
			_trades.Clear();
			CheckChart();
		}

		if (_unsupported != null)
			return;

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
			_lastBarEt = et.ToString("MM.dd.yy HH:mm");
			var raw = new RangeBar(bar - 1, et, k.Open, k.High, k.Low, k.Close, k.Volume, k.Delta);

			if (_agg is null)
			{
				_engine.Process(raw);
				TrackDay(bar - 1, bar - 1, et);
			}
			else
			{
				// Deciding on 15-minute closes from a 5-minute chart: the engine only sees whole blocks.
				foreach (var (block, first) in _agg.Add(raw))
				{
					_engine.Process(block);
					TrackDay(first, block.Index, block.TimeEt);
				}
			}
		}

		// Runs even after the loss cap pauses new entries, so an exit in progress can finish.
		if (_liveTrading && _realtime && bar == CurrentBar - 1)
			ManageLive(bar);
	}

	private bool IsLive => _liveTrading && _realtime && !_killedForDay;

	#region Engine events

	private void OnPendingPlaced(int bar, int dir, decimal price)
	{
		_pendingDir = dir;
		_lastEvent = $"Retest {(dir > 0 ? "buy" : "sell")} limit {price}";

		if (IsLive && StartTrade())
		{
			_entryPrice = price;
			Send(dir > 0 ? OrderDirections.Buy : OrderDirections.Sell, OrderTypes.Limit, price, Contracts, "entry");
		}
	}

	// A fill that lands after this cancel shows up as a position the engine isn't trading,
	// and ManageLive flattens it.
	private void OnPendingCancelled(int bar)
	{
		_lastEvent = "Retest expired";
		CancelAll("entry");
	}

	private void OnEntered(int bar, int dir, decimal price, decimal stop, decimal target)
	{
		_trades.Add(new TradeMark { EntryBar = bar, Dir = dir, Entry = price, Stop = stop, Target = target });

		// Initial risk, kept so R still means something after the stop moves to breakeven.
		_riskTicks = InstrumentInfo.TickSize > 0 ? Math.Abs(price - stop) / InstrumentInfo.TickSize : 0;
		_lastEvent = $"{(dir > 0 ? "Long" : "Short")} {price}";

		// Immediate entries are decided on the bar close; send them now. Retest fills already
		// happened at the broker (that's what told the engine), so nothing to send.
		if (IsLive && _s.Entry == RangeEntry.Immediate && StartTrade())
		{
			_entryPrice = price;
			Send(dir > 0 ? OrderDirections.Buy : OrderDirections.Sell, OrderTypes.Market, 0, Contracts, "entry");
		}
	}

	private void OnStopMoved(int bar, decimal stop)
	{
		_lastEvent = $"Stop moved to {stop}";

		if (_liveTrading && _realtime && NetPosition() != 0)
			MoveBrokerStop(stop);
	}

	private void OnExited(int bar, decimal price, decimal pnl, string reason)
	{
		if (_trades.Count > 0 && _trades[^1].ExitBar < 0)
		{
			var t = _trades[^1];
			t.ExitBar = bar;
			t.Exit = price;
			t.Pnl = pnl;
			t.Reason = reason;
		}

		_lastEvent = $"{reason}: {Money(pnl)}";

		// No orders from here: if the engine closed on a bar close while the bot still holds a
		// position, ManageLive sees "position but not in trade" and runs one guarded flatten.
	}

	#endregion

	#region Live execution

	// Live state comes from the bot's OWN orders, found by their "#trade" comment, never from
	// the account position: that lags every order by a moment and includes other bots and
	// manual trades on the same account. (Reading it caused a flatten loop that built a
	// 279,000-lot replay position.) ATAS also replaces a stop on every modify, so orders are
	// looked up by comment rather than by the object the bot created.
	private void ManageLive(int bar)
	{
		var price = GetCandle(bar).Close;
		var pos = NetPosition();

		// Retest limit filled at the broker.
		if (pos != 0 && _engine.State == RangeDayState.Pending)
			_engine.SyncExternalFill(bar, _entryPrice);

		if (pos != 0)
			_hadPosition = true;

		// Flat again after being in: a stop, target or flatten filled. Clean up and tell the engine.
		if (pos == 0 && _hadPosition)
		{
			_hadPosition = false;
			_flattenRequested = false;
			CancelAll("stop", "target", "entry");

			if (_engine.State == RangeDayState.InTrade)
			{
				var stopHit = FilledOf("stop") > 0;
				_engine.SyncExternalExit(bar, stopHit ? _brokerStop : FilledOf("target") > 0 ? _engine.TargetPrice : price, stopHit ? "Stop" : "Target");
			}

			return;
		}

		if (pos == 0)
			return;

		// Real loss cap on the account, not just the engine's estimate.
		if (!_killedForDay && MaxDailyLoss > 0 && Portfolio is not null && Portfolio.ClosedPnL + Portfolio.OpenPnL <= -MaxDailyLoss)
		{
			_killedForDay = true;
			RequestFlatten(bar, price, "Daily loss cap");
		}

		if (ToEt(GetCandle(bar).Time).TimeOfDay >= _s.HardClose)
			RequestFlatten(bar, price, "Hard close");

		// A position the engine isn't trading (it exited on a bar close, or a late or stray fill).
		if (_engine.State != RangeDayState.InTrade)
			RequestFlatten(bar, price, "Exit");

		if (_flattenRequested)
		{
			ProgressFlatten(pos);
			return;
		}

		// Exits rest at the broker so the trade is protected even if ATAS goes down. Once per trade.
		if (!_bracketSent)
		{
			var exitSide = pos > 0 ? OrderDirections.Sell : OrderDirections.Buy;
			Send(exitSide, OrderTypes.Stop, _engine.StopPrice, Math.Abs(pos), "stop");
			Send(exitSide, OrderTypes.Limit, _engine.TargetPrice, Math.Abs(pos), "target");
			_bracketSent = true;
			_brokerStop = _engine.StopPrice;
			_liveBest = _engine.EntryPrice;
			_liveProtectOn = false;
		}

		// Protect on every tick: a flush is a price event, not a bar-close event.
		if (_s.ProtectAtPct > 0)
			ProtectLive(Math.Sign(pos), price);
	}

	private void RequestFlatten(int bar, decimal price, string reason)
	{
		CancelAll("entry");

		if (_flattenRequested)
			return;

		_flattenRequested = true;
		_flattenAt = DateTime.UtcNow;
		_lastEvent = $"{reason}: flattening";
		CancelAll("stop", "target");

		// Engine first, so nothing re-arms a bracket for a trade that is being closed.
		_engine.SyncExternalExit(bar, price, reason);
	}

	// One market exit at a time, sized to the bot's own position, only once the stop and target
	// are confirmed gone (else one could fill too and flip the position), and only if the
	// account really holds at least that much on the same side.
	private void ProgressFlatten(decimal pos)
	{
		if (AnyWorking("stop", "target", "flatten") || DateTime.UtcNow < _flattenRetryAt)
			return;

		var account = CurrentPosition;

		if (Math.Sign(account) != Math.Sign(pos) || Math.Abs(account) < Math.Abs(pos))
		{
			_lastEvent = $"Flatten waiting: account {account:N0} vs bot {pos:N0}";
			return;
		}

		Send(pos > 0 ? OrderDirections.Sell : OrderDirections.Buy, OrderTypes.Market, 0, Math.Abs(pos), "flatten");
		_flattenAt = DateTime.UtcNow; // an unregistered flatten counts as working for 5s from here
		_flattenRetryAt = DateTime.UtcNow.AddSeconds(2);
	}

	// Net contracts across every order of the current trade: buys filled minus sells filled.
	private decimal NetPosition()
	{
		decimal net = 0;

		foreach (var o in TradeOrders(null))
			net += (o.Direction == OrderDirections.Buy ? 1 : -1) * Filled(o);

		return net;
	}

	private decimal FilledOf(string role)
	{
		decimal sum = 0;

		foreach (var o in TradeOrders(role))
			sum += Filled(o);

		return sum;
	}

	// Active, or just sent and not registered yet (give registration a few seconds).
	private bool AnyWorking(params string[] roles)
	{
		foreach (var role in roles)
		{
			foreach (var o in TradeOrders(role))
			{
				if (o.State == OrderStates.Active || o.State == OrderStates.None && DateTime.UtcNow < _flattenAt.AddSeconds(5))
					return true;
			}
		}

		return false;
	}

	private void CancelAll(params string[] roles)
	{
		foreach (var role in roles)
		{
			foreach (var o in TradeOrders(role))
				Cancel(o);
		}
	}

	private Order ActiveOf(string role)
	{
		foreach (var o in TradeOrders(role))
		{
			if (o.State == OrderStates.Active)
				return o;
		}

		return null;
	}

	// This trade's orders (optionally one role): the ones the bot sent plus anything ATAS lists
	// under the same comment, e.g. the replacement created by a stop modify. Deduped by Id.
	private List<Order> TradeOrders(string role)
	{
		var result = new List<Order>();
		var ids = new HashSet<string>();
		var prefix = $"{OrderTag} ";
		var suffix = $" #{_tradeId}";

		bool Mine(Order o) =>
			o?.Comment is { } c && c.StartsWith(prefix) && c.EndsWith(suffix) && (role is null || c == $"{OrderTag} {role}{suffix}");

		void Add(Order o)
		{
			if (!Mine(o) || result.Contains(o) || !string.IsNullOrEmpty(o.Id) && !ids.Add(o.Id))
				return;

			result.Add(o);
		}

		foreach (var o in _sent)
			Add(o);

		try
		{
			if (TradingManager?.Orders is { } all)
			{
				foreach (var o in all.ToList())
					Add(o);
			}
		}
		catch (InvalidOperationException)
		{
			// The list changed while we read it; the next tick reads it again.
		}

		return result;
	}

	// Filled quantity. An order not yet registered reports Unfilled 0, so only trust it once
	// the broker has it (Active or Done).
	private static decimal Filled(Order o) =>
		o is not null && o.State is OrderStates.Active or OrderStates.Done ? o.QuantityToFill - o.Unfilled : 0;

	private void ProtectLive(int dir, decimal last)
	{
		_liveBest = dir > 0 ? Math.Max(_liveBest, last) : Math.Min(_liveBest, last);

		var trigger = _engine.EntryPrice + (_engine.TargetPrice - _engine.EntryPrice) * _s.ProtectAtPct / 100m;

		if (!_liveProtectOn && (dir > 0 ? _liveBest >= trigger : _liveBest <= trigger))
			_liveProtectOn = true;

		if (_liveProtectOn)
			MoveBrokerStop(_liveBest - dir * _s.ProtectPullbackTicks * InstrumentInfo.TickSize);
	}

	// Modifies the resting stop in place (no moment without a stop) and only ever tightens it.
	// One modify at a time; the next tick retries with the newest price.
	private void MoveBrokerStop(decimal price)
	{
		var tm = TradingManager;
		var old = ActiveOf("stop");
		var tick = InstrumentInfo.TickSize;
		var dir = Math.Sign(NetPosition());

		if (tm is null || old is null || dir == 0 || tick <= 0 || _modifyBusy || _flattenRequested)
			return;

		price = Math.Round(price / tick) * tick;

		if (dir > 0 ? price <= _brokerStop : price >= _brokerStop)
			return;

		var updated = (Order)old.Clone();
		updated.TriggerPrice = updated.Price = price;
		_modifyBusy = true;

		Task.Run(async () =>
		{
			try
			{
				await tm.ModifyOrderAsync(old, updated, false, false);
				_brokerStop = price;
				_lastEvent = $"Stop moved to {price}";
			}
			catch (Exception ex)
			{
				_lastEvent = $"Stop move failed: {ex.Message}";
			}
			finally
			{
				_modifyBusy = false;
			}
		});
	}

	private decimal CurrentPosition => TradingManager?.Position?.Volume ?? 0m;

	private Portfolio Portfolio => TradingManager?.Portfolio;

	// A new trade number, but only when nothing from the last trade is open or working.
	private bool StartTrade()
	{
		if (NetPosition() != 0 || AnyWorking("entry", "stop", "target", "flatten"))
		{
			_lastEvent = "Previous trade still open - entry skipped";
			return false;
		}

		_tradeId = DateTime.Now.ToString("MMddHHmmss");
		_sent.Clear();
		_hadPosition = _bracketSent = _flattenRequested = false;
		_flattenRetryAt = default;
		return true;
	}

	private Order Send(OrderDirections side, OrderTypes type, decimal price, decimal qty, string role)
	{
		var comment = $"{OrderTag} {role} #{_tradeId}";

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

		_sent.Add(order);

		// Never block the calculate thread on the broker call; it can deadlock ATAS.
		Task.Run(async () =>
		{
			try { await tm.OpenOrderAsync(order, false, false, false); }
			catch (Exception ex) { _lastEvent = $"{comment} rejected: {ex.Message}"; }
		});

		return order;
	}

	private void Cancel(Order o)
	{
		var tm = TradingManager;

		if (o is null || o.State != OrderStates.Active || tm is null)
			return;

		Task.Run(async () =>
		{
			try { await tm.CancelOrderAsync(o, false, false); }
			catch (Exception ex) { _lastEvent = $"Cancel failed: {ex.Message}"; }
		});
	}

	// Removed from the chart: no more ticks will arrive, so cancel and exit in one go. The
	// account check still stops it from adding to or flipping a position.
	protected override void OnDispose()
	{
		if (FlattenOnStop && _liveTrading)
		{
			CancelAll("entry", "stop", "target");
			var pos = NetPosition();
			var account = CurrentPosition;

			if (pos != 0 && Math.Sign(account) == Math.Sign(pos) && Math.Abs(account) >= Math.Abs(pos))
				Send(pos > 0 ? OrderDirections.Sell : OrderDirections.Buy, OrderTypes.Market, 0, Math.Abs(pos), "flatten");
		}

		base.OnDispose();
	}

	#endregion

	#region Drawing and time

	private sealed class DayBox
	{
		public DateTime Day;
		public int Start, End, Last;
		public decimal High, Low;
		public bool Skipped;
	}

	private sealed class TradeMark
	{
		public int EntryBar, ExitBar = -1, Dir;
		public decimal Entry, Stop, Target, Exit, Pnl;
		public string Reason;
	}

	// Called once per closed bar, after the engine has seen it.
	private void TrackDay(int first, int bar, DateTime et)
	{
		if (_engine.State == RangeDayState.Waiting)
			return;

		var box = _boxes.Count > 0 && _boxes[^1].Day == _engine.Day ? _boxes[^1] : null;

		if (_engine.State == RangeDayState.Building)
		{
			if (box is null)
				_boxes.Add(box = new DayBox { Day = _engine.Day, Start = first });

			box.End = bar;
			box.High = _engine.High;
			box.Low = _engine.Low;
		}

		if (box is null)
			return;

		box.Skipped = _engine.State == RangeDayState.Skipped;

		if (et.TimeOfDay <= _s.HardClose)
			box.Last = bar;
	}

	private static Color Draw(CrossColor c, int alpha = 255) => Color.FromArgb(alpha, c.R, c.G, c.B);

	private void DrawChart(RenderContext ctx)
	{
		var c = ChartInfo?.PriceChartContainer;

		if (c is null)
			return;

		var first = Math.Max(0, FirstVisibleBarNumber);
		var last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
		var tick = InstrumentInfo.TickSize;

		for (var i = 0; i < _boxes.Count; i++)
		{
			var b = _boxes[i];
			var lineEnd = i == _boxes.Count - 1 && b.Last >= CurrentBar - 2 ? CurrentBar - 1 : b.Last;

			if (lineEnd < first || b.Start > last)
				continue;

			var x1 = Left(c, b.Start);
			var x2 = Left(c, b.End + 1);
			var yHi = c.GetYByPrice(b.High, false);
			var yLo = c.GetYByPrice(b.Low, false);
			var box = new Rectangle(x1, yHi, Math.Max(1, x2 - x1), Math.Max(1, yLo - yHi));
			ctx.FillRectangle(Draw(BoxColor, BoxOpacity), box);
			ctx.DrawRectangle(new RenderPen(Color.FromArgb(255, 120, 120, 120), 1), box);

			var caption = $"{_s.RangeStart:hh\\:mm}-{_s.RangeEnd:hh\\:mm} range · {(b.High - b.Low) / tick:N0}t{(b.Skipped ? " · skipped - outside risk parameters" : "")}";
			var capSize = ctx.MeasureString(caption, _labelFont);
			var capRect = new Rectangle(x1, yHi - capSize.Height - 4, capSize.Width + 6, capSize.Height + 2);
			ctx.FillRectangle(ChartBg, capRect);
			ctx.DrawString(caption, _labelFont, Color.FromArgb(255, 154, 154, 154), capRect.X + 3, capRect.Y + 1);

			// Today's lines run to the right edge with the labels parked there, like the IB indicator.
			var latest = i == _boxes.Count - 1;
			var labelW = ctx.MeasureString("IBH", _tagFont).Width;
			var xEnd = latest ? Container.Region.Right - labelW - 12 : Left(c, lineEnd + 1);
			DrawLevel(ctx, c, "IBH", b.High, x2, xEnd, HighColor, latest);
			DrawLevel(ctx, c, "IBM", (b.High + b.Low) / 2, x2, xEnd, MidColor, latest);
			DrawLevel(ctx, c, "IBL", b.Low, x2, xEnd, LowColor, latest);
		}

		for (var i = 0; i < _trades.Count; i++)
		{
			var t = _trades[i];
			var exitBar = t.ExitBar >= 0 ? t.ExitBar : CurrentBar - 1;

			if (exitBar < first || t.EntryBar > last)
				continue;

			DrawTrade(ctx, c, t, exitBar, i == _trades.Count - 1);
		}
	}

	private void DrawLevel(RenderContext ctx, IChartContainer c, string name, decimal price, int x1, int x2, CrossColor color, bool label)
	{
		var y = c.GetYByPrice(price, false);
		// Today full strength; earlier days faded so they read as context, not signals.
		var alpha = label ? 255 : PastLevelOpacity;

		if (alpha <= 0)
			return;

		ctx.DrawLine(new RenderPen(Draw(color, alpha), 1, DashStyle.Dash), x1, y, x2, y);

		if (label)
			ctx.DrawString(name, _tagFont, Draw(color), x2 + 6, y - ctx.MeasureString(name, _tagFont).Height / 2);
	}

	private void DrawTrade(RenderContext ctx, IChartContainer c, TradeMark t, int exitBar, bool latest)
	{
		var open = t.ExitBar < 0;
		var xs = Center(c, t.EntryBar);
		var xe = Center(c, exitBar);
		var stop = open ? (IsLive && _brokerStop > 0 ? _brokerStop : _engine.StopPrice) : t.Stop;
		var stopColor = Draw(StopColor);
		var targetColor = Draw(TargetColor);

		var yEntry = c.GetYByPrice(t.Entry, false);
		var yStop = c.GetYByPrice(stop, false);
		var yTarget = c.GetYByPrice(t.Target, false);
		ctx.DrawLine(new RenderPen(Color.FromArgb(255, 154, 154, 154), 1), xs, yEntry, xe, yEntry);
		ctx.DrawLine(new RenderPen(stopColor, 2), xs, yStop, xe, yStop);
		ctx.DrawLine(new RenderPen(targetColor, 2), xs, yTarget, xe, yTarget);

		if (_s.ProtectAtPct > 0)
		{
			var yProtect = c.GetYByPrice(t.Entry + (t.Target - t.Entry) * _s.ProtectAtPct / 100m, false);
			ctx.DrawLine(new RenderPen(Draw(TargetColor, 150), 1, DashStyle.Dot), xs, yProtect, xe, yProtect);

			if (latest)
				Label(ctx, $"{_s.ProtectAtPct:0}% · protect", Draw(TargetColor, 180), xe + 6, yProtect);
		}

		if (latest)
		{
			Label(ctx, $"Stop {stop:0.00}", stopColor, xe + 6, yStop);
			Label(ctx, $"Target {t.Target:0.00}", targetColor, xe + 6, yTarget);
		}

		// Entry: small triangle on the side the trade came from, tag beyond it.
		var dirColor = t.Dir > 0 ? targetColor : stopColor;
		var tip = t.Dir > 0 ? yEntry + 4 : yEntry - 4;
		var baseY = t.Dir > 0 ? tip + 8 : tip - 8;
		ctx.FillPolygon(dirColor, new[] { new Point(xs - 5, baseY), new Point(xs + 5, baseY), new Point(xs, tip) });
		Tag(ctx, $"{(t.Dir > 0 ? "Long" : "Short")} {t.Entry:0.00}", dirColor, xs, t.Dir > 0 ? baseY + 3 : baseY - 3, t.Dir < 0);

		if (open)
			return;

		var win = t.Pnl > 0;
		var exitColor = win ? targetColor : stopColor;
		var yExit = c.GetYByPrice(t.Exit, false);
		ctx.FillEllipse(exitColor, new Rectangle(xe - 4, yExit - 4, 8, 8));

		// Exit tag goes on the far side from the entry so it never sits on the entry tag.
		var below = yExit >= yEntry;
		Tag(ctx, $"{(t.Pnl >= 0 ? "+" : "-")}${Math.Abs(t.Pnl):N0} {t.Reason.ToLowerInvariant()}", exitColor, xe, below ? yExit + 10 : yExit - 10, !below);
	}

	// Bar edges from consecutive bar starts, so this doesn't depend on what GetXByBar's flag means.
	private static int Left(IChartContainer c, int bar) => c.GetXByBar(bar, true);

	private static int Center(IChartContainer c, int bar) => (Left(c, bar) + Left(c, bar + 1)) / 2;

	private void Label(RenderContext ctx, string text, Color color, int x, int y)
	{
		var size = ctx.MeasureString(text, _labelFont);
		var rect = new Rectangle(x + 2, y - size.Height / 2 - 1, size.Width + 4, size.Height + 2);
		ctx.FillRectangle(ChartBg, rect);
		ctx.DrawString(text, _labelFont, color, rect.X + 2, rect.Y + 1);
	}

	// Pill: tinted fill, thin border, text centred on x. 'above' grows the pill upward from y.
	private void Tag(RenderContext ctx, string text, Color color, int x, int y, bool above)
	{
		var size = ctx.MeasureString(text, _tagFont);
		var w = size.Width + 10;
		var h = size.Height + 4;
		var rect = new Rectangle(x - w / 2, above ? y - h : y, w, h);
		ctx.FillRectangle(ChartBg, rect);
		ctx.FillRectangle(Color.FromArgb(45, color.R, color.G, color.B), rect);
		ctx.DrawRectangle(new RenderPen(color, 1), rect);
		ctx.DrawString(text, _tagFont, Lighten(color), rect.X + 5, rect.Y + 2);
	}

	// Near-opaque chart background behind tags and captions so candles don't show through.
	private static readonly Color ChartBg = Color.FromArgb(235, 20, 24, 34);

	// Signed dollars: +$1,234 / -$917 / $0.
	private static string Money(decimal v) => v.ToString("+$#,0;-$#,0;$0");

#if MARKET
	private const string Title = "Opening Range Bot";
#else
	private const string Title = "Opening Range Bot (in-house)";
#endif

	private static Color Lighten(Color c) => Color.FromArgb(255, (c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);

	private DateTime ToEt(DateTime time) =>
		_chartTimeIsUtc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), Eastern) : time;

	// Runs at bar 0 of every calculation. In-house: notes the chart, never refuses it. Market:
	// supported instruments and timeframes only, with the instrument's tick value, commission and
	// default daily loss applied, so it works out of the box. NQ/MNQ join once validated.
	private void CheckChart()
	{
		var symbol = (InstrumentInfo?.Instrument ?? "").TrimStart('#').ToUpperInvariant();
		var minutes = 0d;

		for (var i = 1; i < Math.Min(CurrentBar, 300); i++)
		{
			var gap = (GetCandle(i).Time - GetCandle(i - 1).Time).TotalMinutes;

			if (gap > 0 && (minutes == 0 || gap < minutes))
				minutes = gap;
		}

		_chartLabel = $"{symbol} · {minutes:0}m";
		_unsupported = null;

		// Instrument profiles. ES from the ~3-year ES test, NQ from the NQ test (range-breakout-log.md).
		(string Name, decimal TickValue, decimal Commission, int MinRange, int MaxRange, decimal DailyLoss)? profile =
			symbol.StartsWith("MES") ? ("MES", 1.25m, 1m, 8, 80, 50m)
			: symbol.StartsWith("ES") ? ("ES", 12.5m, 4m, 8, 80, 500m)
			: symbol.StartsWith("MNQ") ? ("MNQ", 0.5m, 1m, 32, 600, 150m)
			: symbol.StartsWith("NQ") ? ("NQ", 5m, 4m, 32, 600, 1500m)
			: null;

#if MARKET
		if (profile is null)
		{
			_unsupported = $"Unsupported instrument {symbol}. Use ES, MES, NQ or MNQ.";
			return;
		}

		// 5m or 15m charts; NQ/MNQ always decide on 15-minute closes (their validated setup).
		if (minutes is not (5 or 15))
		{
			_unsupported = $"Unsupported timeframe ({minutes:0}m). Use a 5- or 15-minute chart.";
			return;
		}
#endif

		// In-house falls back to the ES profile on other symbols; every value can be overridden.
		var p = profile ?? ("ES defaults", 12.5m, 4m, 8, 80, 500m);
		_profile = p.Name;
		_s.TickValue = _userTickValue > 0 ? _userTickValue : p.TickValue;
		_s.Commission = _userCommission > 0 ? _userCommission : p.Commission;
		_s.MinRangeTicks = _userMinRange > 0 ? _userMinRange : p.MinRange;
		_s.MaxRangeTicks = _userMaxRange > 0 ? _userMaxRange : p.MaxRange;
		_s.MaxDailyLoss = _userDailyLoss > 0 ? _userDailyLoss : p.DailyLoss;
		// Decision bars: NQ/MNQ were validated on 15-minute closes (on 5m closes NQ tested PF 1.04 vs
		// 1.21), ES/MES on the chart's own 5m or 15m bars. A 5m chart deciding on 15m feeds the engine
		// grouped blocks, which reproduces the 15m chart exactly (range-breakout-log.md).
		var fifteen = _decision == DecisionBars.FifteenMinute || _decision == DecisionBars.Auto && p.Name.EndsWith("NQ");
		_agg = fifteen && minutes == 5 ? new BarAggregator(15, 5) : null;
		_chartLabel = _agg != null
			? $"{symbol} · 5m chart · trades on 15m closes · {p.Name} profile"
			: $"{symbol} · {minutes:0}m · {p.Name} profile";
	}

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
		if (ChartInfo is null || Container is null || InstrumentInfo.TickSize <= 0)
			return;

		DrawChart(context);

		if (!ShowDashboard)
			return;

		var e = _engine;
		var mode = !_liveTrading ? "SIMULATION (no orders)" : _killedForDay ? "LIVE - paused (loss cap)" : _realtime ? "LIVE" : "LIVE - waiting for ticks";

		_rows.Clear();
		_rows.Add(new PanelRow("Mode", mode, _liveTrading ? Palette.Down : Palette.Muted));
		_rows.Add(new PanelRow("Chart", _chartLabel, Palette.Muted));

		if (_unsupported != null)
		{
			_rows.Add(new PanelRow("Not running", _unsupported, Palette.Down));
			_dashboard.Render(context, Container.Region, Title, _rows);
			return;
		}

		_rows.Add(new PanelRow("Bar time ET", _lastBarEt, Palette.Muted));
		_rows.Add(new PanelRow("Today", e.State.ToString(), e.State == RangeDayState.InTrade ? Palette.Of(e.Direction) : Palette.Muted));

		if (e.State is not (RangeDayState.Waiting or RangeDayState.Building))
			_rows.Add(new PanelRow("Range", $"{e.Low} - {e.High} ({(e.High - e.Low) / InstrumentInfo.TickSize:N0}t)", Palette.Muted));

		if (e.State == RangeDayState.Pending)
			_rows.Add(new PanelRow("Retest limit", $"{(_pendingDir > 0 ? "Buy" : "Sell")} {e.PendingPrice:0.00}", Palette.Of(_pendingDir)));

		if (e.State == RangeDayState.InTrade)
			AddTradeRows(e);

		if (_liveTrading)
			_rows.Add(new PanelRow("Position (bot)", NetPosition().ToString("N0"), Palette.Of(NetPosition())));

		_rows.Add(new PanelRow("Last event", _lastEvent, Palette.Muted));
		_rows.Add(new PanelRow("Day P&L (sim)", Money(e.DayPnl), Palette.Of(e.DayPnl)));
		_rows.Add(new PanelRow("Trades", e.Trades.ToString(), Palette.Muted));
		_rows.Add(new PanelRow("Win rate", e.Trades == 0 ? "-" : $"{100m * e.Wins / e.Trades:N0}%", Palette.Muted));
		_rows.Add(new PanelRow("Profit factor", e.GrossLoss == 0 ? "-" : (e.GrossWin / e.GrossLoss).ToString("N2"), e.GrossWin >= e.GrossLoss ? Palette.Up : Palette.Down));
		_rows.Add(new PanelRow("Net", Money(e.Net), Palette.Of(e.Net)));
		_rows.Add(new PanelRow("Avg trade", e.Trades == 0 ? "-" : Money(e.Net / e.Trades), Palette.Of(e.Net)));
		_rows.Add(new PanelRow("Max drawdown", $"{e.MaxDrawdown:$#,0}", e.MaxDrawdown > 0 ? Palette.Down : Palette.Muted));

		_dashboard.Render(context, Container.Region, Title, _rows);
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
		var live = IsLive && _brokerStop > 0;
		var stop = live ? _brokerStop : e.StopPrice;
		_rows.Add(new PanelRow(live ? "Stop (broker)" : "Stop", $"{stop:0.00} ({(stop - e.EntryPrice) / tick:+0;-0;0}t)", Palette.Down));
		_rows.Add(new PanelRow("Target", $"{e.TargetPrice:0.00} ({(e.TargetPrice - e.EntryPrice) / tick:+0;-0;0}t)", Palette.Up));

		if (_s.ProtectAtPct > 0)
		{
			var trigger = e.EntryPrice + (e.TargetPrice - e.EntryPrice) * _s.ProtectAtPct / 100m;
			_rows.Add(e.ProtectOn || live && _liveProtectOn
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
