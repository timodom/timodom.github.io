#!/usr/bin/env python3
"""Verify the adapters against real captured files (the Phase 0 → Phase 1 bridge).

The adapters were written from each bot's *writer* source, not its docs, which is
strong — but not proof. This is the proof step: point it at a real file and it
reports what parsed, what did not, and why.

    python tools/verify_capture.py --adapter rain    "C:\\ATAS\\signals\\rain_signals.csv"
    python tools/verify_capture.py --adapter scalper scalper/logs/trades.csv
    python tools/verify_capture.py --adapter mbo     "...\\ESU6_CME_2026-08-29_intents.jsonl"

Opens every file read-only and writes nothing anywhere. It lives outside the
``signalrouter`` package on purpose: the package itself contains no file access
at all, and a test asserts that. Keeping the one thing that touches disk out here
keeps that assertion honest.

Exit code is non-zero if anything was rejected, so it can gate a phase.
"""

from __future__ import annotations

import argparse
import pathlib
import sys
from collections import Counter

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from signalrouter.adapters import MboAdapter, RainAdapter, ScalperAdapter  # noqa: E402
from signalrouter.adapters.base import Reject  # noqa: E402

ADAPTERS = {"rain": RainAdapter, "scalper": ScalperAdapter, "mbo": MboAdapter}


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("path")
    ap.add_argument("--adapter", required=True, choices=sorted(ADAPTERS))
    ap.add_argument("--symbol", help="override symbol (scalper has no symbol column)")
    ap.add_argument("--show", type=int, default=3, help="signals to print (default 3)")
    ap.add_argument("--max-rejects", type=int, default=20)
    args = ap.parse_args(argv)

    path = pathlib.Path(args.path)
    if not path.is_file():
        print(f"not a file: {path}", file=sys.stderr)
        return 2

    kwargs = {}
    if args.symbol:
        kwargs = {"symbol": args.symbol} if args.adapter == "scalper" else {"default_symbol": args.symbol}
    adapter = ADAPTERS[args.adapter](**kwargs)

    signals, rejects, skipped = [], [], 0
    # errors="replace": a half-flushed final line must not abort the whole check.
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            result = adapter.parse_line(line, str(path))
            if result is None:
                skipped += 1
            elif isinstance(result, Reject):
                rejects.append(result)
            else:
                signals.append(result)

    total = len(signals) + len(rejects) + skipped
    print(f"\n{path.name}  [{args.adapter}]")
    print("─" * 64)
    print(f"  lines read      {total}")
    print(f"  parsed          {len(signals)}")
    print(f"  skipped         {skipped}   (headers, blanks)")
    print(f"  rejected        {len(rejects)}")

    if signals:
        first, last = signals[0], signals[-1]
        print(f"  time span       {first.ts_utc:%Y-%m-%d %H:%M:%S} → {last.ts_utc:%H:%M:%S} UTC")
        print(f"  symbols         {', '.join(sorted({s.symbol for s in signals}))}")
        by_dir = Counter(s.direction.value for s in signals)
        print(f"  direction       {dict(by_dir)}")
        by_status = Counter(s.status.value for s in signals)
        print(f"  status          {dict(by_status)}")

        incoherent = [s for s in signals if not s.stop_is_coherent]
        if incoherent:
            print(f"\n  ⚠  {len(incoherent)} signal(s) with a stop on the wrong side of entry.")
            print("     Recorded, not dropped — this is a finding about the strategy,")
            print("     not a parser bug. First:", incoherent[0].signal_id)

        resolved = [s for s in signals if s.outcome_r is not None]
        if resolved:
            avg = sum(s.outcome_r for s in resolved) / len(resolved)
            wins = sum(1 for s in resolved if s.outcome_r > 0)
            print(f"\n  outcomes        {len(resolved)} resolved | "
                  f"{wins} up | avg {avg:+.2f}R")

        print(f"\n  first {min(args.show, len(signals))} parsed:")
        for s in signals[: args.show]:
            tgt = f" → {s.target_1}" if s.target_1 else ""
            conf = f" conf {s.confidence:.2f}" if s.confidence is not None else ""
            print(f"    {s.ts_utc:%H:%M:%S} {s.strategy_id.value:<18} {s.symbol:<6} "
                  f"{s.direction.value:<5} {s.entry}/{s.stop}{tgt}{conf}")
            if s.reason:
                print(f"      {s.reason[:72]}")

    if rejects:
        print(f"\n  rejected lines (first {min(args.max_rejects, len(rejects))}):")
        for r in rejects[: args.max_rejects]:
            print(f"    ✕ {r}")
        print("\n  Any reject means the adapter and the writer disagree.")
        print("  Send these lines back and the adapter gets corrected — that is")
        print("  exactly what this step is for.")
        return 1

    if not signals:
        print("\n  Nothing parsed. If this is RAIN, check Mode=LIVE — Write-SignalCsv")
        print("  returns early unless EnableExecCsv, so DRY_RUN writes no rows.")
        return 1

    print("\n  ✓ every line accounted for\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
