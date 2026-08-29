"""RAIN adapter.

Fixture lines are built to match ``Write-SignalCsv`` byte-for-byte: the exact
header it writes, lowercase buy/sell, InvariantCulture periods, and the
``RAIN-yyyyMMdd-HHmmss-fff`` id format.
"""
from datetime import timezone

import pytest

from signalrouter.adapters import RainAdapter
from signalrouter.adapters.base import Reject
from signalrouter.model import Direction, Signal, Status, StrategyId

HEADER = "signal_id,timestamp,symbol,side,quantity,entry,sl,tp1,tp2,status,score"
LONG = "RAIN-20260829-114321-123,2026-08-29T11:43:21Z,ES,buy,2,6502,6497,6512,6517,pending,4"
SHORT = "RAIN-20260829-120500-001,2026-08-29T12:05:00Z,ES,sell,2,6510.25,6515.25,6500.25,6495.25,pending,3"


@pytest.fixture
def adapter():
    return RainAdapter()


def test_header_and_blank_are_skipped_not_rejected(adapter):
    assert adapter.parse_line(HEADER) is None
    assert adapter.parse_line("") is None
    assert adapter.parse_line("   \n") is None


def test_long_signal(adapter):
    s = adapter.parse_line(LONG, "rain_signals.csv")
    assert isinstance(s, Signal)
    assert s.signal_id == "RAIN-20260829-114321-123"
    assert s.strategy_id is StrategyId.RAIN_BREAK_RETEST
    assert s.direction is Direction.LONG
    assert s.entry == 6502 and s.stop == 6497
    assert s.target_1 == 6512 and s.target_2 == 6517
    assert s.quantity == 2
    assert s.status is Status.SIGNALED
    assert s.ts_utc.tzinfo is not None
    assert s.ts_utc.astimezone(timezone.utc).hour == 11


def test_score_normalises_to_confidence(adapter):
    assert adapter.parse_line(LONG).confidence == 1.0    # 4/4
    assert adapter.parse_line(SHORT).confidence == 0.75  # 3/4


def test_lowercase_sell_becomes_short(adapter):
    s = adapter.parse_line(SHORT)
    assert s.direction is Direction.SHORT
    assert s.stop > s.entry
    assert s.stop_is_coherent


def test_decimal_prices_parse_exactly(adapter):
    # The writer uses InvariantCulture because a C# indicator reads this file.
    s = adapter.parse_line(SHORT)
    assert s.entry == 6510.25
    assert s.risk_ticks() == 20.0


def test_zero_tp2_means_absent_not_zero(adapter):
    # [double]$null in PowerShell renders as 0 -- a price of 0 is never real.
    line = LONG.replace(",6517,", ",0,")
    s = adapter.parse_line(line)
    assert s.target_2 is None
    assert s.target_1 == 6512


@pytest.mark.parametrize("bad,why", [
    ("RAIN-1,2026-08-29T11:43:21Z,ES,hold,2,6502,6497,6512,6517,pending,4", "side"),
    ("RAIN-1,notatime,ES,buy,2,6502,6497,6512,6517,pending,4", "timestamp"),
    ("RAIN-1,2026-08-29T11:43:21Z,ES,buy,2,NaNprice,6497,6512,6517,pending,4", "numeric"),
    ("RAIN-1,2026-08-29T11:43:21Z,ES,buy,2", "columns"),
    ("RAIN-1,2026-08-29T11:43:21Z,ES,buy,2,6502,6502,6512,6517,pending,4", "zero risk"),
])
def test_bad_lines_reject_and_never_raise(adapter, bad, why):
    r = adapter.parse_line(bad, "rain_signals.csv")
    assert isinstance(r, Reject), f"expected reject for {why}"
    assert r.line == bad


def test_status_vocabulary_maps(adapter):
    assert adapter.parse_line(LONG.replace(",pending,", ",filled,")).status is Status.FILLED
    assert adapter.parse_line(LONG.replace(",pending,", ",cancelled,")).status is Status.INVALIDATED
    # Unknown status must not lose the row -- fall back and keep the raw value.
    s = adapter.parse_line(LONG.replace(",pending,", ",weird,"))
    assert s.status is Status.SIGNALED
    assert s.metadata["raw_status"] == "weird"
