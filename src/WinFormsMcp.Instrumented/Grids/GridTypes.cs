using System;
using System.Collections.Generic;

namespace WinFormsMcp.Instrumented.Grids;

/// <summary>The operations beyond reading that a grid supports. Reading is implied.</summary>
[Flags]
public enum GridFeatures
{
    None = 0,

    /// <summary>Rows and cells can be selected.</summary>
    Select = 1,

    /// <summary>Rows and columns can be brought into view.</summary>
    Scroll = 2,

    /// <summary>Cells can be edited through the editor.</summary>
    Edit = 4
}

/// <summary>The kind of value a column holds, as an agent should format input for it.</summary>
public enum GridValueKind
{
    Text,
    Number,
    Boolean,
    Date,
    Time,
    Duration,
    Choice,
    Other
}

/// <summary>Why an adapter refuses an edit; the application knows the reason, the bridge only relays it.</summary>
public enum GridEditRejection
{
    /// <summary>The cell cannot be edited.</summary>
    NotEditable,

    /// <summary>The editor could not be shown (for example in a background window).</summary>
    EditorNotShown,

    /// <summary>The editor does not accept the text.</summary>
    InvalidInput,

    /// <summary>The text matches more than one choice.</summary>
    AmbiguousInput,

    /// <summary>The application's validation refused the value or the row.</summary>
    ValidationFailed,

    /// <summary>The value was posted but the row did not commit.</summary>
    RowNotCommitted,

    Other
}

/// <summary>Why an adapter refuses a grid operation.</summary>
public enum GridOperationError
{
    /// <summary>The grid or the adapter does not support the operation.</summary>
    Unsupported,

    /// <summary>The column or cell is read-only.</summary>
    ReadOnly,

    /// <summary>The column is hidden.</summary>
    Hidden,

    /// <summary>The grid has no on-screen layout yet.</summary>
    NotRealized,

    /// <summary>The view changed after the agent read it.</summary>
    StaleView
}

/// <summary>Thrown by a <see cref="GridAdapter"/> member to refuse an operation with a specific reason.</summary>
public sealed class GridOperationException : Exception
{
    public GridOperationException(GridOperationError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>The reason the operation was refused.</summary>
    public GridOperationError Error { get; }
}

/// <summary>
/// The size of the current view and a fingerprint of it. The fingerprint should change when the
/// view changes in a way that moves rows: recommended inputs are the row count, the column count,
/// the data-source identity and a hash of the sort and filter state. It is compared, never interpreted.
/// </summary>
public sealed class GridViewInfo
{
    public GridViewInfo(int rowCount, int columnCount, long fingerprint)
    {
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (columnCount < 0) throw new ArgumentOutOfRangeException(nameof(columnCount));
        RowCount = rowCount;
        ColumnCount = columnCount;
        Fingerprint = fingerprint;
    }

    /// <summary>Data rows in the current view.</summary>
    public int RowCount { get; }

    /// <summary>Columns in the adapter's column collection, visible or not.</summary>
    public int ColumnCount { get; }

    public long Fingerprint { get; }
}

/// <summary>
/// Lets an adapter tell the bridge that a grid's view changed, so that row positions an agent read
/// earlier are no longer valid. The bridge hands one to <see cref="GridAdapter.Attach"/>; it refers
/// only to the bridge's per-grid state, so it does not keep the grid alive.
/// </summary>
public sealed class GridViewSignal
{
    private readonly GridState state;

    internal GridViewSignal(GridState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// Call from the grid's events that change the view (data source, row count, sort, filter,
    /// column collection). Cheap; call it on the UI thread. It never throws into the event handler
    /// that calls it.
    /// </summary>
    public void Changed()
    {
        try { state.MarkChanged(); }
        catch (Exception) { /* Runs inside application event handlers; a failure here must not reach them. */ }
    }
}

/// <summary>
/// What an adapter reports about one column. Text is exposed verbatim apart from the length bounds
/// and the response budget the bridge applies: adapters must not put secrets in headers, bands or
/// choices, and mark a column <see cref="Redacted"/> instead of hiding its cells themselves.
/// </summary>
public sealed class GridColumnDescription
{
    internal const int MaxBands = 8;
    internal const int MaxChoices = 50;
    private readonly List<string?> bands = new();
    private readonly List<string> choices = new();
    private GridValueKind valueKind;

    internal GridColumnDescription()
    {
    }

    /// <summary>The column's name in the application, for example the designer name.</summary>
    public string? Name { get; set; }

    /// <summary>The data field the column is bound to, when it has one.</summary>
    public string? Field { get; set; }

    /// <summary>The caption a user sees.</summary>
    public string? Header { get; set; }

    /// <summary>False when the column is hidden. Defaults to true.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>True when a user can edit the column's cells now.</summary>
    public bool Editable { get; set; }

    /// <summary>
    /// True when the column's cell text is secret: the bridge then never reads it and never echoes edit
    /// text for it.
    /// </summary>
    public bool Redacted { get; set; }

    public GridValueKind ValueKind
    {
        get => valueKind;
        set
        {
            if (!Enum.IsDefined(typeof(GridValueKind), value)) throw new ArgumentOutOfRangeException(nameof(value));
            valueKind = value;
        }
    }

    internal IReadOnlyList<string?> Bands => bands;

    internal IReadOnlyList<string> Choices => choices;

    /// <summary>True when a choice was dropped because the list was full.</summary>
    internal bool ChoicesTruncated { get; private set; }

    /// <summary>
    /// Adds one level of the column's band (group header) path, outermost first; null is an
    /// unnamed band. Returns false (and drops it) once eight bands are added.
    /// </summary>
    public bool AddBand(string? caption)
    {
        if (bands.Count >= MaxBands) return false;
        bands.Add(caption);
        return true;
    }

    /// <summary>
    /// Adds the display text of one value a user can choose in this column. Returns false (and drops it,
    /// marking the list truncated) once 50 choices are added.
    /// </summary>
    public bool AddChoice(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (choices.Count >= MaxChoices)
        {
            ChoicesTruncated = true;
            return false;
        }
        choices.Add(text);
        return true;
    }
}

/// <summary>A request to edit one cell, as the agent gave it. The bridge has validated it before the adapter sees it.</summary>
public sealed class GridEditRequest
{
    private readonly Func<bool> viewChanged;

    internal GridEditRequest(int row, int column, string text, Func<bool> viewChanged)
    {
        Row = row;
        Column = column;
        Text = text;
        this.viewChanged = viewChanged;
    }

    /// <summary>The row position in the view the agent read.</summary>
    public int Row { get; }

    /// <summary>The column index in the adapter's column collection order.</summary>
    public int Column { get; }

    /// <summary>The text as a user would type it, in the application's UI culture. May be empty.</summary>
    public string Text { get; }

    /// <summary>
    /// A live check: true when the view changed since the agent read it. Check it after focusing the cell and
    /// before posting; when true throw <see cref="GridOperationException"/> with
    /// <see cref="GridOperationError.StaleView"/>. It reads the grid, so call it on the UI thread only (which is where
    /// <see cref="GridAdapter.SetCell"/> runs); do not hand the request to another thread and read it there.
    /// </summary>
    public bool ViewChanged => viewChanged();
}

/// <summary>The outcome of <see cref="GridAdapter.SetCell"/>: the edit was committed or refused.</summary>
public sealed class GridEditResult
{
    private GridEditResult()
    {
    }

    internal bool IsCommitted { get; private set; }

    internal string? DisplayText { get; private set; }

    internal int RowAfter { get; private set; }

    internal GridEditRejection Reason { get; private set; }

    internal string? ErrorText { get; private set; }

    /// <summary>
    /// The edit was posted and the row committed. <paramref name="displayText"/> is the text now shown in the
    /// cell; <paramref name="rowAfter"/> is the row's position in the view afterwards, or a negative value when
    /// the row is no longer in the view.
    /// </summary>
    public static GridEditResult Committed(string? displayText, int rowAfter)
        => new GridEditResult { IsCommitted = true, DisplayText = displayText, RowAfter = rowAfter < 0 ? -1 : rowAfter };

    /// <summary>
    /// The edit was refused. <paramref name="errorText"/> is the application's message when it has one (it is
    /// untrusted text); <paramref name="displayText"/> is the text now shown in the cell.
    /// </summary>
    public static GridEditResult Rejected(GridEditRejection reason, string? errorText, string? displayText = null)
    {
        if (!Enum.IsDefined(typeof(GridEditRejection), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new GridEditResult { Reason = reason, ErrorText = errorText, DisplayText = displayText, RowAfter = -1 };
    }
}
