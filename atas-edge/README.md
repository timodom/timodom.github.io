# Edge suite for ATAS

Clean-room ATAS (C#) versions of the six HolyGrailAlgo NinjaTrader tools. HGA doesn't publish its formulas, so this is not a port of their code. Each tool rebuilds the behavior from the feature list and adds what NinjaTrader versions usually can't do well: real bid/ask delta, signals that don't repaint, and a neutral "chop" state.

| HGA tool | Edge equivalent | What's different |
|---|---|---|
| Cumulative Delta | `Edge Cumulative Delta` | Uses ATAS's exchange bid/ask delta rather than an uptick/downtick estimate. Draws CVD candles with an EMA trigger line, can reset each session, and marks confirmed price/CVD divergences. |
| Trendband | `Edge Trend Band` | An ATR band around an EMA with **three** states: bull above, bear below, and neutral (chop) inside. Candles get a brighter shade when the bar's delta agrees with the trend. |
| Long-Short Signal | `Edge Long-Short Signals` | Fires on **closed bars only**, so arrows never repaint. A signal needs price to close back outside the band, bar delta in the same direction, and CVD on the same side of its EMA. Each signal plots a suggested stop, and the dashboard can be dragged. |
| Dashboard w/ trailing target | `Edge Trail Manager` (strategy) | Moves to breakeven, trails by ticks or ATR (the stop only ratchets one way), can take a fixed target, and has a daily loss cap. It shows a draggable dashboard. It only exits trades and never opens them. |
| Channel Breaks | `Edge Channel Breaks` | Uses a Donchian channel. Breakouts need above-average volume and delta in the breakout direction. Fades need a wick through the channel edge and a close back inside. Choose Breakouts, Fades or Both. The dashboard shows distance to the channel edges. |
| Strength | `Edge Strength` | Band **color** shows direction and force (smoothed RSI). Band **width** shows trend efficiency, so it widens in clean trends and narrows in chop. Bull/Bear prints mark confirmed swings that were RSI-extreme or RSI-divergent. The dashboard shows RSI, strength and efficiency. |

**New: `Edge Range Breakout Bot`**: an opening range / initial balance breakout bot for ES on 5-minute bars. With live trading off (the default) it simulates trades and shows backtest stats. With it on, it places real orders, each with a stop and a target resting at the broker. It's built on `Indicator` (ATAS 8 has no UI for `ChartStrategy`), so it appears in the Indicators list. Spec: `docs/range-breakout-spec.md`. Status, changelog and open items: `docs/range-breakout-log.md`.

All panels can be dragged with the mouse, and their position is saved with the chart template. Every signal has an optional ATAS alert.

## Build and install

You need Windows with ATAS installed, plus the .NET SDK that matches your ATAS build (current ATAS uses .NET 10; change `TargetFramework` in both `.csproj` files if yours differs).

```
cd atas-edge\Edge.Indicators && dotnet build -c Release
cd ..\Edge.Strategies && dotnet build -c Release
```

The build copies the DLLs to `Documents\ATAS\Indicators` and `Documents\ATAS\Strategies`; the strategy project also copies to `%APPDATA%\ATAS\Indicators`, which is where ATAS 8 loads custom DLLs from. Restart ATAS (or reload indicators), then add the tools from the **Edge** category. If ATAS isn't installed in the default folder, pass `-p:ATAS_BASE="D:\path\to\ATAS"`.

## How signals avoid repainting

ATAS recalculates the live bar on every tick. The tools evaluate a bar only after it has closed (see `ClosedBarGate`), so an arrow that appears stays where it is. Pivot-based marks (divergences, prints) need `strength` closed bars on each side of the swing. That means they show up a few bars late, on the bar where the swing actually was. The delay is the cost of not repainting.

## Trail Manager: read before going live

- It works on whatever position the chart's account has in that instrument, including trades you entered by hand. (This rests on how ATAS's `ChartStrategy.CurrentPosition` behaves, so confirm it in sim.)
- The trailing stop is virtual. The strategy holds the level and sends a market order when price touches it. If ATAS or your connection goes down, it can't exit. **Always keep a hard stop at the broker** (an ATM or order template) as your disaster stop.
- The daily loss cap flattens positions but can't block new manual orders.
- Run it in the ATAS simulator first.

## Layout

```
Edge.Indicators/
  Core/BarMath.cs            EMA, Wilder RMA/ATR/RSI. Values are stored per bar, so re-running the live bar is safe
  Core/Calcs.cs              Trend band, CVD, pivots, closed-bar gate. Shared by the indicators
  Core/DragPanel.cs          Draggable dashboard rendering and shared colors
  Core/DashboardIndicator.cs Base class: dashboard + mouse dragging + saved position
  Edge*.cs                   One file per tool (visual output and settings only)
Edge.Strategies/
  EdgeTrailManager.cs        Execution and risk (links the Core files in at compile time)
  EdgeRangeBreakoutBot.cs    Range breakout bot: chart, dashboard, live orders (engine in Core/RangeBreakoutEngine.cs)
tests/
  RangeBreakoutEngine.Tests  Synthetic-bar checks for the engine (dotnet run, no ATAS needed)
```

Calculation lives in `Core/` and display lives in the indicator files. That way Signals reuses the exact Trend Band and CVD math, and you don't need those indicators loaded on the chart.
