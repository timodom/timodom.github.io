# Edge Bar Export: research guide and idea backlog

A small tool for testing trading ideas on years of real bars before building anything. It pairs an ATAS indicator that dumps the chart's bars to a CSV with an offline harness that replays them.

## What it is

`Edge Bar Export (research)` is an ATAS indicator in the **Edge** category (`atas-edge/Edge.Research/EdgeBarExport.cs`, deploys to `%APPDATA%\ATAS\Indicators`). Once the chart's history has loaded, it writes every loaded bar to:

```
Documents\ATAS\EdgeResearch\<instrument>-<bars>.csv
```

Columns: `time,open,high,low,close,volume,delta`. It draws nothing and places no orders. Times are the chart's own times, which are UTC on this setup. ATAS shows an alert with the bar count and the file path when the export finishes, or the error if it fails.

## How to use it

1. Open a chart on the **continuous** contract (for example `ES`), never a single month like `ESZ6`. Back months trade thinly before the roll, so their history is misleading.
2. Pick any timeframe or chart type: time bars, range, Renko and so on all work.
3. Add `Edge Bar Export (research)` and wait for history to load. The CSV appears in `Documents\ATAS\EdgeResearch`.
4. To export again (for example after loading more days), tick **Export now**.
5. Remove the indicator when you're not testing. It rewrites the file every time the chart reloads.

## Known limits (this PC's data server)

- **15-minute** history goes back to **2023-12-17**. Asking for 1,500 or 3,000 days gives the same start.
- **5-minute** history covers about one year (from **2025-11-09**).
- Bars only: no DOM, no MBO, no footprint per price. The `delta` column is the bar's total bid/ask delta.

## The offline harness

`atas-edge/tests/RangeResearch` reads an exported CSV and runs the range bot's own engine over it:

```
dotnet run --project atas-edge/tests/RangeResearch -c Release -- <csv> [--from yyyy-MM-dd]
```

- It reproduced the ATAS dashboard numbers exactly, so results match what the bot would do on the chart.
- Each row shows a train/test split and a t-statistic of the mean trade P&L. Roughly, |t| above 2 means the result is unlikely to be luck.
- `--from` starts the test at a date, which is handy for checking a stretch you didn't use to pick the idea.
- First result: on about 3 years of 15m continuous ES, ORB-15 with immediate entry made money (509 trades, PF 1.30, t 2.6). The retest entry lost money.

> **Lesson: confirm on data you didn't use to pick the idea.**
> On 2026 data alone a wide-IB rotation idea looked great (PF 2.0). On the two earlier years it had never seen, it failed badly (PF 0.44, t −3.7). Always confirm on the longest history available, and on data not used to choose the idea.

## Idea backlog

Each idea has one line on how to test it.

- **(a) Test any rule before building a bot.** Write the rule into the harness (or a copy of it), run it on the longest 15m export, and only build the ATAS version if train and test both hold up.
- **(b) Re-test existing bots: Break & Retest, Momentum Scalper, RAIN.** Port each one's entry/exit logic into the harness and run 3 years of 15m ES. The WendyAI vault records RAIN at about −0.3R and still under validation, so this would settle it.
- **(c) Other markets (recommended first).** Export continuous NQ, CL, GC and RTY on 15m and run the winning immediate-entry ORB-15 setup unchanged. If it holds on several markets, the edge is more likely real.
- **(d) Stats for the morning trade plan (`/build-trade-plan`) and the ZeroDayTrades site.** From one 15m ES export, compute average daily range, how often the IB breaks and on which side, the time of day of the high and low, and the overnight gap fill rate.
- **(e) Level studies.** Mark prior-day high/low, overnight high/low, VWAP (from the bar data) and round numbers, then count touches, breaks and reversals at each and the move that follows.
- **(f) Order-flow ideas using the bar delta column.** Test rules such as "breakout bar with delta in the breakout direction" or "price/delta divergence at a level" against the same trades without the delta filter.
- **(g) Range/Renko chart strategies.** Export a range or Renko chart instead of time bars and run the same rule; compare with the time-bar result.
