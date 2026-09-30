#!/usr/bin/env python3
"""MAPS - Pools layer. A small, cautious routine runner.

Call `tick` every 5 minutes from cron (Mac/Linux) or Task Scheduler (Windows).
Each routine runs at most once per day, after its scheduled time.

    python3 scripts/routines.py tick            # run whatever is due
    python3 scripts/routines.py run morning-digest
    python3 scripts/routines.py list

Guards against burning your Claude Pro quota:
  - a lock file, so runs never overlap
  - a hard timeout per routine
  - a daily cap on Claude runs (limits.claude_runs_per_day)
  - no automatic retries: a failure is logged and waits for tomorrow
"""
import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
from config import load_config  # noqa: E402
import build_data  # noqa: E402

LOG = ROOT / "logs" / "routines.jsonl"
LOCK = ROOT / "logs" / "routines.lock"
DAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]


def history():
    if not LOG.exists():
        return []
    out = []
    for line in LOG.read_text(encoding="utf-8").splitlines()[-2000:]:
        try:
            out.append(json.loads(line))
        except ValueError:
            pass
    return out


def log(rec):
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(json.dumps(rec) + "\n")


def acquire_lock(stale_after=3600):
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    try:
        if LOCK.exists() and time.time() - LOCK.stat().st_mtime > stale_after:
            LOCK.unlink()
        fd = os.open(LOCK, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)
        return True
    except FileExistsError:
        return False


def release_lock():
    try:
        LOCK.unlink()
    except FileNotFoundError:
        pass


def build_command(r, cfg, vault):
    if r.get("prompt"):
        prompt_path = ROOT / r["prompt"]
        prompt = prompt_path.read_text(encoding="utf-8")
        prompt = prompt.replace("{{today}}", dt.date.today().isoformat())
        cmd = [cfg.get("claude_bin", "claude"), "-p", prompt,
               "--max-turns", str(r.get("max_turns", 12))]
        tools = r.get("allowed_tools") or cfg.get("limits", {}).get("allowed_tools")
        if tools:
            cmd += ["--allowedTools", ",".join(tools)]
        return cmd
    if r.get("command") == "build_data":
        return None  # handled in-process
    return r["command"]


def run(r, cfg, vault):
    started = dt.datetime.now()
    rec = {"name": r["name"], "started": started.isoformat(timespec="seconds"),
           "claude": bool(r.get("prompt"))}
    try:
        cmd = build_command(r, cfg, vault)
        if cmd is None:
            build_data.build(vault, cfg, ROOT / "dashboard" / "data" / "data.js")
            rec.update(status="ok", exit=0)
        else:
            proc = subprocess.run(cmd, cwd=vault, shell=isinstance(cmd, str),
                                  capture_output=True, text=True,
                                  timeout=r.get("timeout", 900))
            rec.update(status="ok" if proc.returncode == 0 else "failed", exit=proc.returncode,
                       tail=(proc.stdout or proc.stderr or "")[-400:])
    except subprocess.TimeoutExpired:
        rec.update(status="timeout", exit=None)
    except Exception as e:  # keep the runner alive; the log says what broke
        rec.update(status="error", exit=None, tail=str(e)[-400:])
    rec["duration"] = round((dt.datetime.now() - started).total_seconds())
    log(rec)
    return rec


def is_due(r, now, hist):
    if not r.get("enabled", True):
        return False
    days = [d.lower()[:3] for d in r.get("days", DAYS)]
    if DAYS[now.weekday()] not in days:
        return False
    if now.strftime("%H:%M") < r.get("time", "00:00"):
        return False
    today = now.date().isoformat()
    return not any(h["name"] == r["name"] and h["started"].startswith(today) for h in hist)


def claude_runs_today(hist):
    today = dt.date.today().isoformat()
    return sum(1 for h in hist if h.get("claude") and h["started"].startswith(today))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("action", choices=["tick", "run", "list"])
    ap.add_argument("name", nargs="?")
    ap.add_argument("--config", default=str(ROOT / "maps.yaml"))
    args = ap.parse_args()

    cfg = load_config(args.config)
    vault = Path(os.path.expanduser(cfg.get("vault", ""))).resolve()
    if not vault.is_dir():
        sys.exit(f"Vault not found: {vault}")
    routines = cfg.get("routines") or []
    cap = cfg.get("limits", {}).get("claude_runs_per_day", 3)

    if args.action == "list":
        hist = history()
        for r in sorted(routines, key=lambda r: r.get("time", "")):
            flag = "claude" if r.get("prompt") else "local "
            print(f"{r.get('time', '--:--')}  {flag}  {'on ' if r.get('enabled', True) else 'off'}  {r['name']}")
        print(f"Claude runs today: {claude_runs_today(hist)}/{cap}")
        return

    if not acquire_lock():
        print("Another run is in progress; skipping.")
        return
    try:
        hist = history()
        now = dt.datetime.now()
        if args.action == "run":
            todo = [r for r in routines if r["name"] == args.name]
            if not todo:
                sys.exit(f"No routine named {args.name!r}")
        else:
            todo = [r for r in routines if is_due(r, now, hist)]
        ran_any = False
        for r in todo:
            if r.get("prompt") and claude_runs_today(hist) >= cap:
                rec = {"name": r["name"], "started": now.isoformat(timespec="seconds"),
                       "status": "capped", "claude": False, "duration": 0}
                log(rec)
                hist.append(rec)
                print(f"{r['name']}: skipped, daily Claude cap ({cap}) reached")
                continue
            rec = run(r, cfg, vault)
            hist.append(rec)
            ran_any = True
            print(f"{r['name']}: {rec['status']} in {rec['duration']}s")
        if ran_any or args.action == "run":
            build_data.build(vault, cfg, ROOT / "dashboard" / "data" / "data.js")
    finally:
        release_lock()


if __name__ == "__main__":
    main()
