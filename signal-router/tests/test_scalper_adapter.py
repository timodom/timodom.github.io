"""Scalper adapter -- ticks-to-prices and the closed-trade shape."""
import csv
import io
import json

import pytest

from signalrouter.adapters import ScalperAdapter
from signalrouter.adapters.base import Reject
from signalrouter.model import Direction, Signal, Status, StrategyId

HEADER = ("trade_id,session,direction,entry_signal_reason,entry_conditions_json,"
          "intended_entry_price,actual_entry_price,slippage_ticks,stop_ticks,"
          "target_t1_ticks,target_t2_ticks,runner_mode,quantity,entry_time,"
          "exit_time,exit_reason,exit_price,realized_pnl")


def row(direction="LONG", entry=6502.0, stop_ticks=8, t1=12, t2=20,
        exit_reason="target", exit_price=6505.0, pnl=150.0, conditions=None):
    """Build a row the way ``trade_log.py`` does -- via csv, so the embedded
    JSON blob is quoted and escaped exactly as the real writer emits it."""
    cond = json.dumps(conditions if conditions is not None else {"delta": 240, "wall": True},
                      separators=(",", ":"))
    buf = io.StringIO()
    csv.writer(buf, lineterminator="").writerow([
        "SC-0001", "morning", direction, "absorption at wall", cond,
        entry, entry, 0.0, stop_ticks, t1, t2 if t2 is not None else "",
        "False", 1, "2026-08-29T14:31:00Z", "2026-08-29T14:44:00Z",
        exit_reason, exit_price, pnl,
    ])
    return buf.getvalue()


@pytest.fixture
def adapter():
    return ScalperAdapter()


def test_header_skipped(adapter):
    assert adapter.parse_line(HEADER) is None


def test_ticks_convert_to_absolute_prices_long(adapter):
    s = adapter.parse_line(row(), "trades.csv")
    assert isinstance(s, Signal)
    assert s.entry == 6502.0
    assert s.stop == 6502.0 - 8 * 0.25    # 6500.0
    assert s.target_1 == 6502.0 + 12 * 0.25
    assert s.target_2 == 6502.0 + 20 * 0.25
    assert s.stop_is_coherent


def test_ticks_convert_to_absolute_prices_short(adapter):
    s = adapter.parse_line(row(direction="SHORT", exit_price=6499.0), "trades.csv")
    assert s.direction is Direction.SHORT
    assert s.stop == 6502.0 + 8 * 0.25
    assert s.target_1 == 6502.0 - 12 * 0.25
    assert s.stop_is_coherent


def test_no_symbol_column_so_it_is_configured(adapter):
    assert adapter.parse_line(row()).symbol == "ES"
    assert ScalperAdapter(symbol="NQ").parse_line(row()).symbol == "NQ"


def test_custom_tick_size(adapter):
    s = ScalperAdapter(tick_size=0.01).parse_line(row())
    assert s.stop == pytest.approx(6502.0 - 8 * 0.01)


def test_arrives_already_terminal(adapter):
    # This is a closed-trade log: rows only exist once the trade finished.
    assert adapter.parse_line(row(exit_reason="target")).status is Status.TARGET
    assert adapter.parse_line(row(exit_reason="stop")).status is Status.STOPPED
    assert adapter.parse_line(row(exit_reason="time_invalidation")).status is Status.EXPIRED
    for r in ("runner_absorption", "manual", "session_end"):
        assert adapter.parse_line(row(exit_reason=r)).status is Status.CLOSED


def test_exit_reason_is_preserved_not_flattened(adapter):
    # CLOSED buckets three different exits -- the precise reason must survive so
    # a win-rate can be computed without them being conflated.
    s = adapter.parse_line(row(exit_reason="runner_absorption"))
    assert s.metadata["exit_reason"] == "runner_absorption"


def test_outcome_r_computed_from_prices(adapter):
    # +3.00 points on a 2.00-point risk (8 ticks) = +1.5R
    s = adapter.parse_line(row(entry=6502.0, stop_ticks=8, exit_price=6505.0))
    assert s.outcome_r == pytest.approx(1.5)


def test_outcome_r_negative_on_a_loss(adapter):
    s = adapter.parse_line(row(exit_reason="stop", exit_price=6500.0))
    assert s.outcome_r == pytest.approx(-1.0)


def test_outcome_r_sign_correct_for_shorts(adapter):
    s = adapter.parse_line(row(direction="SHORT", entry=6502.0, exit_price=6499.0))
    assert s.outcome_r == pytest.approx(1.5)


def test_realized_pnl_kept_in_metadata_not_as_r(adapter):
    # R comes from prices so it is comparable across strategies and contract sizes.
    s = adapter.parse_line(row(pnl=150.0))
    assert s.metadata["realized_pnl"] == 150.0


def test_missing_t2_is_none(adapter):
    assert adapter.parse_line(row(t2=None)).target_2 is None


def test_entry_conditions_parsed(adapter):
    s = adapter.parse_line(row(conditions={"delta": 300}))
    assert s.metadata["entry_conditions"]["delta"] == 300


def test_unparsable_conditions_kept_verbatim(adapter):
    # A corrupt metadata blob must not cost us the whole trade record.
    fields = next(csv.reader(io.StringIO(row())))
    fields[4] = "{not json"
    buf = io.StringIO()
    csv.writer(buf, lineterminator="").writerow(fields)
    s = adapter.parse_line(buf.getvalue())
    assert isinstance(s, Signal)
    assert s.metadata["entry_conditions"]["_unparsed"] == "{not json"


def test_confidence_absent_rather_than_invented(adapter):
    assert adapter.parse_line(row()).confidence is None


@pytest.mark.parametrize("bad", [
    row(direction="SIDEWAYS"),
    row(stop_ticks=0),
    row(stop_ticks=-4),
    "SC-1,morning,LONG",
])
def test_bad_lines_reject_and_never_raise(adapter, bad):
    assert isinstance(adapter.parse_line(bad, "trades.csv"), Reject)
