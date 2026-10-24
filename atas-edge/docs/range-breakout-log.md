# Edge Range Breakout Bot: project log

Running record of what was built, what's verified, what went wrong and what's still open.
Rules live in `range-breakout-spec.md`; this file tracks the work.

**Locked version:** tag `range-bot-v0.3` on branch `atas-edge-suite` (2026-10-24). Validated setup: all defaults with **Entry = Immediate** (ES 15m, 709 sessions: 509 trades, PF 1.30, +$57,314, t 2.6; confirmed on the ATAS dashboard). Note: the code default for Entry is still Retest; set Immediate in the bot's settings. Earlier lock: `range-bot-v0.2`.

## Status (2026-10-24)

| Area | State |
|---|---|
| Loads in ATAS 8.0.15 (.NET 10) | Verified on the owner's PC |
| Simulation, backtest stats, dashboard | Verified on chart |
| Chart drawing (IB box, IBH/IBM/IBL, trade tags) | Verified on chart |
| Protect exit (engine) | 7/7 synthetic-bar tests pass (`atas-edge/tests/RangeBreakoutEngine.Tests`) |
| Live entry limit placement | Verified in Market Replay: registers 0.02–0.3 s after the breakout bar closes |
| Live order handling after the 2026-10-23 incident fix (`f81d05b`) | **Not yet run against an account.** Audit a Replay session before any live use |
| Edge: an actual edge | Current bot: **no** (continuous ES 193 days, PF 0.92, test half 0.53). **ORB-15 with immediate entry**: PF ≈ 1.3, t ≈ 2.4–2.6 over ~3 years of 15m ES incl. 2 unseen years (run 4). Regime/rotation idea did NOT survive out of sample |

**Do not run Live Trading on a real or shared account** until a Replay session on a non-skipped
day has been audited (see "Replay audit" below).

## Decision bars: NQ on a 5m chart (2026-10-24)

Owner-approved design: NQ/MNQ decide on 15-minute closes even on a 5-minute chart (NQ on 5m
closes: PF 1.04, max DD $38,228 vs 15m PF 1.21, $20,719, same 238 days). `Core/BarAggregator.cs`
groups 5m bars into clock-aligned 15m blocks (:00/:15/:30/:45; emitted when the :10/:25/:40/:55 bar
closes); the bot and the harness (`--aggregate 15`) share it.

Check (NQ, 600t, $1,500 cap, $4 commission, 2025-11-09 .. 2026-10-23):

| Input | Bars | Trades | Win | PF | Net | Max DD |
|---|---|---|---|---|---|---|
| Real 15m file (`NQ-67287.csv --from 2025-11-09`) | 22,600 | 148 | 51% | 1.21 | +$35,078 | $20,719 |
| 5m file grouped to 15m (`NQ-67797.csv --aggregate 15`) | 22,600 | 148 | 51% | 1.21 | +$35,078 | $20,719 |
| 5m file, 5m decisions | 67,797 | 153 | 48% | 1.04 | +$6,713 | $38,228 |

Exact match. Stops and targets in the simulation are checked on the 15m block (stop first when
both are touched), as in the harness; live, the broker's resting orders and the tick-by-tick
Protect act intrabar either way. Retest timeout bars count chart (5m) bars under aggregation;
irrelevant to the validated immediate entry.

## Max risk per trade (2026-10-24)

New engine setting `MaxRiskPerTrade` ($ for the whole position at the stop, 0 = off, default off):
a breakout whose stop would lose more is skipped and the day stays armed. With it off, results are
unchanged (ES 509 trades, PF 1.30). Immediate entry, validated settings otherwise:

| 1 ES (8–80t, $500 cap) | Trades | PF | Net | Max DD | Train / test PF | t |
|---|---|---|---|---|---|---|
| Off | 509 | 1.30 | +$57,314 | $10,974 | 1.29 / 1.33 | 2.6 |
| $1,500 | 484 | 1.29 | +$50,914 | $10,432 | 1.32 / 1.20 | 2.6 |
| $1,250 | 461 | 1.31 | +$49,831 | $8,762 | 1.37 / 1.16 | 2.7 |
| $1,000 | 381 | 1.24 | +$29,964 | $10,227 | 1.34 / **0.89** | 2.0 |
| $750 | 249 | 1.27 | +$18,217 | $6,418 | 1.23 / 1.52 | 1.8 |
| $500 | 81 | 0.87 | −$2,462 | $4,550 | 0.93 / 0.30 | −0.6 |

| 1 MNQ (32–600t, $150 cap) | Trades | PF | Net | Max DD | Train / test PF | t |
|---|---|---|---|---|---|---|
| Off | 585 | 1.30 | +$15,544 | $2,088 | 1.29 / 1.32 | 2.7 |
| $400 | 565 | 1.23 | +$11,645 | $2,633 | 1.25 / 1.19 | 2.2 |
| $300 | 478 | 1.25 | +$9,656 | $2,992 | 1.30 / 1.08 | 2.2 |
| **$250** | **386** | **1.40** | +$10,381 | **$1,623** | 1.43 / 1.24 | 3.0 |
| $200 | 281 | 1.44 | +$7,300 | $690 | 1.41 / 1.67 (27 test trades) | 2.8 |

**Reading.** On ES the limit mostly removes trades; $1,250 is neutral, tighter hurts. On MNQ a
$200–250 limit keeps or raises PF and cuts the worst drawdown, but NQ ranges keep growing, so a
dollar limit rules out more days each year (test-period trades fall from 130 to 50 at $250). It
was tuned on these results, so treat it as an account-fit tool, not proven extra edge. Default
stays off.

## NQ test

**Data.** `NQ-67287.csv`: NQ continuous 15m, 2023-12-17 .. 2026-10-23, 709 RTH days, the same
span as ES run 4. It has real volume in every month, 66,547 of 67,286 gaps are 15 minutes and
the rest are the daily break and weekends. Harness: `--tick-value 5 --commission 4 --min-range 32`,
validated setup (ORB-15, **Entry = Immediate**, all other defaults). "Recent" = `--from 2025-11-09`
(last ~11 months, the stretch ES runs 1–3 had already seen); "earlier" = the 471 days before it.

**NQ ranges grew a lot.** NQ went from ~16,800 to ~29,000, and the 09:30 15m range grew with it.
The median was 229 ticks in H1 2024 and is 525–570 ticks in 2026; on ES 80t covered most days.
A fixed tick limit therefore trades less and less as price rises (see open items).

**Max range sweep** (1 NQ, daily cap $1,500 unless stated; t ≥ 2 ≈ unlikely to be luck):

| Max range | All 709 days: trades / win / PF / net / max DD / t | Train / test PF | Earlier 471 days: PF / t | **Recent 238 days**: trades / PF / net / t |
|---|---|---|---|---|
| 400t | 415 / 53% / 1.46 / +$134,180 / $19,537 / 3.4 ($2k cap) | 1.46 / 1.45 | 1.44 / – | 68 / 1.53 / +$30,573 / – |
| 500t | 509 / 53% / 1.38 / +$153,714 / $18,613 / 3.1 | 1.42 / 1.23 | 1.46 / 3.3 | 102 / 1.16 / +$17,317 / 0.7 |
| **600t** | **585 / 52% / 1.31 / +$158,945 / $20,719 / 2.8** | **1.30 / 1.32** | **1.36 / 2.8** | **148 / 1.21 / +$35,078 / 1.0** |
| 700t | 627 / 52% / 1.32 / +$183,402 / $23,876 / 3.0 | 1.25 / 1.50 | 1.30 / 2.4 | 178 / 1.36 / +$72,658 / 1.8 |
| 800t | 660 / 52% / 1.26 / +$167,460 / $41,191 / 2.5 | 1.21 / 1.38 | 1.28 / – | 200 / 1.24 / +$57,330 / 1.3 |
| 1000t | 700 / 52% / 1.29 / +$199,375 / $51,947 / 2.8 ($2k cap) | 1.30 / 1.27 | 1.33 / – | 224 / 1.22 / +$61,524 / – |
| none | 712 / 52% / 1.26 / +$190,617 / $64,931 / 2.5 ($2k cap) | 1.32 / 1.16 | 1.36 / – | 232 / 1.13 / +$38,992 / – |

- **Daily loss cap barely matters.** With max 2 trades a day it only decides whether a second
  trade follows a big loss. Over $1,000 / $2,000 / $3,000 / $4,000 / no cap, PF moves by ≤ 0.03
  and net by a few % at every max range.
- **Every calendar year is positive** at 600t: 2024 PF 1.27, 2025 PF 1.35, 2026 PF 1.31 (also at 700t).
- **Slippage** of 1 tick per side ($10 per NQ trade) costs ~$5.9k on 585 trades: PF 1.31 → 1.29, t 2.7.
- Unlike ES, the **retest** entry does not lose on NQ over 3 years (600t: PF 1.22). It does lose
  in the recent stretch (PF 0.98), and immediate beats it in every slice.

**Verdict: ORB-15 immediate holds on NQ**, by the same bar as ES. Over 3 years PF is 1.31 and
t 2.8 (ES: 1.30 / 2.6). It is positive in train and in test, and positive in both the earlier
471 days (t 2.8) and the recent 11 months. The recent 11 months on their own are positive but
**not statistically meaningful** (t 1.0, 148 trades). About 15 range/cap variants were tried,
which inflates t a little.

**Recommended NQ / MNQ settings** (all other settings = the validated ES setup):

| | NQ | MNQ |
|---|---|---|
| Min range | 32 ticks (never binds; smallest opening range seen 56t) | 32 ticks |
| **Max range** | **600 ticks** (150 pts) | **600 ticks** |
| **Daily loss cap** | **$1,500** (about half a large loss, as ES's $500 is to its 80t ≈ $1,000) | **$150** |

**Actual loss per stopped trade** (the stop is the far side of the range, but entry is the
breakout bar's close, which can be well beyond the range edge, so the loss can exceed the range).
On 1 MNQ at 600t: median $208, 1 in 10 worse than $332, worst $501 (NQ: ×10). These losses grow with
the ranges: the median was $174 in 2024 and is $274 in 2026. The daily cap only blocks new entries;
it never cuts an open trade, so the worst day was one full stop ($501 MNQ). Second trades
after a loss are rare: a cap that stops after any loss gives the same PF 1.30 / t 2.7 / max DD.

Why 600t: the plateau where results stay steady is 500–700t (PF 1.31–1.38, max DD $19–24k, every
year positive). 600t is its middle. At 800t and above, drawdown roughly doubles ($41–65k) for no
gain in PF. 700t looked best in the recent stretch, but it sits next to that cliff, so 600t is the
robust choice. 700t is a fair alternative if the owner wants more trades in today's bigger ranges.

**Expected results at the recommended settings** (3 years, 709 sessions, $4 RT commission per
contract, no slippage):

| | Trades | Win | PF | Net | Worst drawdown | t |
|---|---|---|---|---|---|---|
| 1 NQ | 585 | 52% | 1.31 | +$158,945 | $20,719 | 2.8 |
| 1 MNQ (≈ (NQ + $4 × trades) / 10 − $1 × trades) | 585 | 52% | 1.30 | +$15,544 | $2,088 | 2.7 |
| 1 MNQ, recent 11 months only | 148 | 51% | 1.20 | +$3,419 | $2,088 | 1.0 |

Plain reading: in about half its trades the bot wins, and winners are ~1.3× the size of losers.
On 1 MNQ that made ~$15.5k over 3 years, with a worst peak-to-valley dip of ~$2.1k. A run that
good is unlikely to be luck over the full 3 years. The last 11 months alone are too short to
prove anything, but they are positive.

**Not tuned for NQ:** Protect pullback 6t and the breakeven/retest offsets are ES-sized
tick values; on NQ 6t is about 1.5 points. The results above use them as they are (Protect since
tested: keep 6t, see "NQ Protect settings"). A tick-based
max range also drifts as NQ's price rises; a limit relative to recent daily range would not.

**Confirmed on the ATAS chart (2026-10-24).** Market edition on #NQZ6 15m, NQ profile, max risk
off, simulation. The dashboard shows 585 trades, 52% win, PF 1.31, +$158,945, avg +$272, max
drawdown $20,719, identical to the harness.

### NQ on 5m vs 15m (same 238 days)

`NQ-67797.csv`: NQ continuous 5m, 2025-11-09 .. 2026-10-23 (ATAS caps 5m at ~1 year). It has
real volume every month and 67,545 of 67,796 gaps are 5 minutes. Compared with the 15m file over
the same days (`--from 2025-11-09`). NQ profile: min 32t, max 600t, cap $1,500, 1 NQ, $4 RT.

| 1 NQ, 238 days | Trades | Win | PF | Net | Max DD | t | Train / test PF |
|---|---|---|---|---|---|---|---|
| **15m** ORB-15 immediate | 148 | 51% | **1.21** | **+$35,078** | **$20,719** | 1.0 | 1.27 / 1.05 |
| 5m ORB-15 immediate | 153 | 48% | 1.04 | +$6,713 | $38,228 | 0.2 | 1.08 / 0.94 |
| 15m, max risk $2,000 | 31 | 52% | 1.71 | +$13,076 | $4,135 | 1.4 | 1.82 / 1.35 |
| 5m, max risk $2,000 | 43 | 44% | 1.19 | +$6,278 | $12,541 | 0.5 | 1.78 / 0.29 |
| 5m retest (code default) | 134 | 47% | 1.02 | +$3,539 | $25,088 | 0.1 | 1.06 / 0.94 |

**Verdict: run NQ on 15m only.** On 5m the same rules roughly break even, drawdown nearly
doubles, and the test third loses. This is unlike ES, where 5m held up (run 3: PF 1.31). On 5m the
breakout fires on the first 5-minute close past the range, and on NQ that entry gets shaken out
more often. Only one year of 5m exists, so this is not proof that 5m can never work. It is
clear enough to keep the market edition's NQ 15m-only lock, and to keep the in-house edition off
NQ 5m charts.

### NQ Protect settings (15m, 3 years)

Scratch copy of the harness with Protect exposed as options; the repo harness was not changed.
At the defaults it reproduces 585 / PF 1.31 / +$158,945 exactly. NQ profile, 1 NQ.

| Protect | Trades | Win | PF | Net | Max DD | t | Train / test PF |
|---|---|---|---|---|---|---|---|
| Off | 585 | 50% | 1.25 | +$130,520 | $28,774 | 2.3 | 1.24 / 1.26 |
| **At 80%, pullback 6t (default)** | 585 | 52% | **1.31** | +$158,945 | **$20,719** | 2.8 | 1.30 / 1.32 |
| At 80%, pullback 12t | 585 | 52% | 1.30 | +$154,685 | $20,809 | 2.7 | 1.29 / 1.32 |
| At 80%, pullback 20t | 585 | 52% | 1.29 | +$149,005 | $20,929 | 2.6 | 1.28 / 1.31 |
| At 80%, pullback 30t | 585 | 52% | 1.28 | +$142,720 | $21,079 | 2.5 | 1.27 / 1.30 |
| At 80%, pullback 40t | 585 | 52% | 1.27 | +$139,660 | $21,229 | 2.5 | 1.26 / 1.29 |
| At 80%, pullback 60t | 585 | 52% | 1.26 | +$133,800 | $21,504 | 2.4 | 1.25 / 1.28 |
| At 80%, pullback 80t | 585 | 52% | 1.25 | +$127,345 | $21,704 | 2.3 | 1.24 / 1.27 |
| At 60%, pullback 30t | 589 | 57% | 1.35 | +$165,124 | $21,969 | 3.2 | 1.37 / 1.28 |
| At 70%, pullback 30t | 586 | 55% | 1.31 | +$156,346 | $22,274 | 2.9 | 1.35 / 1.23 |
| At 90%, pullback 30t | 585 | 51% | 1.24 | +$128,655 | $28,649 | 2.2 | 1.24 / 1.25 |

**Verdict: keep the defaults on NQ.** Protect earns its place: switched off, PF falls to 1.25 and
drawdown rises to $28.8k. The ES-sized 6t pullback is not a handicap on NQ. Every wider pullback
does a little worse, step by step, because on 15m bars Protect mostly locks in the gain at the bar
close. An earlier trigger (60%) looks better over the 3 years but worse in the test third
(1.28 vs 1.32), which is the pattern of a fit to past data, not an improvement. Closes the
"Protect pullback not tuned for NQ" open item.

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

## Research run 4 — continuous ES 15m, ~3 years (2023-12-17 .. 2026-10-23), the deciding test

ATAS gave 15-minute history back to 2023-12-17 (709 RTH days). The 471 days before 2025-11-09
were **never seen** by runs 1–3, so they are the real out-of-sample test. On 15m bars the IB-15
is the single 09:30 bar and breakouts are judged on 15-minute closes (coarser than the 5m bot).

| | All 709 days: trades / PF / net / t | Train / test PF | **Unseen 471 days**: trades / PF / net / t |
|---|---|---|---|
| Current bot (ORB-15 **retest**) | 351 / 0.93 / −$8,017 / −0.6 | 0.89 / 1.08 | 260 / 0.87 / −$11,403 / −1.0 |
| **ORB-15 immediate** | **509 / 1.30 / +$57,314 / 2.6** | **1.29 / 1.33** | **376 / 1.32 / +$42,746 / 2.4** |
| IB-30 immediate | 331 / 1.08 / +$10,514 / 0.6 | 1.15 / 0.82 | 261 / 1.16 / +$14,969 / 1.0 |
| IB-60 immediate | 446 / 1.09 / +$17,966 / 0.8 | 1.17 / 0.92 | 315 / 1.14 / +$17,878 / 0.9 |
| Immediate, IB/ADR < 0.25 | 340 / 1.36 / +$44,115 / 2.5 | 1.29 / 1.59 | 256 / 1.34 / +$30,114 / 2.1 |
| Immediate, IB/ADR ≥ 0.35 | 155 / 0.95 / −$6,383 / −0.3 | 1.05 / 0.79 | 82 / 1.14 / +$8,635 / 0.4 |
| Rotation (IB-15, to IBM), all days | 214 / 0.76 / −$7,881 / −1.6 | 0.47 / 1.89 | 149 / **0.44** / −$14,359 / **−3.7** |
| Rotation, IB/ADR ≥ 0.40 | 41 / 0.87 / −$1,352 | 0.29 / 2.05 | 18 / 0.26 / −$5,022 / −2.1 |
| Combined regime system | 381 / 1.32 / +$42,764 / 2.4 | 1.22 / 1.64 | 274 / 1.26 / +$25,092 / 1.7 |

**Verdict after ~3 years.**
- **Survives:** ORB-15 with **immediate entry** — positive in every period tested (5m 2026, 5m
  one year, 15m three years, and the unseen 2024–25 alone), PF ≈ 1.3, t ≈ 2.4–2.6. The current
  **retest** entry loses in every long sample. This is the one finding that looks real.
- **Did not survive:** wide-IB rotation (strongly negative out of sample, t −3.7) and "wide-IB
  breakouts always lose" (true in 2026, not in 2024–25). The combined regime system's 2026 edge
  came from those two and fades out of sample.
- **Narrow-IB filter (< 0.25):** similar PF to unfiltered immediate (1.34–1.36 vs 1.30–1.32) with
  fewer trades — a small, unproven improvement at best.
- Costs: 1 tick slippage per side is ~$25 per trade; on 509 trades that is ~$12.7k, leaving the
  3-year result still clearly positive (~$44.6k, PF ≈ 1.23). Max drawdown ≈ $11k on 1 ES
  (≈ $1.1k on 1 MES).

## Research run 3 — continuous ES 5m, one year + unseen stretch

ATAS capped 5-minute history at ~1 year even with 1,500 days requested: `ES-67797.csv`,
2025-11-09 .. 2026-10-23, 238 RTH days. The 2025-11-09 .. 2026-01-16 stretch was **never seen**
in runs 1–2, so it is a true out-of-sample check. t = mean trade P&L ÷ standard error (≈2 is the
usual bar; rough, ignores the ~30 variants tried).

| | Full year: trades / PF / net / t | Last 30% (test) PF | Unseen stretch only: trades / PF / net |
|---|---|---|---|
| Current bot (ORB-15 retest) | 111 / 1.05 / +$2,081 / 0.2 | 0.61 | 29 / 1.60 / +$4,847 |
| ORB-15 immediate | 139 / 1.31 / +$16,932 / 1.5 | 1.23 | 31 / 1.18 / +$1,914 |
| Narrow-IB breakout leg (< 0.25) | 83 / 1.57 / +$16,818 / 1.9 | 1.69 | 15 / 1.43 / +$1,903 |
| Wide-IB rotation leg (≥ 0.40) | 32 / 1.58 / +$3,085 / 1.1 | 3.78 | 5 / 1.18 / +$155 |
| **Combined regime system** | **115 / 1.57 / +$19,903 / 2.2** | **1.80** | **20 / 1.39 / +$2,058** |
| Combined, 1 tick slippage/side | 115 / 1.47 / +$17,028 / 1.9 | 1.68 | 20 / 1.28 / +$1,558 |
| Wide-IB breakouts (≥ 0.35), avoided | 68 / 0.79 / −$10,810 / −0.8 | 0.69 | 10 / 0.51 / −$3,015 |

**Reading.** The combined system stays positive in every slice: train, test and the unseen
stretch. Wide-IB breakouts lose in every slice, so "don't take breakouts on wide-IB days" is the
most consistent finding. Over the full year the system reaches t ≈ 2.2 (1.9 after slippage):
borderline significant, and less so once the number of variants tried is counted. In the unseen
stretch it made money but trailed the current bot (PF 1.39 vs 1.60) on only 20 trades. Verdict:
**a real candidate worth building and forward-testing, not a proven edge.**

## Research run 2 — continuous ES, 193 days (2026-01-18 .. 2026-10-23)

Export of the **continuous ES** contract (`ES-54819.csv`, 25–38M contracts every month, no roll
artifacts). Train = first 70% of days (to 2026-08-03), test = last 30%. 1 ES, $4 commission.

**Engine variants**

| Variant | Trades | Win | PF | Net | Max DD | Train PF | Test PF |
|---|---|---|---|---|---|---|---|
| Baseline ORB-15 retest (current bot) | 82 | 43% | 0.92 | −$2,766 | $9,061 | 1.31 | **0.53** |
| ORB-15 immediate | 108 | 52% | 1.34 | +$15,018 | $8,776 | 1.65 | 1.01 |
| IB-30 retest / immediate | 44 / 55 | | 0.86 / 0.81 | | | | |
| IB-60 retest / immediate | 95 / 113 | | 1.03 / 1.02 | | $12–14k | | 0.75 / 0.88 |
| ORB-5 immediate (mid-IB momentum) | 198 | 49% | 1.27 | +$21,171 | $7,284 | 1.40 | 1.01 |
| ORB-10 immediate | 149 | 49% | 1.16 | +$10,254 | $9,114 | 1.29 | 0.95 |

**Breakout by regime** (ORB-15 immediate, IB-15 width ÷ prior-14-day average RTH range)

| IB/ADR | Trades | Win | PF | Net | Train PF | Test PF |
|---|---|---|---|---|---|---|
| < 0.15 | 7 | 57% | 1.44 | +$1,072 | 1.14 | 2.72 |
| 0.15–0.25 | 59 | 56% | **1.56** | +$12,677 | 1.51 | **1.65** |
| 0.25–0.35 | 61 | 48% | 1.16 | +$5,681 | 1.29 | 0.73 |
| ≥ 0.35 | 49 | 41% | **0.56** | **−$19,596** | 0.55 | 0.58 |

**Rotation** (fade first failed probe of IB-15 high/low to IBM): ≥ 0.40 → 22 trades, 64%, PF 2.01,
+$3,325 (train 1.78, test 3.28); 0.25–0.40 → PF 0.51 (both halves negative). IB-30/60 rotation
and half-way targets: no robust bucket.

**Combined regime system** — narrow (IB/ADR < 0.25) = immediate breakout; wide (≥ 0.40) =
rotation to IBM; middle = no trade:

| | Trades | Win | PF | Net | Max DD | Train PF | Test PF |
|---|---|---|---|---|---|---|---|
| Combined | 88 | 58% | **1.60** | **+$17,073** | $4,728 | 1.51 | **1.80** |
| Combined, 1 tick slippage per side | 88 | 58% | 1.51 | +$14,873 | $4,978 | 1.43 | 1.68 |

**Reading.**
- The most robust single finding: **breakouts on wide-IB days lose** (49 trades, PF 0.55 train /
  0.58 test, −$19.6k). They are where the current bot's losses come from.
- **Narrow-IB immediate breakouts** hold in both halves (PF 1.51 / 1.65, 59 trades).
- Immediate entry beats the retest limit across the board (adverse selection of the limit).
- The combined regime system is positive in both halves and after slippage, with half the
  drawdown of the baseline.
- Caveats: one instrument, ~9 months, one market period; 88 trades total and only 22 rotation
  trades. Bucket edges were coded before the real data was seen, and the narrow-breakout /
  wide-rotation split was the owner's stated hypothesis before testing, which limits (not
  removes) data-snooping risk. Needs forward testing before any money.

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
