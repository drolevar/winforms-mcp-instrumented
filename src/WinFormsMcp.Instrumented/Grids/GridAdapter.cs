using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WinFormsMcp.Instrumented.Grids;

/// <summary>
/// Teaches the bridge to read, select and edit one family of grid controls (a third-party grid
/// suite, for example). Every member runs on the registered UI thread. Read members
/// (<see cref="Accepts"/>, <see cref="GetFeatures"/>, <see cref="GetViewInfo"/>,
/// <see cref="DescribeColumn"/>, <see cref="GetCellText"/>, <see cref="IsRowSelected"/> and
/// <see cref="TryGetFocusedCell"/>) must not create window handles, show UI, pump messages or
/// change focus or selection; <see cref="Select"/>, <see cref="Scroll"/> and <see cref="SetCell"/>
/// may. The bridge owns the loops and the bounds: it asks about one column or one cell at a time,
/// only for indexes it has clamped, and limits all text. Rows are positions <c>0..RowCount-1</c> in
/// the current view (data rows only) and columns are indexes in the adapter's column collection
/// order; neither is a durable key. An adapter must not keep grids in fields or statics.
/// <see cref="GetFeatures"/> runs for every grid in every tree walk, so it must be cheap.
/// Adapters are consulted in registration order, before the built-in DataGridView handling.
/// Register with <see cref="McpBridgeBuilder.AddGridAdapter"/>.
/// </summary>
public abstract class GridAdapter
{
    /// <summary>The name of the built-in DataGridView handling; application adapters cannot use it.</summary>
    internal const string DataGridViewName = "datagridview";

    private static readonly Regex NamePattern = new(@"^[a-z0-9._-]{1,48}\z", RegexOptions.CultureInvariant);

    /// <param name="name">Adapter name advertised in hello, matching [a-z0-9._-]{1,48}.</param>
    protected GridAdapter(string name)
    {
        if (name == null || !NamePattern.IsMatch(name))
            throw new ArgumentException("An adapter name must match [a-z0-9._-]{1,48}.", nameof(name));
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// True when this adapter handles <paramref name="control"/>. The bridge calls it once per
    /// control instance and caches the answer; throwing means "not accepted".
    /// </summary>
    public abstract bool Accepts(Control control);

    /// <summary>
    /// Called once, when the bridge first accepts <paramref name="grid"/>. Subscribe to the grid's
    /// events that change the view (data source, row count, sort, filter, column collection) and
    /// call <see cref="GridViewSignal.Changed"/>. Keep neither the grid nor the signal in a static.
    /// </summary>
    public virtual void Attach(Control grid, GridViewSignal signal)
    {
    }

    /// <summary>The operations beyond reading that this grid supports now. Reading is always implied.</summary>
    public abstract GridFeatures GetFeatures(Control grid);

    /// <summary>
    /// The size and fingerprint of the current view. Throw <see cref="GridOperationException"/> with
    /// <see cref="GridOperationError.Unsupported"/> for grouped, virtual or otherwise unsupported views.
    /// </summary>
    public abstract GridViewInfo GetViewInfo(Control grid);

    /// <summary>Fills the description of one column; <paramref name="column"/> is within the column count.</summary>
    public abstract void DescribeColumn(Control grid, int column, GridColumnDescription d);

    /// <summary>
    /// The text a user sees in the cell (display text, not the underlying value), or null when the
    /// cell has none. The bridge bounds it and never asks for a redacted column.
    /// </summary>
    public abstract string? GetCellText(Control grid, int row, int column);

    /// <summary>Whether the row is selected; null when the adapter cannot tell.</summary>
    public virtual bool? IsRowSelected(Control grid, int row) => null;

    /// <summary>Reports the focused cell; false when no cell is focused.</summary>
    public virtual bool TryGetFocusedCell(Control grid, out int row, out int column)
    {
        row = -1;
        column = -1;
        return false;
    }

    /// <summary>
    /// Focuses the cell (or the row's first visible cell when <paramref name="column"/> is null), makes it the
    /// only selection and brings it into view. The default refuses.
    /// </summary>
    public virtual void Select(Control grid, int row, int? column)
        => throw new GridOperationException(GridOperationError.Unsupported,
            "The adapter does not support selecting.");

    /// <summary>Brings a row and/or a column into view without changing the selection. The default refuses.</summary>
    public virtual void Scroll(Control grid, int? row, int? column)
        => throw new GridOperationException(GridOperationError.Unsupported,
            "The adapter does not support scrolling.");

    /// <summary>
    /// Edits a cell through its editor, as a user does, including the application's validation.
    /// Return <see cref="GridEditResult.Committed"/> only after the row commit succeeded and the value is
    /// confirmed as posted. The default refuses.
    /// </summary>
    public virtual GridEditResult SetCell(Control grid, GridEditRequest request)
        => throw new GridOperationException(GridOperationError.Unsupported,
            "The adapter does not support editing.");
}
