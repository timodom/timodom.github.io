namespace Edge.Atas.Hub;

using System;
using System.IO;

/// <summary>
/// Paste-to-save for the Discord webhook. ATAS saves indicator settings in plain text (workspace,
/// chart templates) by reading each property, so the property shows only this mask and the URL
/// itself lives in one local file. Anyone holding the URL can post to the members' channel.
/// </summary>
public static class WebhookStore
{
	public const string DefaultPath = @"C:\ATAS\secrets\discord-signals.txt";

	public enum SaveResult { Saved, Ignored, Rejected }

	private const string NotSet = "not set";
	private const string SavedPrefix = "saved (";

	/// <summary>Blank or a mask (ATAS restoring the saved setting) is ignored; anything that isn't a
	/// Discord webhook URL is rejected; a webhook URL is written to <paramref name="path"/>.</summary>
	public static SaveResult Save(string path, string value)
	{
		var v = (value ?? "").Trim();

		if (v.Length == 0 || v == NotSet || v.StartsWith(SavedPrefix, StringComparison.Ordinal))
			return SaveResult.Ignored;

		if (!v.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
			&& !v.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
			return SaveResult.Rejected;

		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllText(path, v);
		return SaveResult.Saved;
	}

	public static string Read(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
		}
		catch
		{
			return "";
		}
	}

	public static string Mask(string path)
	{
		var url = Read(path);
		return url.Length == 0 ? NotSet : $"{SavedPrefix}…{url[^Math.Min(4, url.Length)..]})";
	}
}
