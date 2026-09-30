/* MAPS dashboard: read-only view over window.MAPS_DATA (written by scripts/build_data.py). */
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const ACCENT = "#ff7a1a";
  const REFRESH_MS = 60_000;

  const state = { data: null, view: "rings", area: null, query: "", selected: null, stamp: null };
  let idx = null;          // { byId, nbrs, color }
  let viewCtx = null;      // current 2D view: { nodeSel, pos, linkLayer, zoom, svg }
  let orbitCtx = null;     // current 3D view
  const layoutCache = {};  // per-data force layout

  // ---------- data loading ----------
  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = src + "?t=" + Date.now();
      s.onload = () => { s.remove(); resolve(); };
      s.onerror = () => { s.remove(); reject(new Error(src)); };
      document.head.appendChild(s);
    });
  }

  async function loadData() {
    window.MAPS_DATA = undefined;
    try { await loadScript("data/data.js"); } catch (_) { /* fall through to sample */ }
    if (!window.MAPS_DATA) {
      try { await loadScript("data/sample-data.js"); } catch (_) { /* nothing */ }
      if (window.MAPS_DATA) window.MAPS_DATA.sample = true;
    }
    return window.MAPS_DATA;
  }

  async function refresh(force) {
    const data = await loadData();
    if (!data) {
      $("canvas").innerHTML = '<p class="empty" style="padding:24px">No data yet. Run <code>python3 scripts/build_data.py</code>.</p>';
      return;
    }
    if (!force && data.generated_at === state.stamp) return;
    state.stamp = data.generated_at;
    state.data = data;
    indexData(data);
    renderPanels();
    renderView();
  }

  function indexData(d) {
    const byId = new Map(d.nodes.map((n) => [n.id, n]));
    const nbrs = new Map(d.nodes.map((n) => [n.id, new Set()]));
    d.links.forEach((l) => { nbrs.get(l.source)?.add(l.target); nbrs.get(l.target)?.add(l.source); });
    const colors = new Map(d.areas.map((a) => [a.id, a.color]));
    colors.set("hub", ACCENT);
    idx = { byId, nbrs, color: (a) => colors.get(a) || "#94a3b8" };
  }

  // ---------- helpers ----------
  const fmtAgo = (ts) => {
    const s = Date.now() / 1000 - ts;
    if (s < 3600) return Math.max(1, Math.round(s / 60)) + "m ago";
    if (s < 86400) return Math.round(s / 3600) + "h ago";
    return Math.round(s / 86400) + "d ago";
  };
  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const size = () => { const el = $("canvas"); return { w: el.clientWidth, h: el.clientHeight }; };
  const nodeR = (n) => (n.area === "hub" ? 0 : Math.min(5, 1.6 + Math.sqrt(n.degree) * 0.55));

  function isActive(n) {
    if (n.area === "hub") return true;
    if (state.area && n.area !== state.area) return false;
    if (state.query) {
      const q = state.query.toLowerCase();
      return n.title.toLowerCase().includes(q) || n.path.toLowerCase().includes(q) ||
        (n.tags || []).some((t) => t.toLowerCase().includes(q));
    }
    return true;
  }

  // ---------- panels ----------
  function renderPanels() {
    const d = state.data;
    document.title = `${d.title} Dashboard`;
    $("title").textContent = d.title;
    $("subtitle").textContent = d.subtitle || "";
    $("sampleBadge").hidden = !d.sample;
    $("needsTitle").textContent = `Needs ${d.owner || "you"}`;

    const ageMin = (Date.now() - new Date(d.generated_at).getTime()) / 60000;
    const okRuns = d.routines.filter((r) => r.status === "ok").length;
    const failed = d.routines.some((r) => ["failed", "error", "timeout"].includes(r.status));
    $("status").innerHTML = [
      ["Memory", "on"], ["Routines " + okRuns + "/" + d.routines.length, failed ? "warn" : "on"],
      ["Claude runs " + (d.stats.runs_today || 0), "accent"], [ageMin < 20 ? "Synced" : "Stale data", ageMin < 20 ? "on" : "warn"],
    ].map(([t, c]) => `<span><i class="dot ${c}"></i>${esc(t)}</span>`).join("");

    $("stats").innerHTML = `<b>${d.stats.files}</b> files · <b>${d.stats.links}</b> links · <b>${d.stats.open_tasks}</b> open tasks`;
    $("updated").textContent = "Data " + fmtAgo(new Date(d.generated_at).getTime() / 1000);

    // chips
    const chips = [`<span class="chip ${state.area ? "" : "active"}" data-area=""><i style="background:${ACCENT}"></i>all</span>`]
      .concat(d.areas.map((a) => `<span class="chip ${state.area === a.id ? "active" : ""}" data-area="${esc(a.id)}" style="${state.area === a.id ? "color:" + a.color : ""}"><i style="background:${a.color}"></i>${esc(a.name)} <small>${a.count}</small></span>`));
    $("chips").innerHTML = chips.join("");

    // focus metrics
    const focus = (d.focus && d.focus.length ? d.focus : []).slice(0, 2);
    const today = new Date().toISOString().slice(0, 10);
    const editsToday = (d.activity.find((a) => a.date === today) || {}).edits || 0;
    const metrics = focus.map((f) => ({ label: f.label, value: f.value, p: f.progress ?? 0 }));
    if (metrics.length < 2) metrics.push({ label: "Edits today", value: String(editsToday), p: Math.min(1, editsToday / 20) });
    if (metrics.length < 2) metrics.push({ label: "Open tasks", value: String(d.stats.open_tasks), p: 1 });
    $("focus").innerHTML = metrics.map((m) => `<div class="metric"><label>${esc(m.label)}</label><b>${esc(m.value)}</b><div class="bar"><i style="width:${Math.round(m.p * 100)}%"></i></div></div>`).join("");

    // 14-day activity
    const act = d.activity.slice(-14);
    const max = Math.max(1, ...act.map((a) => a.edits));
    const shade = d3.scaleLinear().domain([0, max]).range(["#232937", ACCENT]);
    const names = act.slice(0, 7).map((a) => new Date(a.date + "T12:00").toLocaleDateString(undefined, { weekday: "short" }));
    $("week").innerHTML = names.map((n) => `<div class="dname">${n}</div>`).join("") +
      act.map((a) => `<div class="cell" title="${a.date}: ${a.edits} edits" style="background:${a.edits ? shade(a.edits) : ""}"></div>`).join("");
    const total = act.reduce((s, a) => s + a.edits, 0);
    const peak = act.reduce((p, a) => (a.edits > p.edits ? a : p), act[0] || { edits: 0 });
    $("weekTotal").textContent = total + " edits";
    $("weekPeak").textContent = peak && peak.edits ? "Peak " + peak.date.slice(5) : "";

    // digest
    if (d.digest) {
      $("digestFile").textContent = d.digest.file.split("/").pop().replace(".md", "").replace(/^Digest\s*/, "");
      $("digest").innerHTML = d.digest.lines.map((l) => {
        if (l.startsWith("# ")) return "";
        if (l.startsWith("#")) return `<h4>${esc(l.replace(/^#+\s*/, ""))}</h4>`;
        return `<p>${esc(l.replace(/^[-*]\s*/, "• ").replace(/\[\[([^\]|]+)(\|[^\]]+)?\]\]/g, "$1"))}</p>`;
      }).join("");
    } else {
      $("digestFile").textContent = "";
      $("digest").innerHTML = '<p class="empty">No digest yet. It appears after the morning-digest routine runs.</p>';
    }

    // needs you
    const overdue = d.tasks.filter((t) => t.days < 0);
    $("needsCount").textContent = overdue.length;
    const soon = d.tasks.filter((t) => t.days <= 2).slice(0, 7);
    $("tasks").innerHTML = soon.length ? soon.map((t) => {
      const when = t.days < 0 ? `${-t.days}d late` : t.days === 0 ? "today" : `in ${t.days}d`;
      const who = t.who || t.file.split("/").pop().replace(/\.md$/, "");
      const txt = t.who ? t.text.replace("@" + t.who, "").trim() : t.text;
      return `<div class="task ${t.days < 0 ? "over" : ""}" data-file="${esc(t.file)}"><span class="flag">⚑</span>
        <div><div class="who">${esc(who)}</div><div class="txt">${esc(txt)}</div></div><span class="when">${when}</span></div>`;
    }).join("") : '<p class="empty">Nothing overdue. Nice.</p>';

    // routines
    const now = new Date().toTimeString().slice(0, 5);
    const next = d.routines.find((r) => r.enabled && r.time > now && r.status !== "ok");
    const done = d.routines.filter((r) => r.status === "ok").length;
    $("routineCount").textContent = `${done}/${d.routines.length} today`;
    const icon = { ok: "✓", failed: "✗", error: "✗", timeout: "⏱", capped: "CAP", pending: "–", due: "…" };
    $("routines").innerHTML = d.routines.map((r) => {
      const isNext = r === next;
      const st = !r.enabled ? "off" : isNext ? "next" : r.status;
      const label = !r.enabled ? "off" : isNext ? "NEXT" : icon[r.status] || r.status;
      return `<tr class="${isNext ? "next" : ""}"><td class="t">${esc(r.time)}</td>
        <td>${esc(r.name)}<span class="tag ${r.claude ? "ai" : ""}">${r.claude ? "CLAUDE" : esc(r.where || "LOCAL")}</span>${r.duration != null ? `<span class="dur">${r.duration}s</span>` : ""}</td>
        <td class="s ${st}">${label}</td></tr>`;
    }).join("") || '<tr><td colspan="3" class="empty">No routines in maps.yaml</td></tr>';

    // system
    const sys = d.system || {};
    const keys = ["cpu", "ram", "disk"].filter((k) => sys[k] != null);
    $("system").innerHTML = keys.map((k) => `<div><label><span>${k.toUpperCase()}</span><span>${sys[k]}%</span></label><div class="bar"><i class="${k}" style="width:${sys[k]}%"></i></div></div>`).join("") || '<p class="empty">No stats</p>';
    $("nominal").textContent = keys.every((k) => sys[k] < 85) ? "● All systems nominal" : "";
  }

  function tickClock() {
    const now = new Date();
    const hm = now.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
    const date = now.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short", year: "numeric" });
    $("clock").textContent = hm;
    $("date").textContent = date;
    $("todayTime").textContent = hm;
    $("todayDate").textContent = date;
    $("todayMeta").textContent = Intl.DateTimeFormat().resolvedOptions().timeZone.replace("_", " ");
    const h = (now.getHours() % 12) + now.getMinutes() / 60, m = now.getMinutes();
    const hand = (a, len, w, c) => `<line x1="0" y1="0" x2="${Math.sin(a) * len}" y2="${-Math.cos(a) * len}" stroke="${c}" stroke-width="${w}" stroke-linecap="round"/>`;
    $("dial").innerHTML = `<circle r="21" fill="none" stroke="#232937" stroke-width="2"/>` +
      hand((h / 12) * Math.PI * 2, 11, 2.2, "#e6e8ee") + hand((m / 60) * Math.PI * 2, 16, 1.4, ACCENT) + `<circle r="2" fill="${ACCENT}"/>`;
  }

  // ---------- 2D view scaffolding ----------
  function teardown() {
    if (orbitCtx) { orbitCtx.stop(); orbitCtx = null; }
    viewCtx = null;
    $("canvas").innerHTML = "";
  }

  function makeSvg() {
    const { w, h } = size();
    const svg = d3.select("#canvas").append("svg").attr("viewBox", [-w / 2, -h / 2, w, h]);
    const root = svg.append("g");
    const zoom = d3.zoom().scaleExtent([0.4, 8]).on("zoom", (e) => root.attr("transform", e.transform));
    svg.call(zoom).on("dblclick.zoom", null);
    svg.on("click", (e) => { if (e.target === svg.node()) select(null); });
    return { svg, root, zoom, w, h, R: Math.max(120, Math.min(w, h) / 2 - 24) };
  }

  function drawNodes(layer, nodes, pos) {
    const sel = layer.selectAll("circle.node").data(nodes.filter((n) => n.area !== "hub"), (n) => n.id)
      .join("circle").attr("class", "node")
      .attr("cx", (n) => pos.get(n.id)[0]).attr("cy", (n) => pos.get(n.id)[1])
      .attr("r", nodeR).attr("fill", (n) => idx.color(n.area));
    bindNodeEvents(sel);
    return sel;
  }

  function drawHub(layer, label, r = 16) {
    const g = layer.append("g").attr("class", "hub").style("cursor", "pointer");
    const hex = d3.range(6).map((i) => [Math.cos(Math.PI / 3 * i - Math.PI / 2) * r, Math.sin(Math.PI / 3 * i - Math.PI / 2) * r]);
    g.append("circle").attr("r", r * 2.2).attr("fill", ACCENT).attr("opacity", 0.08);
    g.append("polygon").attr("points", hex.join(" ")).attr("fill", "#1a1206").attr("stroke", ACCENT).attr("stroke-width", 1.5);
    g.append("circle").attr("r", r * 0.3).attr("fill", ACCENT);
    g.append("text").attr("y", r + 14).attr("text-anchor", "middle").attr("class", "area-label").text(label);
    const hub = state.data.nodes.find((n) => n.area === "hub");
    if (hub) g.on("click", (e) => { e.stopPropagation(); select(hub.id); });
    return g;
  }

  function bindNodeEvents(sel) {
    const tip = $("tooltip");
    sel.on("mouseenter", (e, n) => {
      tip.style.display = "block";
      tip.innerHTML = `<b>${esc(n.title)}</b><span>${esc(n.area)} · ${n.degree} links · edited ${fmtAgo(n.mtime)}</span><span>${esc(n.path)}</span>`;
      if (!state.selected) showLinks(n.id);
    }).on("mousemove", (e) => {
      tip.style.left = e.clientX + 14 + "px"; tip.style.top = e.clientY + 12 + "px";
    }).on("mouseleave", () => {
      tip.style.display = "none";
      if (!state.selected) showLinks(null);
    }).on("click", (e, n) => { e.stopPropagation(); select(n.id); });
  }

  // curved link overlay used by the radial views
  function showLinks(id) {
    if (!viewCtx || !viewCtx.linkLayer) return;
    const { linkLayer, pos } = viewCtx;
    const links = id ? state.data.links.filter((l) => l.source === id || l.target === id) : [];
    linkLayer.selectAll("path").data(links, (l) => l.source + ">" + l.target).join("path")
      .attr("class", "link").attr("stroke", (l) => idx.color(idx.byId.get(l.source)?.area)).attr("opacity", 0.8)
      .attr("d", (l) => curve(pos.get(l.source), pos.get(l.target), viewCtx.bend ?? 0.35));
  }

  function curve(a, b, bend) {
    if (!a || !b) return null;
    const mx = (a[0] + b[0]) / 2 * (1 - bend), my = (a[1] + b[1]) / 2 * (1 - bend);
    return `M${a[0]},${a[1]}Q${mx},${my} ${b[0]},${b[1]}`;
  }

  function applyHighlight() {
    if (orbitCtx) { orbitCtx.highlight(); return; }
    if (!viewCtx) return;
    const sel = state.selected;
    const nb = sel ? idx.nbrs.get(sel) : null;
    viewCtx.nodeSel.classed("dim", (n) => !isActive(n) || (sel && n.id !== sel && !nb.has(n.id)))
      .attr("stroke", (n) => (n.id === sel ? "#fff" : null)).attr("stroke-width", 1.5);
    if (viewCtx.allLinks) {
      viewCtx.allLinks.attr("opacity", (l) => {
        if (sel) return l.source === sel || l.target === sel ? 0.9 : 0.02;
        const a = idx.byId.get(l.source), b = idx.byId.get(l.target);
        return isActive(a) && isActive(b) ? viewCtx.linkOpacity : 0.02;
      });
    }
    showLinks(sel);
  }

  // ---------- views ----------
  const views = {
    rings() {
      const d = state.data, s = makeSvg(), { root, R } = s;
      const rIn = R * 0.3, rOut = R * 0.86;
      [R * 0.18, rOut + 6, R * 0.94].forEach((r, i) => root.append("circle").attr("class", "guide" + (i === 2 ? " accent" : "")).attr("r", r));
      root.append("text").attr("class", "ring-label").attr("y", -R * 0.94 - 6).attr("text-anchor", "middle").text("Routines");
      root.append("text").attr("class", "ring-label").attr("y", -rOut - 12).attr("text-anchor", "middle").attr("fill", "#a78bfa").style("fill", "#a78bfa").text("Memory");
      const linkLayer = root.append("g");
      const pos = new Map();
      const areas = d.areas;
      const gap = 0.06, span = Math.PI * 2 - gap * areas.length;
      const total = d3.sum(areas, (a) => a.count) || 1;

      // largest spacing where every area fits between rIn and rOut
      const fits = (sp) => areas.every((a) => {
        const ang = span * a.count / total; let left = a.count;
        for (let r = rIn; r <= rOut && left > 0; r += sp) left -= Math.max(1, Math.floor(r * ang / sp));
        return left <= 0;
      });
      let sp = 26; while (sp > 3 && !fits(sp)) sp -= 0.5;
      const dotR = (n) => Math.min(sp * 0.42, 1.8 + Math.sqrt(n.degree) * 0.7);

      let a0 = -Math.PI / 2;
      const labels = [];
      areas.forEach((a) => {
        const ang = span * a.count / total;
        const members = d.nodes.filter((n) => n.area === a.id).sort((x, y) => y.degree - x.degree);
        let i = 0, rMax = rIn;
        for (let r = rIn; i < members.length; r += sp) {
          rMax = r;
          const cap = Math.max(1, Math.floor(r * ang / sp));
          const row = members.slice(i, i + cap);
          row.forEach((n, k) => {
            const t = a0 + ang * (row.length === 1 ? 0.5 : (k + 0.5) / cap + (cap - row.length) / (2 * cap));
            pos.set(n.id, [Math.cos(t) * r, Math.sin(t) * r]);
          });
          i += cap;
        }
        const mid = a0 + ang / 2;
        labels.push({ a, x: Math.cos(mid) * (rIn - 18), y: Math.sin(mid) * (rIn - 18), lx: Math.cos(mid) * (rMax + 16), ly: Math.sin(mid) * (rMax + 16) });
        a0 += ang + gap;
      });
      const hub = d.nodes.find((n) => n.area === "hub");
      if (hub) pos.set(hub.id, [0, 0]);

      root.append("g").selectAll("line").data(labels).join("line")
        .attr("x2", (l) => l.x).attr("y2", (l) => l.y).attr("stroke", (l) => l.a.color).attr("opacity", 0.25);
      const nodeSel = drawNodes(root.append("g"), d.nodes, pos).attr("r", dotR);
      const lab = root.append("g").selectAll("g").data(labels).join("g").attr("transform", (l) => `translate(${l.x},${l.y})`)
        .style("cursor", "pointer").on("click", (e, l) => { e.stopPropagation(); setArea(state.area === l.a.id ? null : l.a.id); });
      lab.append("circle").attr("r", 8).attr("fill", (l) => l.a.color);
      lab.append("text").attr("text-anchor", "middle").attr("dy", 3.5).attr("fill", "#0b0d12").style("font", "700 9px Inter,sans-serif").text((l) => l.a.name[0].toUpperCase());
      root.append("g").selectAll("text").data(labels).join("text").attr("class", "area-label")
        .attr("x", (l) => l.lx).attr("y", (l) => l.ly).attr("dy", 3.5).style("fill", (l) => l.a.color)
        .attr("text-anchor", (l) => (Math.abs(l.lx) < 30 ? "middle" : l.lx < 0 ? "end" : "start")).text((l) => `${l.a.name} · ${l.a.count}`);

      // routines on the outer ring
      const rs = d.routines;
      const rg = root.append("g").selectAll("g").data(rs).join("g")
        .attr("transform", (r, i) => { const t = -Math.PI / 2 + (i + 0.5) * (Math.PI * 2 / Math.max(rs.length, 1)); return `translate(${Math.cos(t) * R * 0.94},${Math.sin(t) * R * 0.94})`; });
      rg.append("circle").attr("r", 6).attr("fill", "#11141b").attr("stroke", (r) => (r.status === "ok" ? "#34d399" : r.status === "pending" ? "#5b6376" : ["failed", "error", "timeout"].includes(r.status) ? "#f87171" : ACCENT));
      rg.append("circle").attr("r", 2).attr("fill", (r) => (r.claude ? ACCENT : "#8a93a6"));
      rg.append("title").text((r) => `${r.time} ${r.name} (${r.status})`);
      drawHub(root, d.hub.replace(".md", "").toUpperCase() + ".MD");
      return { ...s, nodeSel, pos, linkLayer, bend: 0.5 };
    },

    circle() {
      const d = state.data, s = makeSvg(), { root, R } = s;
      const order = d.areas.map((a) => a.id);
      const nodes = d.nodes.filter((n) => n.area !== "hub").sort((a, b) => order.indexOf(a.area) - order.indexOf(b.area) || b.degree - a.degree);
      const r = R * 0.82, pos = new Map();
      nodes.forEach((n, i) => { const t = (i / nodes.length) * Math.PI * 2 - Math.PI / 2; pos.set(n.id, [Math.cos(t) * r, Math.sin(t) * r]); });
      const hub = d.nodes.find((n) => n.area === "hub"); if (hub) pos.set(hub.id, [0, 0]);
      const links = d.links.filter((l) => pos.has(l.source) && pos.has(l.target));
      const allLinks = root.append("g").selectAll("path").data(links).join("path").attr("class", "link")
        .attr("stroke", (l) => idx.color(idx.byId.get(l.source).area)).attr("d", (l) => curve(pos.get(l.source), pos.get(l.target), 0.85));
      const nodeSel = drawNodes(root.append("g"), nodes, pos);
      let start = 0;
      d.areas.forEach((a) => {
        const t = ((start + a.count / 2) / nodes.length) * Math.PI * 2 - Math.PI / 2;
        const t0 = (start / nodes.length) * Math.PI * 2 - Math.PI / 2, t1 = ((start + a.count) / nodes.length) * Math.PI * 2 - Math.PI / 2;
        root.append("path").attr("d", d3.arc()({ innerRadius: r + 10, outerRadius: r + 13, startAngle: t0 + Math.PI / 2 + 0.01, endAngle: t1 + Math.PI / 2 - 0.01 })).attr("fill", a.color);
        const x = Math.cos(t) * (r + 26), y = Math.sin(t) * (r + 26);
        root.append("text").attr("class", "area-label").attr("x", x).attr("y", y).attr("dy", 3)
          .attr("text-anchor", Math.abs(x) < 20 ? "middle" : x < 0 ? "end" : "start").style("fill", a.color).text(a.name);
        start += a.count;
      });
      drawHub(root, "", 10);
      return { ...s, nodeSel, pos, allLinks, linkOpacity: 0.12 };
    },

    areas() {
      const d = state.data, s = makeSvg(), { root, R } = s;
      const tree = { children: d.areas.map((a) => ({ area: a, children: d.nodes.filter((n) => n.area === a.id).map((n) => ({ node: n })) })) };
      const h = d3.hierarchy(tree).sum((x) => (x.node ? 1 + x.node.degree : 0)).sort((a, b) => b.value - a.value);
      d3.pack().size([R * 2, R * 2]).padding((x) => (x.depth === 0 ? 14 : 2))(h);
      const pos = new Map();
      h.leaves().forEach((l) => { pos.set(l.data.node.id, [l.x - R, l.y - R]); });
      const groups = h.children || [];
      root.append("g").selectAll("circle").data(groups).join("circle").attr("cx", (g) => g.x - R).attr("cy", (g) => g.y - R).attr("r", (g) => g.r)
        .attr("fill", (g) => g.data.area.color).attr("fill-opacity", 0.05).attr("stroke", (g) => g.data.area.color).attr("stroke-opacity", 0.35)
        .style("cursor", "pointer").on("click", (e, g) => { e.stopPropagation(); setArea(state.area === g.data.area.id ? null : g.data.area.id); });
      const linkLayer = root.append("g");
      const leafR = new Map(h.leaves().map((l) => [l.data.node.id, l.r]));
      const nodeSel = drawNodes(root.append("g"), d.nodes, pos).attr("r", (n) => Math.max(1.5, (leafR.get(n.id) || 2) * 0.85));
      root.append("g").selectAll("text").data(groups).join("text").attr("class", "area-label").attr("text-anchor", "middle")
        .attr("x", (g) => g.x - R).attr("y", (g) => g.y - R - g.r - 5).style("fill", (g) => g.data.area.color)
        .text((g) => `${g.data.area.name} · ${g.data.area.count}`);
      return { ...s, nodeSel, pos, linkLayer, bend: 0 };
    },

    links() {
      const d = state.data, s = makeSvg(), { root, R } = s;
      const key = d.generated_at + ":" + Math.round(R);
      let pos = layoutCache[key];
      if (!pos) {
        const anchor = new Map(d.areas.map((a, i) => { const t = (i / d.areas.length) * Math.PI * 2 - Math.PI / 2; return [a.id, [Math.cos(t) * R * 0.6, Math.sin(t) * R * 0.6]]; }));
        const sim = d.nodes.map((n) => ({ id: n.id, area: n.area, ...(n.area === "hub" ? { fx: 0, fy: 0 } : {}) }));
        const links = d.links.map((l) => ({ source: l.source, target: l.target }));
        const f = d3.forceSimulation(sim).stop()
          .force("link", d3.forceLink(links).id((n) => n.id).distance(30).strength(0.12))
          .force("charge", d3.forceManyBody().strength(-26).distanceMax(200))
          .force("x", d3.forceX((n) => (anchor.get(n.area) || [0, 0])[0]).strength(0.07))
          .force("y", d3.forceY((n) => (anchor.get(n.area) || [0, 0])[1]).strength(0.07))
          .force("collide", d3.forceCollide(5));
        for (let i = 0; i < 260; i++) f.tick();
        pos = layoutCache[key] = new Map(sim.map((n) => [n.id, [n.x, n.y]]));
      }
      const allLinks = root.append("g").selectAll("line").data(d.links).join("line").attr("class", "link")
        .attr("stroke", (l) => idx.color(idx.byId.get(l.source).area))
        .attr("x1", (l) => pos.get(l.source)[0]).attr("y1", (l) => pos.get(l.source)[1])
        .attr("x2", (l) => pos.get(l.target)[0]).attr("y2", (l) => pos.get(l.target)[1]);
      const nodeSel = drawNodes(root.append("g"), d.nodes, pos);
      drawHub(root, "", 9);
      return { ...s, nodeSel, pos, allLinks, linkOpacity: 0.18 };
    },

    timeline() {
      const d = state.data, s = makeSvg(), { root, w, h } = s;
      const now = new Date(d.generated_at), start = d3.timeDay.offset(now, -90);
      const x = d3.scaleTime().domain([start, now]).range([-w / 2 + 110, w / 2 - 24]).clamp(true);
      const lanes = d.areas.map((a) => a.id);
      const y = d3.scaleBand().domain(lanes).range([-h / 2 + 20, h / 2 - 76]).padding(0.25);
      root.append("g").attr("class", "axis").attr("transform", `translate(0,${h / 2 - 66})`).call(d3.axisBottom(x).ticks(Math.max(3, Math.floor(w / 110))).tickSizeOuter(0));
      root.append("g").selectAll("line").data(lanes).join("line").attr("class", "guide").attr("x1", -w / 2 + 110).attr("x2", w / 2 - 24)
        .attr("y1", (l) => y(l) + y.bandwidth() / 2).attr("y2", (l) => y(l) + y.bandwidth() / 2);
      root.append("g").selectAll("text").data(d.areas).join("text").attr("class", "area-label").attr("x", -w / 2 + 16)
        .attr("y", (a) => y(a.id) + y.bandwidth() / 2 + 3).style("fill", (a) => a.color).text((a) => `${a.name} · ${a.count}`);
      const pos = new Map();
      const jitter = (id) => { let hsh = 0; for (const c of id) hsh = (hsh * 31 + c.charCodeAt(0)) | 0; return ((hsh >>> 0) % 1000) / 1000 - 0.5; };
      d.nodes.forEach((n) => { if (n.area !== "hub") pos.set(n.id, [x(new Date(n.mtime * 1000)), y(n.area) + y.bandwidth() / 2 + jitter(n.id) * y.bandwidth() * 0.9]); });
      root.append("line").attr("x1", x(now)).attr("x2", x(now)).attr("y1", -h / 2 + 10).attr("y2", h / 2 - 66).attr("stroke", ACCENT).attr("stroke-dasharray", "3 3");
      root.append("text").attr("class", "ring-label").attr("x", x(now) - 4).attr("y", -h / 2 + 12).attr("text-anchor", "end").text("now");
      const linkLayer = root.append("g");
      const nodeSel = drawNodes(root.append("g"), d.nodes, pos);
      return { ...s, nodeSel, pos, linkLayer, bend: 0 };
    },

    orbit() { return startOrbit(); },
  };

  // ---------- 3D orbit ----------
  function startOrbit() {
    const el = $("canvas"), { w, h } = size(), d = state.data;
    if (!window.THREE) { el.innerHTML = '<p class="empty" style="padding:24px">3D view could not load vendor/three.min.js.</p>'; return null; }
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(2, window.devicePixelRatio));
    renderer.setSize(w, h);
    el.appendChild(renderer.domElement);
    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(50, w / h, 1, 5000);
    const far = 70 + d.areas.length * 34;
    const home = () => camera.position.set(0, far * 0.95, far * 1.55);
    home();
    const controls = THREE.OrbitControls ? new THREE.OrbitControls(camera, renderer.domElement) : null;
    if (controls) { controls.enableDamping = true; controls.minDistance = 80; controls.maxDistance = 1400; }

    const sun = new THREE.Mesh(new THREE.SphereGeometry(12, 32, 32), new THREE.MeshBasicMaterial({ color: ACCENT }));
    scene.add(sun);
    [22, 34].forEach((r, i) => scene.add(new THREE.Mesh(new THREE.SphereGeometry(r, 32, 32),
      new THREE.MeshBasicMaterial({ color: ACCENT, transparent: true, opacity: i ? 0.04 : 0.09 }))));
    scene.add(labelSprite(d.hub.toUpperCase(), ACCENT, 0, -26, 0));

    const groups = d.areas.map((a, i) => {
      const radius = 70 + i * 34;
      const ring = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(d3.range(128).map((k) => new THREE.Vector3(Math.cos(k / 128 * Math.PI * 2) * radius, 0, Math.sin(k / 128 * Math.PI * 2) * radius))),
        new THREE.LineBasicMaterial({ color: 0x232937 }));
      scene.add(ring);
      const g = new THREE.Group();
      g.rotation.y = i * 2.4;
      const color = new THREE.Color(a.color);
      const planet = new THREE.Mesh(new THREE.SphereGeometry(4 + Math.sqrt(a.count) * 0.6, 24, 24), new THREE.MeshBasicMaterial({ color, transparent: true }));
      planet.position.set(radius, 0, 0);
      g.add(planet);
      const members = d.nodes.filter((n) => n.area === a.id);
      const cloud = 8 + Math.sqrt(members.length) * 2.2;
      const pts = new Float32Array(members.length * 3);
      members.forEach((n, k) => {
        const u = Math.random(), v = Math.random(), th = u * Math.PI * 2, ph = Math.acos(2 * v - 1);
        const rr = cloud * (0.5 + 0.5 * Math.cbrt(Math.random()));
        pts.set([radius + rr * Math.sin(ph) * Math.cos(th), rr * Math.cos(ph) * 0.6, rr * Math.sin(ph) * Math.sin(th)], k * 3);
      });
      const geo = new THREE.BufferGeometry(); geo.setAttribute("position", new THREE.BufferAttribute(pts, 3));
      const mat = new THREE.PointsMaterial({ color, size: 2.6, sizeAttenuation: true, transparent: true, opacity: 0.95 });
      g.add(new THREE.Points(geo, mat));
      const lbl = labelSprite(a.name.toUpperCase(), a.color, radius, cloud + 10, 0);
      g.add(lbl);
      scene.add(g);
      return { a, g, planet, mat, lbl, speed: 0.0009 * (70 / radius) };
    });

    let raf = 0, running = true;
    const loop = () => {
      if (!running) return;
      groups.forEach((x) => { x.g.rotation.y += x.speed; });
      controls && controls.update();
      renderer.render(scene, camera);
      raf = requestAnimationFrame(loop);
    };
    loop();
    const highlight = () => groups.forEach((x) => {
      const on = !state.area || state.area === x.a.id;
      x.mat.opacity = on ? 0.95 : 0.08; x.planet.material.opacity = on ? 1 : 0.1; x.lbl.material.opacity = on ? 1 : 0.15;
    });
    orbitCtx = {
      highlight,
      reset: () => { home(); controls && controls.target.set(0, 0, 0); },
      stop: () => { running = false; cancelAnimationFrame(raf); renderer.dispose(); },
    };
    highlight();
    return null;
  }

  function labelSprite(text, color, x, y, z) {
    const c = document.createElement("canvas"), ctx = c.getContext("2d");
    c.width = 256; c.height = 64;
    ctx.font = "600 26px Inter, sans-serif"; ctx.fillStyle = color; ctx.textAlign = "center"; ctx.textBaseline = "middle";
    ctx.fillText(text, 128, 32);
    const sp = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(c), transparent: true, depthWrite: false }));
    sp.scale.set(80, 20, 1); sp.position.set(x, y, z);
    return sp;
  }

  // ---------- interactions ----------
  function renderView() {
    teardown();
    const ctx = views[state.view]();
    if (ctx) viewCtx = ctx;
    applyHighlight();
    renderDetail();
  }

  function setArea(a) {
    state.area = a || null;
    renderPanels();
    applyHighlight();
  }

  function select(id) {
    state.selected = id;
    applyHighlight();
    renderDetail();
  }

  function renderDetail() {
    const el = $("detail"), n = state.selected && idx?.byId.get(state.selected);
    if (!n) { el.style.display = "none"; return; }
    const nb = [...(idx.nbrs.get(n.id) || [])].map((id) => idx.byId.get(id)).sort((a, b) => b.degree - a.degree);
    const file = n.path.replace(/\.md$/, "");
    const url = `obsidian://open?vault=${encodeURIComponent(state.data.vault)}&file=${encodeURIComponent(file)}`;
    el.innerHTML = `<button class="close" id="closeDetail">✕</button>
      <h4 style="color:${idx.color(n.area)}">${esc(n.title)}</h4>
      <div class="path">${esc(n.path)}</div>
      <div class="path" style="margin-top:4px">${n.degree} links · edited ${fmtAgo(n.mtime)}${n.tags?.length ? " · #" + n.tags.slice(0, 4).map(esc).join(" #") : ""}</div>
      <ul>${nb.map((m) => `<li data-id="${esc(m.id)}"><span style="color:${idx.color(m.area)}">●</span> ${esc(m.title)}</li>`).join("") || "<li>No links</li>"}</ul>
      <div class="actions"><a class="btn" href="${url}">Open in Obsidian</a></div>`;
    el.style.display = "block";
    $("closeDetail").onclick = () => select(null);
    el.querySelectorAll("li[data-id]").forEach((li) => (li.onclick = () => select(li.dataset.id)));
  }

  function wire() {
    $("tabs").addEventListener("click", (e) => {
      const b = e.target.closest("button"); if (!b) return;
      document.querySelectorAll("#tabs button").forEach((x) => x.classList.toggle("active", x === b));
      state.view = b.dataset.view;
      try { localStorage.setItem("maps.view", state.view); } catch (_) { /* private mode */ }
      renderView();
    });
    $("chips").addEventListener("click", (e) => { const c = e.target.closest(".chip"); if (c) setArea(c.dataset.area); });
    $("search").addEventListener("input", (e) => { state.query = e.target.value.trim(); applyHighlight(); });
    $("search").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && state.query) { const hit = state.data.nodes.find((n) => n.area !== "hub" && isActive(n)); if (hit) select(hit.id); }
      if (e.key === "Escape") { e.target.value = ""; state.query = ""; select(null); }
    });
    $("tasks").addEventListener("click", (e) => { const t = e.target.closest(".task"); if (t && idx.byId.has(t.dataset.file)) select(t.dataset.file); });
    $("resetZoom").onclick = () => {
      if (orbitCtx) orbitCtx.reset();
      else if (viewCtx) viewCtx.svg.transition().duration(300).call(viewCtx.zoom.transform, d3.zoomIdentity);
    };
    let t; window.addEventListener("resize", () => { clearTimeout(t); t = setTimeout(() => state.data && renderView(), 200); });
  }

  try { const v = localStorage.getItem("maps.view"); if (v && v in views) state.view = v; } catch (_) { /* ignore */ }
  document.querySelectorAll("#tabs button").forEach((x) => x.classList.toggle("active", x.dataset.view === state.view));
  wire();
  tickClock();
  setInterval(tickClock, 15_000);
  refresh(true);
  setInterval(() => refresh(false), REFRESH_MS);
})();
