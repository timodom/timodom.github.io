#!/usr/bin/env python3
"""Generates a fake vault and dashboard/data/sample-data.js for the demo.

    python3 scripts/make_sample.py [--vault /tmp/maps-sample-vault]
"""
import argparse
import datetime as dt
import json
import os
import random
import shutil
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
import build_data  # noqa: E402

AREAS = {
    "01 Business": ("business", 70, ["Offer", "Pricing", "Funnel", "Strategy", "Ops", "Hiring", "Brand"]),
    "02 Clients": ("clients", 45, ["Acme", "Northwind", "Globex", "Initech", "Umbrella", "Stark"]),
    "03 Career": ("career", 30, ["Interview", "Pipeline", "Resume", "Networking", "MurphyCobb"]),
    "04 Trading": ("trading", 55, ["Journal", "Setup", "Review", "Rules", "Playbook", "ES", "NQ"]),
    "05 Personal": ("personal", 25, ["Health", "Finance", "Family", "Reading", "Goals"]),
    "06 Content": ("content", 35, ["Post", "Draft", "Idea", "Script", "Newsletter"]),
    "Daily": ("daily", 40, []),
}
PEOPLE = ["Rob M.", "Kartik S.", "Dana", "Leo", "Priya", "Marcus"]
TASKS = ["Send one-page brief", "Follow up on proposal", "Review weekly P&L", "Update resume bullets",
         "Draft cold sequence v2", "Prep interview stories", "Reconcile invoices", "Record YouTube intro",
         "Rework the hook", "Book dentist", "Rebalance watchlist", "Write Friday review"]


def make_vault(vault):
    rnd = random.Random(7)
    if vault.exists():
        shutil.rmtree(vault)
    vault.mkdir(parents=True)
    today = dt.date.today()
    all_titles = []
    plan = []
    for folder, (area, n, words) in AREAS.items():
        (vault / folder).mkdir()
        for i in range(n):
            if folder == "Daily":
                title = (today - dt.timedelta(days=i)).isoformat()
            else:
                title = f"{rnd.choice(words)} {i + 1:02d}"
            plan.append((folder, title))
            all_titles.append(title)
    hubs = {}
    for folder, title in plan:
        hubs.setdefault(folder, title)
    now = time.time()
    for folder, title in plan:
        area = AREAS[folder][0]
        links = {hubs[folder]} if hubs[folder] != title else set()
        same = [t for f, t in plan if f == folder]
        links |= set(rnd.sample(same, min(len(same), rnd.randint(1, 3))))
        if rnd.random() < 0.35:
            links.add(rnd.choice(all_titles))
        links.discard(title)
        body = [f"# {title}", "", f"#{area}", ""]
        body += [f"- related: [[{l}]]" for l in sorted(links)]
        if rnd.random() < 0.22:
            due = today + dt.timedelta(days=rnd.randint(-6, 10))
            who = f" @{rnd.choice(PEOPLE)}" if rnd.random() < 0.5 else ""
            body += ["", f"- [ ] {rnd.choice(TASKS)}{who} 📅 {due.isoformat()}"]
        p = vault / folder / f"{title}.md"
        p.write_text("\n".join(body) + "\n", encoding="utf-8")
        age = rnd.expovariate(1 / 20) * 86400
        if folder == "Daily":
            age = (today - dt.date.fromisoformat(title)).days * 86400 + 3600
        os.utime(p, (now - age, now - age))
    hub_lines = ["# CLAUDE.md", ""] + [f"- [[{hubs[f]}]]  ({AREAS[f][0]})" for f in AREAS]
    (vault / "CLAUDE.md").write_text("\n".join(hub_lines) + "\n", encoding="utf-8")
    for name in ["Inbox", "Templates", "Dashboard notes", "Routines"]:
        (vault / f"{name}.md").write_text(f"# {name}\n\nSee [[CLAUDE]]\n", encoding="utf-8")
    d = vault / "Daily" / f"Digest {today.isoformat()}.md"
    d.write_text(f"# Digest {today.isoformat()}\n## Top 3 today\n- Send Rob the one-page brief\n"
                 "- 90 min deep work on the offer page\n- Friday review prep\n"
                 "## Overdue / due soon\n- Kartik: capability deck (2d overdue)\n"
                 "## Waiting on people\n- Dana: signed SOW\n## One thing to drop\n- Rebuilding the CRM this week\n",
                 encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--vault", default=os.path.join(os.environ.get("TMPDIR", "/tmp"), "maps-sample-vault"))
    args = ap.parse_args()
    vault = Path(args.vault)
    make_vault(vault)
    cfg = {
        "title": "MAPS", "subtitle": "Agentic OS", "owner": "Tim", "hub": "CLAUDE.md",
        "areas": {f: a for f, (a, _, _) in AREAS.items()},
        "focus": [{"label": "Deep work", "value": "3h 20m", "progress": 0.62},
                  {"label": "Gate review", "value": "in 14 d", "progress": 0.3}],
        "routines": [
            {"name": "vault-index", "time": "00:00", "command": "build_data"},
            {"name": "morning-digest", "time": "07:00", "prompt": "x"},
            {"name": "notes-ingest", "time": "09:30", "command": "true"},
            {"name": "tracker-row", "time": "20:30", "command": "true"},
            {"name": "vault-lint", "time": "21:15", "prompt": "x"},
        ],
    }
    out = ROOT / "dashboard" / "data" / "sample-data.js"
    data = build_data.build(vault, cfg, out)
    fake = {"vault-index": ("ok", 2), "morning-digest": ("ok", 95), "notes-ingest": ("ok", 243)}
    for r in data["routines"]:
        if r["name"] in fake:
            r["status"], r["duration"] = fake[r["name"]]
    data["system"] = {"cpu": 24, "ram": 62, "disk": 48}
    data["stats"]["runs_today"] = 3
    data["sample"] = True
    out.write_text("window.MAPS_DATA = " + json.dumps(data, ensure_ascii=False) + ";\n", encoding="utf-8")
    print(f"sample: {data['stats']['files']} notes, {data['stats']['links']} links -> {out}")


if __name__ == "__main__":
    main()
