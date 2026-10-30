namespace Edge.Atas;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;

using ATAS.Indicators;

/// <summary>
/// Writes every loaded bar (time as the chart gives it, OHLC, volume, delta) to
/// Documents\ATAS\EdgeResearch\&lt;instrument&gt;-&lt;bars&gt;.csv once the history load finishes, so
/// strategy ideas can be tested offline on exactly the data the chart has. Draws nothing.
/// </summary>
[DisplayName("Edge Bar Export (research)")]
[Category("Edge")]
public class EdgeBarExport : Indicator
{
	private bool _written;

	[Display(Name = "Export now (tick to re-export)")]
	public bool ExportNow
	{
		get => false;
		set
		{
			if (value)
			{
				_written = false;
				RecalculateValues();
			}
		}
	}

	public EdgeBarExport()
	{
		DenyToChangePanel = true;
		((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (_written || bar != CurrentBar - 1 || bar < 1)
			return;

		_written = true;

		try
		{
			var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ATAS", "EdgeResearch");
			Directory.CreateDirectory(dir);
			var name = (InstrumentInfo?.Instrument ?? "chart").Replace('/', '_').Replace(Path.DirectorySeparatorChar, '_');
			var path = Path.Combine(dir, $"{name}-{CurrentBar}.csv");
			var inv = CultureInfo.InvariantCulture;
			var sb = new StringBuilder("time,open,high,low,close,volume,delta\n");

			for (var i = 0; i < CurrentBar; i++)
			{
				var k = GetCandle(i);
				sb.Append(k.Time.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',')
					.Append(k.Open.ToString(inv)).Append(',')
					.Append(k.High.ToString(inv)).Append(',')
					.Append(k.Low.ToString(inv)).Append(',')
					.Append(k.Close.ToString(inv)).Append(',')
					.Append(k.Volume.ToString(inv)).Append(',')
					.Append(k.Delta.ToString(inv)).Append('\n');
			}

			File.WriteAllText(path, sb.ToString());
			AddAlert("EdgeBarExport", $"Exported {CurrentBar} bars to {path}");
		}
		catch (Exception ex)
		{
			AddAlert("EdgeBarExport", $"Export failed: {ex.Message}");
		}
	}
}
