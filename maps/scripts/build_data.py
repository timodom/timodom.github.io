#!/usr/bin/env python3
"""MAPS - Screen layer data builder.

Reads an Obsidian vault and writes dashboard/data/data.js, which the
dashboard loads. No Claude, no network, standard library only
(psutil is used for CPU/RAM if it happens to be installed).

    python3 scripts/build_data.py                # uses maps.yaml
    python3 scripts/build_data.py --vault ~/Notes
"""
import argparse
import datetime as dt
import json
import os
import re
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
from config import load_config  # noqa: E402

SKIP_DIRS = {".obsidian", ".trash", ".git", ".claude", "node_modules"}
WIKILINK = re.compile(r"\[\[([^\]|#^]+)(?:[#^][^\]|]*)?(?:\|[^\]]*)?\]\]")
MDLINK = re.compile(r"\]\(([^)\s]+\.md)\)")
TAG = re.compile(r"(?:^|\s)#([A-Za-z][\w/-]*)")
TASK = re.compile(r"^\s*[-*] \[( |x|X)\] (.+)$")
DUE = re.compile(r"(?:📅|due::?|@due\()\s*(\d{4}-\d{2}-\d{2})")
WHO = re.compile(r"(?:^|\s)@([A-Z][\w.]*(?: [A-Z]\.)?)")
PALETTE = ["#f5a623", "#4f8cff", "#34d399", "#a78bfa", "#f472b6",
           "#22d3ee", "#facc15", "#fb7185", "#94a3b8", "#60a5fa"]


def read_text(path):
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def frontmatter_tags(text):
    if not text.startswith("---"):
        return []
    end = text.find("\n---", 3)
    if end == -1:
        return []
    block = text[3:end]
    m = re.search(r"^tags:\s*\[(.*?)\]", block, re.M)
    if m:
        return [t.strip(" '\"#") for t in m.group(1).split(",") if t.strip()]
    m = re.search(r"^tags:\s*\n((?:\s*-\s*.+\n?)+)", block, re.M)
    if m:
        return [l.strip()[1:].strip(" '\"#") for l in m.group(1).splitlines() if l.strip()]
    return []


def scan_vault(vault, cfg):
    today = dt.date.today()
    hub_name = cfg.get("hub", "CLAUDE.md")
    area_map = {k.lower(): v for k, v in (cfg.get("areas") or {}).items()}
    files = []
    for dirpath, dirnames, filenames in os.walk(vault):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
        for fn in filenames:
            if fn.lower().endswith(".md"):
                files.append(Path(dirpath) / fn)

    by_stem = {}
    nodes = []
    for p in files:
        rel = p.relative_to(vault).as_posix()
        parts = rel.split("/")
        folder = parts[0] if len(parts) > 1 else ""
        area = area_map.get(folder.lower(), folder.lower() or "system")
        is_hub = rel == hub_name
        node = {
            "id": rel,
            "title": p.stem,
            "area": "hub" if is_hub else area,
            "path": rel,
            "mtime": int(p.stat().st_mtime),
            "size": p.stat().st_size,
        }
        nodes.append(node)
        by_stem.setdefault(p.stem.lower(), rel)
        by_stem.setdefault(rel[:-3].lower(), rel)

    links, tasks = [], []
    seen = set()
    for node in nodes:
        text = read_text(vault / node["path"])
        tags = set(frontmatter_tags(text)) | set(TAG.findall(text))
        node["tags"] = sorted(tags)[:12]
        targets = [t.strip() for t in WIKILINK.findall(text)]
        targets += [Path(t).with_suffix("").as_posix() for t in MDLINK.findall(text)]
        for t in targets:
            key = t.lower()
            dst = by_stem.get(key) or by_stem.get(key.split("/")[-1])
            if dst and dst != node["id"] and (node["id"], dst) not in seen:
                seen.add((node["id"], dst))
                links.append({"source": node["id"], "target": dst})
        for line in text.splitlines():
            m = TASK.match(line)
            if not m or m.group(1) != " ":
                continue
            body = m.group(2).strip()
            due = DUE.search(body)
            who = WHO.search(body)
            clean = DUE.sub("", body).replace("📅", "").strip(" )")
            item = {"text": clean[:160], "file": node["path"], "area": node["area"],
                    "due": due.group(1) if due else None,
                    "who": who.group(1) if who else None}
            if item["due"]:
                days = (dt.date.fromisoformat(item["due"]) - today).days
                item["days"] = days
            tasks.append(item)

    degree = {}
    for l in links:
        degree[l["source"]] = degree.get(l["source"], 0) + 1
        degree[l["target"]] = degree.get(l["target"], 0) + 1
    for n in nodes:
        n["degree"] = degree.get(n["id"], 0)
    return nodes, links, tasks


def build_areas(nodes, cfg):
    colors = cfg.get("colors") or {}
    counts = {}
    for n in nodes:
        if n["area"] != "hub":
            counts[n["area"]] = counts.get(n["area"], 0) + 1
    areas = []
    for i, (name, count) in enumerate(sorted(counts.items(), key=lambda kv: -kv[1])):
        areas.append({"id": name, "name": name, "count": count,
                      "color": colors.get(name, PALETTE[i % len(PALETTE)])})
    return areas


def activity(nodes, days=14):
    today = dt.date.today()
    buckets = {(today - dt.timedelta(days=i)).isoformat(): 0 for i in range(days)}
    for n in nodes:
        d = dt.date.fromtimestamp(n["mtime"]).isoformat()
        if d in buckets:
            buckets[d] += 1
    return [{"date": d, "edits": c} for d, c in sorted(buckets.items())]


def routines_status(cfg):
    log_path = ROOT / "logs" / "routines.jsonl"
    last = {}
    runs_today = 0
    today = dt.date.today().isoformat()
    if log_path.exists():
        for line in log_path.read_text(encoding="utf-8").splitlines()[-2000:]:
            try:
                rec = json.loads(line)
            except ValueError:
                continue
            last[rec["name"]] = rec
            if rec.get("claude") and rec.get("started", "").startswith(today):
                runs_today += 1
    out = []
    now = dt.datetime.now().strftime("%H:%M")
    for r in cfg.get("routines") or []:
        rec = last.get(r["name"], {})
        ran_today = rec.get("started", "").startswith(today)
        out.append({
            "name": r["name"],
            "time": r.get("time", "--:--"),
            "where": r.get("where", "MAC"),
            "claude": bool(r.get("prompt")),
            "enabled": r.get("enabled", True),
            "status": rec.get("status") if ran_today else ("due" if r.get("time", "99") <= now else "pending"),
            "duration": rec.get("duration") if ran_today else None,
            "last_run": rec.get("started"),
        })
    out.sort(key=lambda r: r["time"])
    return out, runs_today


def system_stats(vault):
    stats = {}
    try:
        du = shutil.disk_usage(vault)
        stats["disk"] = round(du.used / du.total * 100)
    except OSError:
        pass
    try:
        import psutil  # optional
        stats["cpu"] = round(psutil.cpu_percent(interval=0.3))
        stats["ram"] = round(psutil.virtual_memory().percent)
    except Exception:
        if hasattr(os, "getloadavg"):
            stats["cpu"] = min(100, round(os.getloadavg()[0] / (os.cpu_count() or 1) * 100))
    return stats


def latest_digest(vault, cfg):
    folder = vault / cfg.get("digest_folder", "Daily")
    if not folder.is_dir():
        return None
    files = sorted(folder.glob("Digest*.md"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not files:
        return None
    lines = [l for l in read_text(files[0]).splitlines() if l.strip() and not l.startswith("---")]
    return {"file": files[0].relative_to(vault).as_posix(), "lines": lines[:14]}


def build(vault, cfg, out_path):
    nodes, links, tasks = scan_vault(vault, cfg)
    routines, runs_today = routines_status(cfg)
    open_tasks = [t for t in tasks if t.get("due")]
    open_tasks.sort(key=lambda t: t["due"])
    data = {
        "generated_at": dt.datetime.now().isoformat(timespec="seconds"),
        "title": cfg.get("title", "MAPS"),
        "subtitle": cfg.get("subtitle", "Agentic OS"),
        "owner": cfg.get("owner", "you"),
        "vault": vault.name,
        "hub": cfg.get("hub", "CLAUDE.md"),
        "stats": {"files": len(nodes), "links": len(links), "runs_today": runs_today,
                  "open_tasks": len(tasks)},
        "areas": build_areas(nodes, cfg),
        "nodes": nodes,
        "links": links,
        "tasks": open_tasks[:40],
        "undated_tasks": len(tasks) - len(open_tasks),
        "activity": activity(nodes),
        "routines": routines,
        "system": system_stats(vault),
        "digest": latest_digest(vault, cfg),
        "focus": cfg.get("focus", []),
    }
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.with_suffix(".tmp")
    tmp.write_text("window.MAPS_DATA = " + json.dumps(data, ensure_ascii=False) + ";\n", encoding="utf-8")
    tmp.replace(out_path)
    return data


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--vault", help="path to the Obsidian vault (overrides maps.yaml)")
    ap.add_argument("--config", default=str(ROOT / "maps.yaml"))
    ap.add_argument("--out", default=str(ROOT / "dashboard" / "data" / "data.js"))
    args = ap.parse_args()
    cfg = load_config(args.config)
    vault = Path(os.path.expanduser(args.vault or cfg.get("vault", ""))).resolve()
    if not vault.is_dir():
        sys.exit(f"Vault not found: {vault}  (set 'vault' in maps.yaml or pass --vault)")
    data = build(vault, cfg, Path(args.out))
    s = data["stats"]
    print(f"MAPS: {s['files']} notes, {s['links']} links, {s['open_tasks']} open tasks -> {args.out}")


if __name__ == "__main__":
    main()
