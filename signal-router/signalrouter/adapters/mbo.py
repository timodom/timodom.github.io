"""Auto MBO shadow -> Signal.

Source: ``%APPDATA%\\ATAS\\IndicatorData\\AutoMboIntents\\{sym}_{exch}_{date}_intents.jsonl``,
written by ``mbo-bot/Strategy/ConfluenceEngine.cs::WriteIntent`` via ``JsonRecorder``.

    {"ms":1756467801000,"long":true,"entry":6502.25,"stop":6497,"target":6512,
     "tier":"Major","sleeve":"swing","conf":0.75,"reasons":["sweep vol=246","bid iceberg 320"]}

**The trap: there is no symbol in the JSON.** ``JsonRecorder.Write`` puts symbol
and exchange in the *filename* and nowhere else, so a parser that only reads the
line cannot know what instrument it is. This adapter derives it from the filename
and refuses rather than guessing when the name does not match the pattern --
mislabelling an instrument would quietly poison every comparison downstream.

Also note ``conf`` is already 0..1 (no rescaling needed, unlike RAIN's 0..4
score), and there is a single ``target`` -- the focused port has no TP2 -- so
``target_2`` is always None here.
"""

from __future__ import annotations

import json
import re
from datetime import datetime, timezone

from ..model import Direction, Signal, Status, StrategyId
from .base import ParseResult, Reject

# JsonRecorder: f"{sym}_{exch}_{day}_{suffix}.jsonl" with day = yyyy-MM-dd.
_FILENAME = re.compile(
    r"^(?P<sym>[^_]+)_(?P<exch>[^_]+)_(?P<day>\d{4}-\d{2}-\d{2})_(?P<suffix>[^.]+)\.jsonl$"
)


class MboAdapter:
    name = "mbo"

    def __init__(self, default_symbol: str | None = None) -> None:
        # Left None on purpose: an explicit reject beats a wrong instrument.
        self.default_symbol = default_symbol

    def parse_line(self, line: str, source_file: str = "") -> ParseResult:
        raw = line.strip()
        if not raw:
            return None

        symbol, exchange = self._symbol_from(source_file)
        if symbol is None:
            return Reject(
                f"cannot determine symbol from filename {source_file!r} "
                "(MBO writes it only in the filename)",
                raw, source_file,
            )

        try:
            d = json.loads(raw)
        except json.JSONDecodeError as exc:
            return Reject(f"invalid json ({exc})", raw, source_file)
        if not isinstance(d, dict):
            return Reject("json is not an object", raw, source_file)

        missing = [k for k in ("ms", "long", "entry", "stop") if k not in d]
        if missing:
            return Reject(f"missing field(s) {','.join(missing)}", raw, source_file)

        try:
            ts = datetime.fromtimestamp(int(d["ms"]) / 1000.0, tz=timezone.utc)
            entry = float(d["entry"])
            stop = float(d["stop"])
            target = float(d["target"]) if d.get("target") not in (None, "") else None
            conf = float(d["conf"]) if d.get("conf") is not None else None
        except (TypeError, ValueError, OSError, OverflowError) as exc:
            return Reject(f"bad numeric/timestamp ({exc})", raw, source_file)

        if entry == stop:
            return Reject("entry == stop (zero risk)", raw, source_file)
        if conf is not None and not 0.0 <= conf <= 1.0:
            return Reject(f"conf out of range: {conf}", raw, source_file)

        reasons = d.get("reasons") or []
        if not isinstance(reasons, list):
            reasons = [str(reasons)]

        sleeve = str(d.get("sleeve", "") or "")
        tier = str(d.get("tier", "") or "")

        return Signal(
            # The writer emits no id, so derive a deterministic one: replaying the
            # same file must not create duplicate rows.
            signal_id=f"MBO-{symbol}-{int(d['ms'])}-{'L' if d['long'] else 'S'}",
            strategy_id=StrategyId.MBO_ABSORPTION,
            ts_utc=ts,
            symbol=symbol,
            direction=Direction.LONG if d["long"] else Direction.SHORT,
            entry=entry,
            stop=stop,
            target_1=target,
            target_2=None,          # focused port is single-TP by design
            timeframe="tick",       # MBO is event-driven, not bar-driven
            status=Status.SIGNALED,
            confidence=conf,
            reason="; ".join(str(r) for r in reasons),
            source_file=source_file,
            metadata={"tier": tier, "sleeve": sleeve, "exchange": exchange,
                      "reasons": [str(r) for r in reasons]},
        )

    def _symbol_from(self, source_file: str) -> tuple[str | None, str | None]:
        if source_file:
            base = source_file.replace("\\", "/").rsplit("/", 1)[-1]
            m = _FILENAME.match(base)
            if m:
                return m.group("sym"), m.group("exch")
        return self.default_symbol, None
