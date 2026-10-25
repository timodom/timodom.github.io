using System;
using System.Collections.Generic;
using Edge.Atas.Core;
using Edge.Atas.Hub;

int fails = 0;
void Check(string name, bool ok, string detail) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}"); if (!ok) fails++; }
void Eq(string name, string actual, string expected) => Check(name, actual == expected, actual == expected ? "" : $"\n   got:  {actual}\n   want: {expected}");

// ---------- SignalFormatter ----------
Eq("range set", SignalFormatter.RangeSet("ES", 5812.25m, 5821.75m, 38, new TimeSpan(11, 30, 0)),
	"ES opening range 5812.25–5821.75 (38t). Breakout armed until 11:30.");
Eq("skipped below", SignalFormatter.RangeSkipped("ES", 6, 8, 80), "ES skipped today: range 6t, below the 8t minimum");
Eq("skipped above", SignalFormatter.RangeSkipped("ES", 81, 8, 80), "ES skipped today: range 81t, above the 80t maximum");
Eq("entry long", SignalFormatter.Entry("ES", 1, 5822m, 5812m, 5837m, 1.5m, 40),
	"ES LONG 5822.00 · stop 5812.00 · target 5837.00 (1.5R) · risk 40t");
Eq("entry short", SignalFormatter.Entry("NQ", -1, 21000m, 21010m, 20985m, 1.5m, 40),
	"NQ SHORT 21000.00 · stop 21010.00 · target 20985.00 (1.5R) · risk 40t");
Eq("stop moved", SignalFormatter.StopMoved("ES", 5822m), "ES stop → 5822.00");
Eq("exit target", SignalFormatter.Exit("ES", 1, "Target", 5837m, 60, 1.5m, false), "ES LONG closed at target 5837.00, +60t (+1.5R)");
Eq("exit stop after restart", SignalFormatter.Exit("NQ", -1, "Stop", 21010m, -40, -1m, true),
	"NQ SHORT closed at stop 21010.00, -40t (-1.0R) (entry was before an ATAS restart)");
Eq("exit protect", SignalFormatter.Exit("ES", 1, "Protect", 5834m, 48, 1.2m, false), "ES LONG closed at the protect stop 5834.00, +48t (+1.2R)");
Eq("exit hard close", SignalFormatter.Exit("ES", 1, "Hard close", 5830m, 32, 0.8m, false), "ES LONG closed at the 15:55 hard close 5830.00, +32t (+0.8R)");
Eq("exit session end", SignalFormatter.Exit("ES", 1, "Session ended", 5825m, 12, 0.3m, false), "ES LONG closed at session end 5825.00, +12t (+0.3R)");
Eq("footer", SignalFormatter.WithFooter("x"), "x\n*Model signal, simulated, not a live trade. Hypothetical results; not financial advice.*");
Eq("payload with name", SignalFormatter.Payload("Name", "a \"b\"\nc"), "{\"username\":\"Name\",\"content\":\"a \\\"b\\\"\\nc\"}");
Eq("payload without name", SignalFormatter.Payload("", "x"), "{\"content\":\"x\"}");

// ---------- InstrumentProfile ----------
Check("profile ESZ6 -> ES", InstrumentProfile.Find("ESZ6")?.Name == "ES", "");
Check("profile MESZ6 -> MES", InstrumentProfile.Find("MESZ6")?.Name == "MES", "");
Check("profile #NQ -> NQ", InstrumentProfile.Find("#NQ")?.Name == "NQ", "");
Check("profile MNQZ6 -> MNQ", InstrumentProfile.Find("MNQZ6")?.Name == "MNQ", "");
Check("profile CL -> none", InstrumentProfile.Find("CL") is null, "");

// ---------- HubCore ----------
// ES, 15m bars, 2026-10-23 ET. Range bar 09:30: 5812.25-5821.75 (38t). 09:45 closes 5822 above the
// high -> long 5822, stop 5812 (low - 1t), 40t risk, target 5822 + 1.5 x 10 = 5837.
var day = new DateTime(2026, 10, 23);
var barIdx = 0;
var esProfile = InstrumentProfile.Find("ES");
HubCore NewCore() => new HubCore(esProfile, 0.25m, TimeSpan.FromMinutes(15));
List<HubMessage> Feed(HubCore core, bool live, params (int h, int m, decimal o, decimal hi, decimal lo, decimal c)[] bars)
{
	var all = new List<HubMessage>();
	foreach (var b in bars)
		all.AddRange(core.Process(new RangeBar(barIdx++, day.AddHours(b.h).AddMinutes(b.m), b.o, b.hi, b.lo, b.c, 1000, 0), live));
	return all;
}
string Texts(List<HubMessage> ms) => string.Join(" | ", ms.ConvertAll(m => $"{m.Kind}: {m.Text}"));
var rangeBar = (9, 30, 5815m, 5821.75m, 5812.25m, 5818m);
var longBreak = (9, 45, 5818m, 5822.5m, 5817m, 5822m);

// 1. Range, long breakout, target.
var t1 = Feed(NewCore(), true, rangeBar, longBreak, (10, 0, 5822m, 5838m, 5821m, 5836m));
Check("target day: 3 posts", t1.Count == 3, Texts(t1));
Eq("target day: range", t1.Count > 0 ? t1[0].Text : "", "ES opening range 5812.25–5821.75 (38t). Breakout armed until 11:30.");
Eq("target day: entry", t1.Count > 1 ? t1[1].Text : "", "ES LONG 5822.00 · stop 5812.00 · target 5837.00 (1.5R) · risk 40t");
Eq("target day: exit", t1.Count > 2 ? t1[2].Text : "", "ES LONG closed at target 5837.00, +60t (+1.5R)");

// 2. Protect: best 5835.5 passes 80% of the way (5834) -> stop 5835.5 - 6t = 5834; next bar takes it.
var t2 = Feed(NewCore(), true, rangeBar, longBreak, (10, 0, 5822m, 5835.5m, 5821m, 5835m), (10, 15, 5835m, 5835.25m, 5833.75m, 5834m));
Check("protect day: 4 posts", t2.Count == 4, Texts(t2));
Eq("protect day: stop moved", t2.Count > 2 ? t2[2].Text : "", "ES stop → 5834.00");
Eq("protect day: exit", t2.Count > 3 ? t2[3].Text : "", "ES LONG closed at stop 5834.00, +48t (+1.2R)");

// 3. Short breakout: 09:45 closes 5812 below the low -> short 5812, stop 5822 (high + 1t), target 5797.
var t3 = Feed(NewCore(), true, rangeBar, (9, 45, 5818m, 5819m, 5811m, 5812m));
Eq("short entry", t3.Count > 1 ? t3[1].Text : "", "ES SHORT 5812.00 · stop 5822.00 · target 5797.00 (1.5R) · risk 40t");

// 4. Range too small (6t) and too large (81t); the engine must agree on the next bar.
var small = Feed(NewCore(), true, (9, 30, 5813m, 5813.75m, 5812.25m, 5813m));
Eq("small range skipped", small.Count == 1 ? small[0].Text : Texts(small), "ES skipped today: range 6t, below the 8t minimum");
var wideCore = NewCore();
var wide = Feed(wideCore, true, (9, 30, 5815m, 5832.5m, 5812.25m, 5820m), (9, 45, 5820m, 5821m, 5819m, 5820m));
Eq("wide range skipped", wide.Count == 1 ? wide[0].Text : Texts(wide), "ES skipped today: range 81t, above the 80t maximum");
Check("engine agrees: Skipped", wideCore.Engine.State == RangeDayState.Skipped, wideCore.Engine.State.ToString());

// 5. No replays: history bars post nothing; an exit seen live after a restart says so.
var gateCore = NewCore();
var hist = Feed(gateCore, false, rangeBar, longBreak);
Check("history posts nothing", hist.Count == 0, Texts(hist));
var liveExit = Feed(gateCore, true, (10, 0, 5822m, 5838m, 5821m, 5836m));
Eq("live exit after restart", liveExit.Count == 1 ? liveExit[0].Text : Texts(liveExit),
	"ES LONG closed at target 5837.00, +60t (+1.5R) (entry was before an ATAS restart)");

// 6. One range post per day; engine tick size from the constructor.
Check("one range post per day", Feed(NewCore(), true, rangeBar, rangeBar).Count == 1, "");
Check("engine tick size", NewCore().Engine.TickSize == 0.25m, "");

// ---------- WebhookStore ----------
var hookPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hub-test-{Guid.NewGuid():N}", "hook.txt");
const string fakeHook = "https://discord.com/api/webhooks/123/abcdEFGHwxyz";
Check("unset reads empty", WebhookStore.Read(hookPath) == "", "");
Check("unset masks as 'not set'", WebhookStore.Mask(hookPath) == "not set", WebhookStore.Mask(hookPath));
Check("blank ignored", WebhookStore.Save(hookPath, "  ") == WebhookStore.SaveResult.Ignored, "");
Check("not a webhook rejected", WebhookStore.Save(hookPath, "https://example.com/x") == WebhookStore.SaveResult.Rejected, "");
Check("paste saved", WebhookStore.Save(hookPath, "  " + fakeHook + " ") == WebhookStore.SaveResult.Saved, "");
Check("read back trimmed", WebhookStore.Read(hookPath) == fakeHook, WebhookStore.Read(hookPath));
Check("mask shows last 4 only", WebhookStore.Mask(hookPath) == "saved (…wxyz)", WebhookStore.Mask(hookPath));
Check("restoring the mask is ignored", WebhookStore.Save(hookPath, "saved (…wxyz)") == WebhookStore.SaveResult.Ignored && WebhookStore.Read(hookPath) == fakeHook, "");
Check("discordapp.com accepted", WebhookStore.Save(hookPath, "https://discordapp.com/api/webhooks/9/zz12") == WebhookStore.SaveResult.Saved, "");
System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(hookPath), true);

// ---- end of checks ----
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
