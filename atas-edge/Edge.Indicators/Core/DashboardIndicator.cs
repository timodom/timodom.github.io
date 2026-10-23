namespace Edge.Atas.Core;

using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators;

using OFT.Rendering;
using OFT.Rendering.Context;
using OFT.Rendering.Control;

/// <summary>
/// Base for indicators that show a draggable on-chart dashboard. Subclasses fill the rows;
/// this class owns rendering, mouse dragging and persisting the panel position.
/// </summary>
public abstract class DashboardIndicator : Indicator
{
	private readonly DragPanel _dashboard = new();
	private readonly List<PanelRow> _rows = new(8);

	protected DashboardIndicator()
		: base(true)
	{
		EnableCustomDrawing = true;
		SubscribeToDrawingEvents(DrawingLayouts.Final);
	}

	[Display(Name = "Show dashboard", GroupName = "Dashboard", Order = 900)]
	public bool ShowDashboard { get; set; } = true;

	[Browsable(false)]
	public int DashboardX { get => _dashboard.OffsetX; set => _dashboard.OffsetX = value; }

	[Browsable(false)]
	public int DashboardY { get => _dashboard.OffsetY; set => _dashboard.OffsetY = value; }

	protected abstract string DashboardTitle { get; }

	/// <summary>Add the rows to show for the current state. Called on every redraw, so keep it cheap.</summary>
	protected abstract void BuildDashboard(List<PanelRow> rows);

	protected override void OnRender(RenderContext context, DrawingLayouts layout)
	{
		if (!ShowDashboard || ChartInfo is null || Container is null || CurrentBar < 2)
			return;

		_rows.Clear();
		BuildDashboard(_rows);

		if (_rows.Count > 0)
			_dashboard.Render(context, Container.Region, DashboardTitle, _rows);
	}

	public override bool ProcessMouseDown(RenderControlMouseEventArgs e) =>
		ShowDashboard && _dashboard.MouseDown(e.Location) || base.ProcessMouseDown(e);

	public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
	{
		if (Container is null || !_dashboard.MouseMove(e.Location, Container.Region))
			return base.ProcessMouseMove(e);

		RedrawChart();
		return true;
	}

	public override bool ProcessMouseUp(RenderControlMouseEventArgs e) =>
		_dashboard.MouseUp() || base.ProcessMouseUp(e);

	public override StdCursor GetCursor(RenderControlMouseEventArgs e) =>
		ShowDashboard && (_dashboard.IsDragging || _dashboard.Contains(e.Location)) ? StdCursor.Hand : base.GetCursor(e);
}
