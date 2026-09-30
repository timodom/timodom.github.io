# MAPS: a local AI operating system

**M**emory · **A**gent · **P**ools · **S**creen. It runs entirely on your laptop. There's no VPS, no API key and no subscription beyond Claude Pro.

| Layer | What it is | File(s) |
|---|---|---|
| **Memory** | `CLAUDE.md` at the root of your Obsidian vault: a ~40-line map that tells Claude where things live | `templates/CLAUDE.md` |
| **Agent** | Claude Code, run from inside your vault, with deny rules and an auto-refresh hook | `templates/claude-settings.json` |
| **Pools** | Scheduled routines (morning digest, nightly lint), with a lock, a timeout and a daily cap on Claude runs | `maps.yaml`, `scripts/routines.py`, `prompts/` |
| **Screen** | A read-only dashboard with six views: Rings, Circle, Areas, Links, Timeline, 3D Orbit | `dashboard/`, `scripts/build_data.py` |

The dashboard never calls Claude. It only reads `dashboard/data/data.js`, which `build_data.py` writes by scanning your vault. Claude's role is to edit notes. When notes change, the screen updates.

## Quick look (2 minutes)

Open `dashboard/index.html` in your browser. With no vault connected yet, it shows **sample data** (orange badge top-right).

## Setup (about 20 minutes)

Requirements: Python 3.9+, `pip install pyyaml` (and `pip install psutil` if you want CPU/RAM on the System panel), and Claude Code logged in with your Pro account.

1. **Config**
   ```bash
   cp maps.example.yaml maps.yaml
   ```
   Set `vault:` to your Obsidian vault folder. Map your top-level folders to area names under `areas:`.

2. **Memory**: copy `templates/CLAUDE.md` to the root of your vault and rewrite it for your real folders. Keep it short: it's a map, not a notebook.

3. **Build the dashboard data**
   ```bash
   python3 scripts/build_data.py
   ```
   Refresh `dashboard/index.html`. The sample badge disappears and you see your own vault.

4. **Agent guardrails (recommended)**: copy `templates/claude-settings.json` to `<vault>/.claude/settings.json`. Replace `/FULL/PATH/TO/maps` with this folder's real path. This does two things:
   - blocks `rm`, `mv`, `curl`, `git push` and web fetches for Claude in your vault
   - rebuilds the dashboard every time Claude edits a file, so the screen stays live while you work

5. **Pools (routines)**: try one by hand first:
   ```bash
   python3 scripts/routines.py list
   python3 scripts/routines.py run morning-digest
   ```
   Then schedule `tick` every 5 minutes. Each routine runs at most once a day, after its time. If your laptop was asleep at 07:00, the digest runs on the first tick after it wakes.

   **Mac / Linux** (`crontab -e`):
   ```
   */5 * * * * cd /FULL/PATH/TO/maps && /usr/bin/python3 scripts/routines.py tick >> logs/cron.log 2>&1
   ```
   On a Mac, cron needs Full Disk Access if your vault is in Documents or iCloud (System Settings → Privacy & Security → Full Disk Access → add `/usr/sbin/cron`). If `claude` isn't found under cron, set `claude_bin:` in `maps.yaml` to the output of `which claude`.

   **Windows** (Task Scheduler → Create Task → trigger "Daily, repeat every 5 minutes"):
   `Program: python`, `Arguments: scripts\routines.py tick`, `Start in: C:\path\to\maps`

## Using it day to day

- Work in Obsidian as usual. Open Claude Code from your vault folder (`cd <vault> && claude`) when you want the agent.
- Keep the dashboard open in a browser tab. It re-reads its data every 60 seconds.
- Click a dot to see its links, then **Open in Obsidian** to jump to the note.
- Chips filter by area. Search highlights matching notes (Enter selects the first match, Esc clears).
- Tasks written as `- [ ] Call back @Rob M. 📅 2026-10-02` feed the "Needs you" panel.

To show the dashboard inside Obsidian, install the **Custom Frames** community plugin and point it at the dashboard file.

## Quota safety (Claude Pro)

- Idle costs nothing. Only routines with a `prompt:` use Claude, and `limits.claude_runs_per_day` caps them.
- There are no retries. A failed or timed-out routine is logged and waits until tomorrow.
- Headless runs use an explicit tool allowlist (`allowed_tools`). The default allows reading anywhere and writing only in `Daily/`.
- Claude usage is shared across claude.ai, Claude Code on this laptop and these routines. Keep routines to 1–2 a day.

## Privacy

`maps.yaml`, `logs/` and `dashboard/data/data.js` are git-ignored, so your real notes never get committed. Only the sample data (from `scripts/make_sample.py`) lives in the repo.

## Later (optional)

When you want routines to run while the laptop sleeps, this same folder can move to a VPS unchanged. Only `vault:` in `maps.yaml` changes. That's a later decision, not something to do now.
