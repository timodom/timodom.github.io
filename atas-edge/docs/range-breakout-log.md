# Edge Range Breakout Bot: project log

Running record of what was built, what's verified, what went wrong and what's still open.
Rules live in `range-breakout-spec.md`; this file tracks the work.

**Locked version:** tag `range-bot-v0.2` on branch `atas-edge-suite`.

## Status (2026-10-24)

| Area | State |
|---|---|
| Loads in ATAS 8.0.15 (.NET 10) | Verified on the owner's PC |
| Simulation, backtest stats, dashboard | Verified on chart |
| Chart drawing (IB box, IBH/IBM/IBL, trade tags) | Verified on chart |
| Protect exit (engine) | 7/7 synthetic-bar tests pass (`atas-edge/tests/RangeBreakoutEngine.Tests`) |
| Live entry limit placement | Verified in Market Replay: registers 0.02–0.3 s after the breakout bar closes |
| Live order handling after the 2026-10-23 incident fix (`f81d05b`) | **Not yet run against an account.** Audit a Replay session before any live use |
| Edge: an actual edge | **Not shown.** 200-day backtest: 53 trades, PF 0.88, −$2,587; no tested variant clearly better (see Backtest 2026-10-24) |

**Do not run Live Trading on a real or shared account** until a Replay session on a non-skipped
day has been audited (see "Replay audit" below).

## Backtest 2026-10-24 (ES 5m, 200 days loaded, ATAS simulation)

Run in ATAS on the normal feed (not Replay), one setting changed at a time from the defaults.
Max drawdown is peak-to-trough of cumulative simulated P&L, commissions included.

| Test | Change from defaults | Trades | Win % | PF | Net | Max DD |
|---|---|---|---|---|---|---|
| Baseline | none (max range 80, opposite-edge stop, 1.5R, Protect 80%) | 53 | 42% | 0.88 | −$2,587 | $9,017 |
| A | Max range 40 | 10 | 50% | 1.08 | +$148 | $1,429 |
| B | Target 1.0R | 53 | 47% | 0.77 | −$4,437 | $8,237 |
| C | Delta filter on | 47 | 43% | 0.89 | −$2,001 | $8,302 |
| D | Protect off | 53 | 40% | 0.90 | −$2,150 | $10,067 |
| E | Midpoint stop | 70 | 36% | 0.78 | −$4,755 | $6,881 |

**Reading.** No variant shows an edge. Baseline is just under break-even on win rate (~42% needed
at 1.5R after costs) and its drawdown is 18× the $500 daily cap. A (max 40) is the only positive
row but leaves 10 trades in ~140 sessions because most 09:30–09:45 ES ranges in this period were
wider than 40 ticks — too few to mean anything. B and E trade risk for hit rate and lose more.
C and D are within noise of baseline. Settings were restored to defaults afterwards and Live
Trading was left OFF.

## Research run 2026-10-24 (offline harness on exported bars)

Tools: `Edge.Research` (bar exporter indicator) and `tests/RangeResearch` (offline harness running
the bot's own engine). The harness reproduced the ATAS dashboard baseline exactly (53 trades, 42%,
PF 0.88, −$2,587, max DD $9,017), so its engine numbers are the bot's numbers.

**Data caveat (important).** The chart is ESZ6. It only became the traded ES contract at the
roll: daily volume jumped from ~12k to ~844k contracts on **2026-09-14**. Before that the "200
days" are thin back-month prints (all of August: ~22k contracts vs 18.5M in September), so every
backtest on this chart so far mixed ~6 weeks of real ES with months of unrepresentative data.

| Variant (engine) | Full file: trades / win / PF / net | Liquid only (30 days from 2026-09-14) |
|---|---|---|
| Baseline ORB-15 retest | 53 / 42% / 0.88 / −$2,587 | 17 / 41% / 0.85 / −$993 |
| ORB-15 **immediate** entry | 78 / 56% / 1.45 / +$13,238 (train 1.48, test 1.35) | 24 / 58% / 1.62 / +$5,029 (train 2.78, **test 0.59**) |
| IB-30 retest / immediate | PF 0.72 / 1.00 | PF 0.69 / 0.85 |
| IB-60 retest / immediate (max 160t) | PF 1.05 / 1.06, DD $11–13k | PF 0.72 / 1.05, DD $6.8–7.8k |
| ORB-5 immediate (mid-IB momentum) | PF 0.95 | PF 1.09 (train 1.72, test 0.38) |
| ORB-10 immediate (mid-IB momentum) | PF 1.20 | PF 1.30 (train 2.45, test 0.41) |
| Rotation, fade first failed IB probe | IB-15/30/60, IBM or half target: PF 0.54–1.00 overall; no bucket positive in both halves with >10 trades | 13 trades, PF 0.73 |

**Reading.** Immediate entry beat retest in every comparison on the full file and over the clean
30 days as a whole, consistent with the retest limit being filled mainly by failing breakouts.
But 30 real days give 6–24 trades per row and results flip between train and test, so **nothing
here is statistically established**. Wide-IB rotation, as tested, shows no support yet. Next
step: export the earlier contracts (ESU6 for ~June–Sep 11, ESM6 before that) and stitch them by
roll date to get ~200 genuine days, then rerun.

## Install

- Build: `dotnet build atas-edge\Edge.Strategies -c Release`
- The build copies `Edge.Atas.Strategies.dll` to `Documents\ATAS\Strategies` (older ATAS) and
  `%APPDATA%\ATAS\Indicators` (ATAS 8.x loads all custom DLLs, strategies included, from there).
- Reload indicators in ATAS (status bar) or restart it. Find the bot in Indicators, group **Edge**.

## Current defaults

| Setting | Default | Notes |
|---|---|---|
| Live trading | OFF | |
| Range | 09:30–09:45 ET (ORB-15) | Owner decision: keep 15 min |
| Entry | Retest, 1 tick past edge, 6-bar timeout | |
| Stop / target | Opposite edge / 1.5R | Risk per trade = range width |
| Min / max range | 8 / 80 ticks | 80t = $1,000 risk on ES; consider ~40 |
| Protect | 80% of target, then trail 6 ticks behind best price | Live: tick by tick at the broker |
| Max trades / losers / daily loss | 2 / 2 / $500 | Daily loss reads the whole account's P&L |
| Previous days IB opacity | 70 / 255 | 0 hides |

## Changelog

| Commit | Change |
|---|---|
| `89e9808` | Starting point: live-capable bot written without the real ATAS DLLs |
| `3a1067b` | Renamed `Stop` setting property to `StopMode` (hid `ChartStrategy.Stop()`); first clean build |
| `649df39` | Build also deploys to `%APPDATA%\ATAS\Indicators`, where ATAS 8 loads custom DLLs |
| `a132c5d` | Bot now derives from `Indicator`, not `ChartStrategy` (ATAS 8 has no UI for chart strategies); orders via `TradingManager` |
| `7c678ad` | Dashboard: side, entry, stop, target, live open P&L (ticks, $, R), retest limit price |
| `1695a24` | **Protect exit** in the engine (owner-approved engine change): after 80% of target, stop trails 6 ticks behind best price |
| `83b2a62` | Live: protect trails the broker stop tick by tick via `ModifyOrderAsync` |
| `7eaa80a` | Chart redesign: grey range box (High Probability Levels grey 158,158,158 @ 60), dashed IBH/IBM/IBL, small entry/exit tags, trade-span lines |
| `ec4bef8` | Box width and bar centers fixed; tags readable over candles; IB labels at the right edge |
| `d16a5a5` | Dark backing on stop/target/protect labels |
| `f81d05b` | **Incident fix**: live state from the bot's own tagged orders, single guarded flatten (see below) |
| `27030ed` | IB lines drawn on skipped days too |
| `d6b143c` | Skipped caption: "skipped - outside risk parameters" |
| `f44ce48` | Previous days' IB lines fade (setting) |

## Incident 2026-10-23: flatten loop on the Replay account

**What happened.** At the 15:55 hard close the bot flattened. On the next tick the account
position had not updated yet and the engine still said InTrade, so the bot re-sent the stop and
target and flattened again, every tick. Each flatten used the stale account size, so the position
doubled back and forth, peaking at **+279,000 / −17,540 contracts** (193 stops, 193 targets, 188
flattens, 76 flatten fills) between 22:28 and 22:34. The tick protect also kept modifying a stop
that had already filled ("Can't cancel order … with state Done", 294 errors in a later session).
Only the Market Replay account was affected; DEMO23817 was not touched.

**Root cause.** All live logic read the account position (`TradingManager.Position`), which lags
every order and includes other bots and manual trades. ATAS also replaces a stop on every modify,
so the original order object stops being the live one.

**Fix (`f81d05b`).**
- Each trade gets an id; orders are tagged `Edge ORB <entry|stop|target|flatten> #<id>` and found
  by tag in `TradingManager.Orders`.
- Bot position = net fills across that trade's orders only.
- Stop and target are sent once per trade; stop modifies target the active stop only.
- Flatten: engine told first, stop/target cancels confirmed, then ONE market exit for the bot's own
  size, only if the account holds at least that much on the same side (can never add or flip);
  2-second retry throttle on rejects.
- Late entry fills after a retest expires are flattened, not adopted.
- No new trade while the previous one has a position or working orders.

## Replay audit (do before any live use)

1. Reload ATAS; the dashboard must show "Position (bot)".
2. Market Replay, chart account **Replay**, Live Trading ON, a date whose 09:30–09:45 range is
   under Max range (otherwise the day is skipped and nothing is tested).
3. Start before 09:30, run past 15:55.
4. Check `%APPDATA%\ATAS\Logs\app_<date>.log`: per `#<id>`, one entry, one stop, one target,
   at most one flatten; no "Order modify failed"; Replay position never beyond the bot's contracts
   and flat at the end.

## Open items

- Replay audit of `f81d05b` (above).
- Lower Max range to ~40 ticks so one loss stays near the $500 daily cap.
- Backtest 3+ months with Protect on; a few hundred trades before judging the edge.
- Optional: let the bar that stops out a trade also count as the reverse breakout (owner deferred).
- Optional: ATR-style trail and a per-trade log/CSV (MAE/MFE, duration, R) (owner deferred).
- Other Edge indicators (`Edge.Indicators` project) don't compile against ATAS 8: `Parameter` is
  ambiguous between `OFT.Attributes` and `ATAS.Indicators`. Pre-existing; the bot doesn't use them.
- `Edge Trail Manager` is still a `ChartStrategy`, so ATAS 8 won't list it.
