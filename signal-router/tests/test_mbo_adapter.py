"""MBO adapter -- the symbol-in-filename trap is the main thing under test."""
import json

import pytest

from signalrouter.adapters import MboAdapter
from signalrouter.adapters.base import Reject
from signalrouter.model import Direction, Signal, Status, StrategyId

FILE = "ESU6_CME_2026-08-29_intents.jsonl"
INTENT = json.dumps({
    "ms": 1756467801000, "long": True, "entry": 6502.25, "stop": 6497.0,
    "target": 6512.0, "tier": "Major", "sleeve": "swing", "conf": 0.75,
    "reasons": ["sweep vol=246", "elected stops=7", "bid iceberg 320"],
})


@pytest.fixture
def adapter():
    return MboAdapter()


def test_symbol_comes_from_filename(adapter):
    s = adapter.parse_line(INTENT, FILE)
    assert isinstance(s, Signal)
    assert s.symbol == "ESU6"
    assert s.metadata["exchange"] == "CME"


def test_full_path_is_handled(adapter):
    win = r"C:\Users\Timothy\AppData\Roaming\ATAS\IndicatorData\AutoMboIntents" "\\" + FILE
    assert adapter.parse_line(INTENT, win).symbol == "ESU6"
    assert adapter.parse_line(INTENT, "/mnt/c/x/" + FILE).symbol == "ESU6"


def test_unknown_filename_rejects_rather_than_guessing(adapter):
    # Guessing the instrument would silently poison every comparison downstream.
    r = adapter.parse_line(INTENT, "intents.jsonl")
    assert isinstance(r, Reject)
    assert "symbol" in r.reason
    assert isinstance(adapter.parse_line(INTENT, ""), Reject)


def test_explicit_default_symbol_allows_it(adapter):
    s = MboAdapter(default_symbol="ES").parse_line(INTENT, "whatever.jsonl")
    assert isinstance(s, Signal) and s.symbol == "ES"


def test_fields_map(adapter):
    s = adapter.parse_line(INTENT, FILE)
    assert s.strategy_id is StrategyId.MBO_ABSORPTION
    assert s.direction is Direction.LONG
    assert s.entry == 6502.25 and s.stop == 6497.0
    assert s.target_1 == 6512.0
    assert s.target_2 is None          # focused port is single-TP
    assert s.confidence == 0.75        # already 0..1, not rescaled
    assert s.status is Status.SIGNALED
    assert s.timeframe == "tick"
    assert "bid iceberg 320" in s.reason
    assert s.metadata["tier"] == "Major" and s.metadata["sleeve"] == "swing"


def test_epoch_ms_converts_to_utc(adapter):
    s = adapter.parse_line(INTENT, FILE)
    assert s.ts_utc.tzinfo is not None
    assert int(s.ts_utc.timestamp() * 1000) == 1756467801000


def test_signal_id_is_deterministic(adapter):
    # Re-reading a file after a restart must not create duplicate rows.
    a = adapter.parse_line(INTENT, FILE)
    b = adapter.parse_line(INTENT, FILE)
    assert a.signal_id == b.signal_id
    short = json.dumps({**json.loads(INTENT), "long": False})
    assert adapter.parse_line(short, FILE).signal_id != a.signal_id


def test_short_direction(adapter):
    s = adapter.parse_line(json.dumps({
        "ms": 1756467801000, "long": False, "entry": 6502.25, "stop": 6507.0,
        "target": 6492.0, "conf": 0.5, "reasons": [],
    }), FILE)
    assert s.direction is Direction.SHORT
    assert s.stop_is_coherent


@pytest.mark.parametrize("bad", [
    "{not json",
    "[1,2,3]",
    json.dumps({"ms": 1, "long": True, "entry": 1.0}),            # missing stop
    json.dumps({"ms": "x", "long": True, "entry": 1.0, "stop": 2.0}),
    json.dumps({"ms": 1, "long": True, "entry": 5.0, "stop": 5.0}),  # zero risk
    json.dumps({"ms": 1, "long": True, "entry": 1.0, "stop": 2.0, "conf": 7.0}),
])
def test_bad_lines_reject_and_never_raise(adapter, bad):
    assert isinstance(adapter.parse_line(bad, FILE), Reject)


def test_blank_line_is_skipped(adapter):
    assert adapter.parse_line("", FILE) is None
    assert adapter.parse_line("  ", FILE) is None
