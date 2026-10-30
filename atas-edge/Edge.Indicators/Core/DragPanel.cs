namespace Edge.Atas.Core;

using System;
using System.Collections.Generic;
using System.Drawing;

using OFT.Rendering.Context;
using OFT.Rendering.Tools;

public readonly record struct PanelRow(string Label, string Value, Color ValueColor);

/// <summary>
/// On-chart info box the user can drag with the mouse. The owning indicator forwards its
/// ProcessMouseDown/Move/Up calls here and persists OffsetX/OffsetY as hidden properties,
/// so the position survives chart template save/load.
/// </summary>
public sealed class DragPanel
{
	private const int Padding = 8;
	private const int ColumnGap = 14;

	private readonly RenderFont _font = new("Arial", 10);
	private readonly RenderFont _titleFont = new("Arial", 10, FontStyle.Bold);
	private readonly RenderPen _border = new(Color.FromArgb(120, 128, 128, 128), 1);

	private Rectangle _bounds;
	private bool _dragging;
	private Point _grab;

	/// <summary>Offset from the chart area's top-left corner.</summary>
	public int OffsetX { get; set; } = 20;

	public int OffsetY { get; set; } = 20;

	public Color Background { get; set; } = Color.FromArgb(210, 18, 22, 30);

	public Color TextColor { get; set; } = Color.FromArgb(225, 225, 225);

	public bool IsDragging => _dragging;

	public bool Contains(Point p) => _bounds.Contains(p);

	public bool MouseDown(Point p)
	{
		if (!_bounds.Contains(p))
			return false;

		_dragging = true;
		_grab = new Point(p.X - _bounds.X, p.Y - _bounds.Y);
		return true;
	}

	public bool MouseMove(Point p, Rectangle region)
	{
		if (!_dragging)
			return false;

		// Clamp so the panel can't be dragged off-screen and lost.
		OffsetX = Math.Clamp(p.X - _grab.X - region.X, 0, Math.Max(0, region.Width - _bounds.Width));
		OffsetY = Math.Clamp(p.Y - _grab.Y - region.Y, 0, Math.Max(0, region.Height - _bounds.Height));
		return true;
	}

	public bool MouseUp()
	{
		var was = _dragging;
		_dragging = false;
		return was;
	}

	public void Render(RenderContext context, Rectangle region, string title, IReadOnlyList<PanelRow> rows)
	{
		var lineHeight = context.MeasureString("Ag", _font).Height + 2;
		var labelWidth = context.MeasureString(title, _titleFont).Width;
		var valueWidth = 0;

		foreach (var row in rows)
		{
			labelWidth = Math.Max(labelWidth, context.MeasureString(row.Label, _font).Width);
			valueWidth = Math.Max(valueWidth, context.MeasureString(row.Value, _font).Width);
		}

		var width = labelWidth + ColumnGap + valueWidth + Padding * 2;
		var height = (rows.Count + 1) * lineHeight + Padding * 2;
		var x = region.X + Math.Clamp(OffsetX, 0, Math.Max(0, region.Width - width));
		var y = region.Y + Math.Clamp(OffsetY, 0, Math.Max(0, region.Height - height));

		_bounds = new Rectangle(x, y, width, height);
		context.FillRectangle(Background, _bounds);
		context.DrawRectangle(_border, _bounds);

		var cy = y + Padding;
		context.DrawString(title, _titleFont, TextColor, x + Padding, cy);
		cy += lineHeight;

		foreach (var row in rows)
		{
			context.DrawString(row.Label, _font, TextColor, x + Padding, cy);
			context.DrawString(row.Value, _font, row.ValueColor, x + Padding + labelWidth + ColumnGap, cy);
			cy += lineHeight;
		}
	}
}

/// <summary>Dashboard text colors shared by every panel.</summary>
public static class Palette
{
	public static readonly Color Up = Color.FromArgb(255, 120, 230, 150);
	public static readonly Color Down = Color.FromArgb(255, 255, 120, 120);
	public static readonly Color Muted = Color.FromArgb(255, 150, 150, 150);

	public static Color Of(Regime regime) => regime switch
	{
		Regime.Bull => Up,
		Regime.Bear => Down,
		_ => Muted
	};

	public static Color Of(decimal value) => value > 0 ? Up : value < 0 ? Down : Muted;
}
