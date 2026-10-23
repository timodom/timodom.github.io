# Range Breakout Engine — Build Spec

**Status:** Spec only. Do not build until the manual test below says the idea is worth it.
**Platform:** ATAS (C#), `ChartStrategy`. Lives in `atas-edge/`, reuses `Core/` and `EdgeTrailManager` logic.
**Market:** MNQ (micro Nasdaq futures), intraday only.

## Why this, not the others

We looked at HolyGrailAlgo (indicators), Einfach Traden TMS-IBS (Initial Balance bot) and TMS-ORB (Opening Range bot). IBS and ORB are the same strategy with a different window. One engine with a configurable window replaces both (€80/month) and has fixed rules we can test.

References: https://marketplace.atas.net/product/tms-orb · https://einfachtraden.com/en/tms-ibs

## Rules

### 1. Range
- Session window `RangeStart`–`RangeEnd`, exchange time (ET). Range = high/low of bars inside the window.
- Presets: **ORB 15** = 09:30–09:45 (default). **IB** = 09:30–10:30.
- Skip the day if range size < `MinRangeTicks` or > `MaxRangeTicks`.

### 2. Entry (after `RangeEnd`, before `NoNewEntriesAfter`)
- Breakout = a bar **closes** beyond the range high (long) or low (short). Evaluated on closed bars only (`ClosedBarGate`) so nothing repaints.
- `EntryMode`:
  - `Immediate`: market order on the breakout close.
  - `Retest` (default): limit order at the broken edge ± `RetestOffsetTicks`. Cancel if not filled within `RetestTimeoutBars`.
- One position at a time. Max `MaxTradesPerDay` (default 2).

### 3. Filters (each can be toggled; keep the count low to avoid curve-fitting)
| Filter | Rule |
|---|---|
| Volume | Breakout bar volume ≥ `VolumeMult` × average volume of the range bars |
| Delta | Breakout bar delta in the trade direction |
| VWAP | Long only above session VWAP, short only below |
| Range size | Min/max ticks, as in Rules 1 |

### 4. Exits
- Stop: `StopMode` = opposite range edge / range midpoint / fixed ticks.
- Target: `TargetMode` = R-multiple (default 1.5R) / range-size multiple / fixed ticks.
- Breakeven and trailing: reuse `EdgeTrailManager` logic.
- `HardCloseTime` (default 15:55 ET): flatten everything. Never hold overnight.

### 5. Daily risk
- `MaxDailyLoss` (account currency) and `MaxLosingTradesPerDay` (default 3), then stop for the day.
- `Contracts`: fixed quantity (no dynamic sizing in v1).

### 6. Safety
- `LiveTrading` **off by default**. When off, the engine simulates fills on the chart (backtest mode).
- Real orders only when `LiveTrading` is on. Always place a broker-side hard stop with every entry.

### 7. Display
- Draw the range box, the breakout/retest levels, entries and exits on the chart.
- Draggable dashboard (reuse `DashboardIndicator`/`DragPanel`) showing: range high/low/size, today's state (waiting / armed / in trade / done), trades today, day P&L.
- Backtest stats over loaded history: trades, win rate, profit factor, net P&L (ticks and $), max drawdown, average trade.

## Build order
1. Range plus breakout markers only (indicator, no orders). Check visually on MNQ.
2. Simulated fills and the backtest panel.
3. Filters.
4. Live order routing (sim account first).

## Before writing any code: manual test
Use ATAS Market Replay on MNQ: ORB-15, retest entry, stop at the opposite edge, target 1.5R, no filters. Log 30–50 trades. Continue only if the results (with and without the delta filter) show a real edge after commissions.

## Open decisions (owner decides)
- [ ] ORB 15 or IB 60 as the primary window
- [ ] Retest vs immediate entry as the default
- [ ] Contracts per trade and daily loss limit
- [ ] Where the code lives: this public Pages repo, or a new private repo (recommended)
