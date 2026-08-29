# Signal Router — Phase 1

Normalises what your three bots already write into one signal shape, so you can
trade one strategy and collect evidence on the others.

**This phase is parse-only.** No file writes, no network, no ATAS, no order code.
Nothing here runs against a live system yet.

## Why this can't touch your trading

Not because a flag says so — because the capability is absent. `tests/test_no_execution_capability.py`
walks the package's AST and fails the build if anyone ever adds:

- an import of `socket`, `urllib`, `requests`, `win32file`, `win32pipe`, `order_router`, `subprocess`, `ctypes`, …
- a call to `place_bracket_entry`, `close_all`, `move_tp`, `cancel_orders`, `send_command`, `OpenOrder`, …
- **any file access at all** — not even a read, in this phase

A mode flag is one bad merge from being wrong. An import that doesn't exist can't be.

The one thing that reads a file lives in `tools/`, deliberately outside the package,
so that assertion stays honest.

## The three adapters

Each was written from the bot's **writer source**, not its documentation. Three
traps that only reading the writer reveals:

| Bot | Source | The trap |
|---|---|---|
| RAIN | `rain_signals.csv` — `AlertSender.ps1::Write-SignalCsv` | `side` is lowercase `buy`/`sell`; a missing TP renders as `0`, not blank |
| Auto MBO | `..._intents.jsonl` — `ConfluenceEngine.cs::WriteIntent` | **Symbol is only in the filename**, never in the JSON |
| Scalper | `trades.csv` — `trade_log.py` | Stop/target are in **ticks**, not prices; no symbol column at all |

### One gap you need to know about

`Write-SignalCsv` returns early unless `EnableExecCsv`, which is only true when
`Mode=LIVE`. **In `DRY_RUN`, RAIN writes no signal CSV** — so this adapter sees
nothing. Since you run RAIN live daily that's fine today, but observing RAIN while
it *isn't* trading needs a different source (the `Logger.ps1` log, or a small
addition to the scanner's alert path). Flagging it rather than silently assuming.

## Verify against your real files

The adapters are grounded in the writers, which is strong — but not proof. Once
you've captured a session:

```bash
python tools/verify_capture.py --adapter rain    "C:\ATAS\signals\rain_signals.csv"
python tools/verify_capture.py --adapter scalper scalper/logs/trades.csv
python tools/verify_capture.py --adapter mbo     "%APPDATA%\ATAS\IndicatorData\AutoMboIntents\ES..._intents.jsonl"
```

Opens read-only, writes nothing, exits non-zero if anything was rejected. Every
rejected line is a place the adapter and the writer disagree — send them back and
the adapter gets corrected. That's what the step is for.

## Design decisions worth knowing

**Signals are immutable.** The router appends rather than mutates, so what a bot
believed *at the time* survives — which is the entire point of collecting this.

**Confidence is normalised to 0–1.** RAIN's 4-check score becomes `score/4`; MBO's
`conf` is already 0–1. The scalper emits no score, so its confidence is `None` —
absent beats invented.

**R is computed from prices, not dollars**, so it means the same thing across
strategies regardless of contract size.

**A bad line never raises.** Parsers return a `Reject` with a reason. One malformed
row in a file a live bot is appending to must not take the router down. Rejects are
counted and surfaced, never swallowed — a rising reject rate is how you find out a
bot changed its format.

**An incoherent stop is recorded, not dropped.** A LONG with its stop above entry
is a real finding about that strategy; rejecting the row would hide it. `Signal.stop_is_coherent`
flags it and `verify_capture.py` reports it.

**No consensus field.** Each strategy stays independently measurable. Agreement is
something you compute from a table of these later, not something the pipeline bakes in.

## Tests

```bash
python -m pytest tests/ -q     # 58 passing
```

Pure functions, no I/O, no platform dependency — unlike the scalper's own suite,
this runs anywhere, including CI.

## What's next

Phase 2 is the router process: tails these files read-only, dedupes on `signal_id`,
writes SQLite plus a CSV mirror, survives restarts via stored file offsets. That's
the phase where the `no file access` test relaxes to `read-only file access` — a
deliberate, visible change to the guarantee rather than a silent one.

Phase 3 (the outcome tracker: MFE, MAE, what would have happened) is what turns
this from a log of opinions into evidence.
