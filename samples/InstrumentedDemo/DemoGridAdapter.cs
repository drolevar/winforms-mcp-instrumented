using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using WinFormsMcp.Instrumented;
using WinFormsMcp.Instrumented.Grids;

namespace InstrumentedDemo;

/// <summary>
/// The consumer example of a grid adapter: it teaches the bridge to read, select and edit <see cref="DemoGrid"/> using
/// only the public seam. It keeps no grid in a field or a static; everything it needs it gets from the call.
/// docs/10-grid-adapters.md describes the contract.
/// </summary>
internal sealed class DemoGridAdapter : GridAdapter
{
    public DemoGridAdapter()
        : base("demo-grid")
    {
    }

    /// <summary>The demo's whole bridge configuration for grids: the adapter and the action that opens a control's context menu.</summary>
    internal static void RegisterWith(McpBridgeBuilder options)
    {
        options.AddGridAdapter(new DemoGridAdapter());

        // A context menu is read-only to the bridge until it is shown. Showing it through ContextMenuStrip.Show sets
        // SourceControl and raises Opening, so the application's own enable/disable logic runs. Where the menu
        // appears is application knowledge: for the demo grid, the focused cell. The strip is closed first so that
        // asking twice runs Opening twice.
        options.AddAction("show_context_menu", control => control.ContextMenuStrip != null, control =>
        {
            ContextMenuStrip menu = control.ContextMenuStrip!;
            menu.Close();
            menu.Show(control, control is DemoGrid grid ? grid.ContextMenuAnchor : new Point(control.Width / 2, control.Height / 2));
        });
    }

    // Runs for every control in every tree walk: keep it a type test.
    public override bool Accepts(Control control) => control is DemoGrid;

    public override void Attach(Control grid, GridViewSignal signal)
    {
        // The handler captures only the signal, which refers only to the bridge's per-grid state. It does not capture
        // the grid, so the chain grid -> handler -> signal -> state keeps nothing alive that the grid does not.
        ((DemoGrid)grid).ViewChanged += (sender, e) => signal.Changed();
    }

    public override GridFeatures GetFeatures(Control grid) => GridFeatures.Select | GridFeatures.Scroll | GridFeatures.Edit;

    public override GridViewInfo GetViewInfo(Control control)
    {
        var grid = (DemoGrid)control;
        // Row count, column count and the sort state: what moves rows. Values are covered by ViewChanged, which the
        // grid raises on every committed edit.
        long fingerprint = grid.RowCount;
        fingerprint = fingerprint * 31 + grid.ColumnCount;
        fingerprint = fingerprint * 31 + grid.SortColumn;
        fingerprint = fingerprint * 31 + (grid.SortDescending ? 1 : 0);
        return new GridViewInfo(grid.RowCount, grid.ColumnCount, fingerprint);
    }

    public override void DescribeColumn(Control control, int column, GridColumnDescription d)
    {
        DemoColumn source = ((DemoGrid)control).ColumnAt(column);
        d.Name = source.Name;
        d.Field = source.Field;
        d.Header = source.Header;
        d.Visible = true;
        d.Editable = source.Editable;
        d.ValueKind = source.Numeric ? GridValueKind.Number : GridValueKind.Text;
        foreach (string band in source.Bands) d.AddBand(band);
    }

    public override string? GetCellText(Control grid, int row, int column) => ((DemoGrid)grid).CellText(row, column);

    public override bool? IsRowSelected(Control grid, int row) => ((DemoGrid)grid).SelectedRow == row;

    public override bool TryGetFocusedCell(Control grid, out int row, out int column)
    {
        var demo = (DemoGrid)grid;
        row = demo.FocusedRow;
        column = demo.FocusedColumn;
        return row >= 0 && column >= 0;
    }

    public override void Select(Control grid, int row, int? column) => ((DemoGrid)grid).SelectCell(row, column ?? DemoGrid.ItemColumn);

    public override void Scroll(Control grid, int? row, int? column)
    {
        // Every column fits on screen, so only a row can be out of view.
        if (row.HasValue) ((DemoGrid)grid).ScrollIntoView(row.Value);
    }

    /// <summary>
    /// The edit sequence of docs/10-grid-adapters.md: focus the cell, check that the view did not change, show the editor,
    /// set its value, post it and close the editor, then confirm the commit. Validation errors are captured instead of
    /// shown, so they come back as a refusal; a modal dialog the grid opens while posting (the confirmation) is
    /// deliberately left open, so the receipt stays started until someone answers it.
    /// </summary>
    public override GridEditResult SetCell(Control control, GridEditRequest request)
    {
        var grid = (DemoGrid)control;

        grid.FocusCell(request.Row, request.Column);
        if (request.ViewChanged)
            throw new GridOperationException(GridOperationError.StaleView, "The view changed before the editor opened.");

        // No-modal mode for the invalid-value event, for the duration of this call only. A real grid often reports its
        // validation message only through such an event, which is why the outcome below is not the only source of it.
        string? errorText = null;
        EventHandler<DemoInvalidValueEventArgs> capture = (sender, e) =>
        {
            errorText = e.ErrorText;
            e.ShowError = false;
        };
        grid.InvalidValue += capture;
        DemoEditOutcome outcome;
        try
        {
            // Inside the try: if setting the text throws, the finally still closes the editor.
            if (!grid.BeginEdit(request.Row, request.Column))
                return GridEditResult.Rejected(GridEditRejection.EditorNotShown, null, grid.CellText(request.Row, request.Column));
            grid.EditorText = request.Text;
            outcome = grid.PostEdit();
        }
        finally
        {
            // After a modal dialog the grid can be gone.
            if (!grid.IsDisposed) grid.CancelEdit();
            grid.InvalidValue -= capture;
        }
        if (grid.IsDisposed) return GridEditResult.Rejected(GridEditRejection.Other, null);

        int rowAfter = grid.RowOf(outcome.Record);
        string? shown = rowAfter < 0 ? null : grid.CellText(rowAfter, request.Column);
        switch (outcome.Status)
        {
            case DemoEditStatus.Committed:
                // Committed only when the cell now shows the value that was typed.
                if (shown == null || !ShowsTypedValue(request.Text, shown))
                    return GridEditResult.Rejected(GridEditRejection.RowNotCommitted, null, shown);
                return GridEditResult.Committed(shown, rowAfter);
            // The captured event text comes first, as in an adapter for a grid that gives the message only through its
            // event; this demo grid also returns it in the outcome, which is the fallback.
            case DemoEditStatus.NotANumber:
                return GridEditResult.Rejected(GridEditRejection.InvalidInput, errorText ?? outcome.ErrorText, shown);
            default:
                return GridEditResult.Rejected(GridEditRejection.ValidationFailed, errorText ?? outcome.ErrorText, shown);
        }
    }

    // The editable column holds whole numbers: the typed text is the value shown, written the way the grid writes it.
    private static bool ShowsTypedValue(string typed, string shown)
        => int.TryParse(typed.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out int value)
           && string.Equals(shown, value.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
}
