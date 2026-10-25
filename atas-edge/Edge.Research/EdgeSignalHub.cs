namespace Edge.Atas;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ATAS.Indicators;

using Edge.Atas.Core;
using Edge.Atas.Hub;

using ZeroDay.Shared;

/// <summary>
/// Posts the Opening Range model's events (range set or skipped, entry, stop moved, exit) to a
/// Discord channel. Runs its own copy of the bot's engine on closed bars with the market edition's
/// locked setup; never reads the bot, the account or orders. Draws nothing, places no orders.
/// Only bars that close while the chart is live are posted, so a reload never replays history.
/// </summary>
[DisplayName("Edge Signal Hub")]
[Category("Edge")]
public class EdgeSignalHub : Indicator
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
	private static readonly TimeZoneInfo Eastern = FindEastern();

	private HubCore _core;
	private InstrumentProfile _profile;
	private string _family;
	private string _unsupported;
	private bool _claimed;
	private bool _realtime;
	private int _lastCalcBar = -1;
	private int _lastBar = -1;
	private bool _chartTimeIsUtc = true;
	private DiscordPacer _pacer;
	private Timer _pump;

	[Display(Name = "Post to Discord", GroupName = "1. Discord", Order = 10)]
	public bool PostToDiscord { get; set; }

	// Paste-to-save: ATAS saves settings by reading this getter, which only ever returns the mask.
	[Display(Name = "Discord webhook (paste the URL, then OK)", GroupName = "1. Discord", Order = 20)]
	public string Webhook
	{
		get => WebhookStore.Mask(WebhookStore.DefaultPath);
		set
		{
			try
			{
				var result = WebhookStore.Save(WebhookStore.DefaultPath, value);

				if (result == WebhookStore.SaveResult.Rejected)
					AddAlert("EdgeSignalHub", "That is not a Discord webhook URL. Nothing was saved.");
				else if (result == WebhookStore.SaveResult.Saved)
					AddAlert("EdgeSignalHub", $"Webhook {WebhookStore.Mask(WebhookStore.DefaultPath)}. Tick 'Send test post' to check it.");
			}
			catch (Exception ex)
			{
				try { AddAlert("EdgeSignalHub", $"Could not save the webhook: {ex.Message}"); } catch { }
			}
		}
	}

	[Display(Name = "Post as (display name)", GroupName = "1. Discord", Order = 30)]
	public string PostAs { get; set; } = "ZeroDayTrades Opening Range";

	[Display(Name = "Send test post (tick)", GroupName = "1. Discord", Order = 40)]
	public bool SendTestPost
	{
		get => false;
		set
		{
			if (value)
				Enqueue($"Test post from Edge Signal Hub ({_profile?.Name ?? "no instrument yet"}). Webhook works.");
		}
	}

	[Display(Name = "Chart bar times are UTC", GroupName = "2. Chart", Order = 10)]
	public bool ChartTimeIsUtc
	{
		get => _chartTimeIsUtc;
		set
		{
			_chartTimeIsUtc = value;
			RecalculateValues();
		}
	}

	public EdgeSignalHub()
	{
		DenyToChangePanel = true;
		((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
	}

	protected override void OnInitialize()
	{
		// ~5 posts per 2 s per webhook is Discord's limit; 1.1 s spacing stays under it.
		_pacer = new DiscordPacer(TimeSpan.FromMilliseconds(1100), SendAsync, d => Task.Delay(d), OnPostFault);
		_pump = new Timer(_ => { try { _ = _pacer.DrainAsync(); } catch { } }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
	}

	protected override void OnDispose()
	{
		_pump?.Dispose();
		_pump = null;
		ReleaseClaim();
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar == 0)
		{
			_realtime = false;
			_lastBar = -1;
			CheckChart();
		}

		if (_unsupported != null || _core is null)
			return;

		// History is calculated once per bar; a repeat call for the same bar means ticks are streaming.
		if (bar == _lastCalcBar && bar == CurrentBar - 1)
			_realtime = true;

		_lastCalcBar = bar;

		// Closed-bar gate: the first time a bar index is seen, the one before it is final.
		if (bar == _lastBar || bar == 0)
			return;

		_lastBar = bar;

		var k = GetCandle(bar - 1);
		var closed = new RangeBar(bar - 1, ToEt(k.Time), k.Open, k.High, k.Low, k.Close, k.Volume, k.Delta);

		foreach (var m in _core.Process(closed, _realtime))
		{
			Log($"{m.Kind}: {m.Text}");

			if (PostToDiscord && _claimed)
				Enqueue(SignalFormatter.WithFooter(m.Text));
		}
	}

	// Runs at bar 0 of every calculation: supported instrument, 15-minute bars, one hub per family.
	private void CheckChart()
	{
		ReleaseClaim();
		_core = null;
		_unsupported = null;
		_profile = InstrumentProfile.Find(InstrumentInfo?.Instrument);

		var minutes = 0d;

		for (var i = 1; i < Math.Min(CurrentBar, 300); i++)
		{
			var gap = (GetCandle(i).Time - GetCandle(i - 1).Time).TotalMinutes;

			if (gap > 0 && (minutes == 0 || gap < minutes))
				minutes = gap;
		}

		if (_profile is null)
			_unsupported = $"Not running: {InstrumentInfo?.Instrument} is not ES, MES, NQ or MNQ.";
		else if (minutes != 15)
			_unsupported = $"Not running: this is a {minutes:0}-minute chart. Use a 15-minute chart.";

		if (_unsupported != null)
		{
			AddAlert("EdgeSignalHub", _unsupported);
			return;
		}

		_family = HubClaim.FamilyOf(_profile);
		_claimed = HubClaim.TryClaim(_family, this);

		if (!_claimed)
			AddAlert("EdgeSignalHub", $"Another Edge Signal Hub already posts {_family} signals. This one stays silent.");

		_core = new HubCore(_profile, InstrumentInfo.TickSize, TimeSpan.FromMinutes(15));
		Log($"Started on {InstrumentInfo?.Instrument} 15m, profile {_profile.Name}, posting {(PostToDiscord ? "ON" : "off")}.");
	}

	private void ReleaseClaim()
	{
		if (_claimed)
			HubClaim.Release(_family, this);

		_claimed = false;
	}

	private void Enqueue(string text) => _pacer?.Enqueue(SignalFormatter.Payload(PostAs, text));

	/// <summary>One attempt at one post. The webhook is read from its file at send time, so it never
	/// sits in the chart settings or the workspace, and pasting a new one takes effect immediately.</summary>
	private async Task<PostOutcome> SendAsync(string payload)
	{
		var url = WebhookStore.Read(WebhookStore.DefaultPath);

		if (url.Length == 0)
			return PostOutcome.Failed("no webhook set - paste it into the 'Discord webhook' setting");

		try
		{
			using var content = new StringContent(payload, Encoding.UTF8, "application/json");
			using var resp = await Http.PostAsync(url, content).ConfigureAwait(false);

			if (resp.IsSuccessStatusCode)
				return PostOutcome.Success;

			if ((int)resp.StatusCode == 429)
			{
				var after = resp.Headers.RetryAfter?.Delta
					?? (resp.Headers.RetryAfter?.Date is DateTimeOffset d ? d - DateTimeOffset.UtcNow : null);
				return PostOutcome.RateLimited(after is TimeSpan t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(1));
			}

			return PostOutcome.Failed($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"
				+ ((int)resp.StatusCode == 404 ? " - the webhook no longer exists (deleted or rotated)"
					: (int)resp.StatusCode == 401 ? " - the webhook token is wrong" : ""));
		}
		catch (Exception ex)
		{
			return PostOutcome.Failed($"request failed: {ex.Message}");
		}
	}

	// The pacer reports each distinct fault once. Never routed back through Discord.
	private void OnPostFault(string why)
	{
		Log($"!! DISCORD DELIVERY FAILED - {why}");

		try
		{
			AddAlert("EdgeSignalHub", $"Discord delivery failed: {why}");
		}
		catch
		{
		}
	}

	// One log file per instrument family, written only by the hub that holds the claim (one writer per file).
	private void Log(string message)
	{
		if (!_claimed)
			return;

		try
		{
			Directory.CreateDirectory(@"C:\ATAS\signals");
			File.AppendAllText($@"C:\ATAS\signals\edge-signal-hub-{_family}.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private DateTime ToEt(DateTime time) =>
		_chartTimeIsUtc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), Eastern) : time;

	private static TimeZoneInfo FindEastern()
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
		catch (TimeZoneNotFoundException)
		{
			return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		}
	}
}
