using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace InstrumentedDemo;

/// <summary>One row of the demo grid's data.</summary>
internal sealed class DemoRecord
{
    public DemoRecord(string item, int quantity, int reserved)
    {
        Item = item;
        Quantity = quantity;
        Reserved = reserved;
    }

    public string Item { get; }
    public int Quantity { get; set; }
    public int Reserved { get; }
}

/// <summary>What the demo grid knows about one column. <see cref="Bands"/> is the group-header path, outermost first.</summary>
internal sealed class DemoColumn
{
    public DemoColumn(string name, string field, string header, int width, bool editable, bool numeric, params string[] bands)
    {
        Name = name;
        Field = field;
        Header = header;
        Width = width;
        Editable = editable;
        Numeric = numeric;
        Bands = bands;
    }

    public string Name { get; }
    public string Field { get; }
    public string Header { get; }
    public int Width { get; }
    public bool Editable { get; }
    public bool Numeric { get; }
    public string[] Bands { get; }
}

/// <summary>Raised when the editor's value is refused. A handler that reports the error itself clears <see cref="ShowError"/>.</summary>
internal sealed class DemoInvalidValueEventArgs : EventArgs
{
    public DemoInvalidValueEventArgs(string text, string errorText)
    {
        Text = text;
        ErrorText = errorText;
    }

    public string Text { get; }
    public string ErrorText { get; }

    /// <summary>True (the default) makes the grid show the error in a message box, as a user sees it.</summary>
    public bool ShowError { get; set; } = true;
}

internal enum DemoEditStatus { Committed, NotANumber, Rejected, Declined }

/// <summary>What posting the editor did. <see cref="Record"/> is the row that was edited.</summary>
internal sealed class DemoEditOutcome
{
    public DemoEditOutcome(DemoEditStatus status, string? errorText, DemoRecord record)
    {
        Status = status;
        ErrorText = errorText;
        Record = record;
    }

    public DemoEditStatus Status { get; }
    public string? ErrorText { get; }
    public DemoRecord Record { get; }
}

/// <summary>
/// A small owner-drawn grid on a plain <see cref="Control"/>: rows of data, a read-only column, an editable numeric
/// column, a two-level banded header, a sort (click a column header), an in-cell editor (double-click, Enter, Escape),
/// a validation that refuses negative numbers, a confirmation dialog for large values, and a context menu whose
/// Opening handler enables Delete only when a row is selected. It knows nothing about the bridge; see
/// <c>DemoGridAdapter</c> for how an application teaches the bridge to read and edit it.
/// </summary>
internal sealed class DemoGrid : Control
{
    internal const int ItemColumn = 0;
    internal const int QuantityColumn = 1;
    internal const int ReservedColumn = 2;

    /// <summary>A quantity above this needs confirmation.</summary>
    internal const int LargeQuantity = 1000;

    private const int RowHeight = 24;

    private static readonly DemoColumn[] Columns =
    {
        new("colItem", "item", "Item", 160, editable: false, numeric: false),
        new("colQuantity", "quantity", "Quantity", 110, editable: true, numeric: true, "Stock", "On hand"),
        new("colReserved", "reserved", "Reserved", 110, editable: false, numeric: true, "Stock", "Held")
    };

    private static readonly int BandLevels = Columns.Max(column => column.Bands.Length);

    private readonly List<DemoRecord> _records = new()
    {
        new("Anvil", 30, 5), new("Bolt", 10, 0), new("Clamp", 20, 2), new("Drill", 5, 1),
        new("Easel", 45, 8), new("Funnel", 15, 3), new("Gasket", 60, 6), new("Hammer", 25, 4),
        new("Inkwell", 8, 0), new("Jack", 12, 2), new("Kettle", 33, 7), new("Lathe", 2, 1)
    };

    private readonly TextBox _editor = new() { Name = "demoGridEditor", Visible = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly ContextMenuStrip _menu;
    private List<DemoRecord> _view;
    private DemoRecord? _focused;
    private DemoRecord? _selected;
    private DemoRecord? _editing;

    public DemoGrid()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        _view = _records.ToList();
        _editor.KeyDown += EditorKeyDown;
        Controls.Add(_editor);
        _menu = BuildContextMenu();
        ContextMenuStrip = _menu;
    }

    /// <summary>Raised when the rows or the values the grid shows change: a sort, a deletion or a committed edit.</summary>
    internal event EventHandler? ViewChanged;

    /// <summary>Raised when the editor's value is refused, before the grid shows its own error message.</summary>
    internal event EventHandler<DemoInvalidValueEventArgs>? InvalidValue;

    /// <summary>Where the grid reports what it did (the demo form shows it in its status bar).</summary>
    internal Action<string>? ReportStatus { get; set; }

    internal int RowCount => _view.Count;
    internal int ColumnCount => Columns.Length;
    internal int SortColumn { get; private set; } = -1;
    internal bool SortDescending { get; private set; }
    internal int FirstVisibleRow { get; private set; }
    internal int FocusedColumn { get; private set; } = -1;
    internal int FocusedRow => _focused == null ? -1 : _view.IndexOf(_focused);
    internal int SelectedRow => _selected == null ? -1 : _view.IndexOf(_selected);
    internal bool IsEditing => _editing != null;

    internal DemoColumn ColumnAt(int column) => Columns[column];

    internal string CellText(int row, int column)
    {
        DemoRecord record = _view[row];
        switch (column)
        {
            case ItemColumn: return record.Item;
            case QuantityColumn: return record.Quantity.ToString(CultureInfo.CurrentCulture);
            default: return record.Reserved.ToString(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The view position of a record, or -1 when it is not in the view.</summary>
    internal int RowOf(DemoRecord record) => _view.IndexOf(record);

    internal void Sort(int column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
        Rebuild();
        NotifyViewChanged();
    }

    /// <summary>Focuses a cell and brings it into view; the selection stays as it was.</summary>
    internal void FocusCell(int row, int column)
    {
        _focused = _view[row];
        FocusedColumn = column;
        ScrollIntoView(row);
        Invalidate();
    }

    /// <summary>Focuses a cell, makes its row the only selection and brings it into view.</summary>
    internal void SelectCell(int row, int column)
    {
        _selected = _view[row];
        FocusCell(row, column);
    }

    internal void ScrollIntoView(int row)
    {
        int visible = VisibleRowCount;
        if (row < FirstVisibleRow) FirstVisibleRow = row;
        else if (row >= FirstVisibleRow + visible) FirstVisibleRow = row - visible + 1;
        FirstVisibleRow = Math.Max(0, FirstVisibleRow);
        Invalidate();
    }

    internal void DeleteSelectedRow()
    {
        DemoRecord? doomed = _selected;
        if (doomed == null) return;
        _records.Remove(doomed);
        if (_focused == doomed) _focused = null;
        _selected = null;
        Rebuild();
        FirstVisibleRow = Math.Max(0, Math.Min(FirstVisibleRow, _view.Count - 1));
        NotifyViewChanged();
        ReportStatus?.Invoke("Deleted " + doomed.Item + "; " + _view.Count + " rows left");
    }

    /// <summary>Where a context menu opened from code should appear: the focused cell, else the middle of the grid.</summary>
    internal Point ContextMenuAnchor
    {
        get
        {
            int row = FocusedRow;
            if (row < 0 || FocusedColumn < 0) return new Point(Width / 2, Height / 2);
            Rectangle cell = CellBounds(row, FocusedColumn);
            return new Point(Math.Max(0, Math.Min(cell.Left + cell.Width / 2, Width - 1)),
                             Math.Max(0, Math.Min(cell.Top + cell.Height / 2, Height - 1)));
        }
    }

    // --- editing -------------------------------------------------------------------------------------------

    /// <summary>Shows the editor over a cell. False when the column is read-only or the grid is not on screen.</summary>
    internal bool BeginEdit(int row, int column)
    {
        if (!Columns[column].Editable || !IsHandleCreated || !Visible) return false;
        CancelEdit();
        ScrollIntoView(row);
        _editing = _view[row];
        _editor.Bounds = CellBounds(row, column);
        _editor.Text = CellText(row, column);
        _editor.Visible = true;
        _editor.Focus();
        _editor.SelectAll();
        return true;
    }

    internal string EditorText
    {
        get => _editor.Text;
        set => _editor.Text = value;
    }

    /// <summary>Closes the editor without posting its value. Does nothing when no cell is being edited.</summary>
    internal void CancelEdit()
    {
        if (_editing == null) return;
        _editing = null;
        _editor.Visible = false;
        // A hidden editor keeps its text otherwise, and the text of a cell must not outlive the edit.
        _editor.Text = string.Empty;
        Invalidate();
    }

    /// <summary>
    /// Posts the editor's value and closes the editor: a refused value raises <see cref="InvalidValue"/> and shows an error
    /// message unless a handler takes that over; a large value opens a modal confirmation dialog.
    /// </summary>
    internal DemoEditOutcome PostEdit()
    {
        DemoRecord record = _editing ?? throw new InvalidOperationException("No cell is being edited.");
        string text = _editor.Text.Trim();
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out int value))
            return Refuse(DemoEditStatus.NotANumber, "Quantity must be a whole number.", text, record);
        if (value < 0)
            return Refuse(DemoEditStatus.Rejected, "Quantity must not be negative.", text, record);
        if (value > LargeQuantity && !Confirm(record, value))
        {
            // The dialog is modal: the grid can have been disposed meanwhile.
            if (!IsDisposed) CancelEdit();
            return new DemoEditOutcome(DemoEditStatus.Declined, "The change was not confirmed.", record);
        }
        // The dialog is modal, so the grid can also have been disposed while the user kept the change.
        if (IsDisposed) return new DemoEditOutcome(DemoEditStatus.Declined, "The grid was closed.", record);

        record.Quantity = value;
        CancelEdit();
        Rebuild();
        NotifyViewChanged();
        return new DemoEditOutcome(DemoEditStatus.Committed, null, record);
    }

    private DemoEditOutcome Refuse(DemoEditStatus status, string errorText, string text, DemoRecord record)
    {
        CancelEdit();
        var args = new DemoInvalidValueEventArgs(text, errorText);
        InvalidValue?.Invoke(this, args);
        if (args.ShowError)
            MessageBox.Show(this, errorText, "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return new DemoEditOutcome(status, errorText, record);
    }

    private bool Confirm(DemoRecord record, int value)
    {
        using var dialog = new Form
        {
            Name = "dlgConfirmQuantity", Text = "Confirm quantity", ClientSize = new Size(380, 120),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        var message = new Label
        {
            Name = "lblConfirmQuantity", Left = 12, Top = 12, Width = 356, Height = 44,
            Text = "Set the quantity of " + record.Item + " to " + value + "? That is more than " + LargeQuantity + "."
        };
        var keep = new Button { Name = "btnConfirmKeep", Text = "Keep", Left = 12, Top = 70, Width = 100, DialogResult = DialogResult.Yes };
        var discard = new Button { Name = "btnConfirmDiscard", Text = "Discard", Left = 124, Top = 70, Width = 100, DialogResult = DialogResult.No };
        dialog.Controls.AddRange(new Control[] { message, keep, discard });
        dialog.AcceptButton = keep;
        dialog.CancelButton = discard;
        return dialog.ShowDialog(this) == DialogResult.Yes;
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            PostEdit();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            CancelEdit();
        }
    }

    // --- view ----------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        IEnumerable<DemoRecord> ordered = _records;
        switch (SortColumn)
        {
            case ItemColumn:
                ordered = SortDescending ? _records.OrderByDescending(r => r.Item, StringComparer.Ordinal) : _records.OrderBy(r => r.Item, StringComparer.Ordinal);
                break;
            case QuantityColumn:
                ordered = SortDescending ? _records.OrderByDescending(r => r.Quantity) : _records.OrderBy(r => r.Quantity);
                break;
            case ReservedColumn:
                ordered = SortDescending ? _records.OrderByDescending(r => r.Reserved) : _records.OrderBy(r => r.Reserved);
                break;
        }
        _view = ordered.ToList();
        Invalidate();
    }

    private void NotifyViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip { Name = "ctxGrid" };
        var delete = new ToolStripMenuItem("Delete row") { Name = "ctxGridDelete", Enabled = false };
        var info = new ToolStripMenuItem("Grid info") { Name = "ctxGridInfo" };
        delete.Click += (_, _) => DeleteSelectedRow();
        info.Click += (_, _) => ReportStatus?.Invoke("The grid has " + _view.Count + " rows");
        menu.Items.Add(delete);
        menu.Items.Add(info);
        // Delete makes sense only for a selected row; the strip decides this each time it opens.
        menu.Opening += (_, _) => delete.Enabled = SelectedRow >= 0;
        return menu;
    }

    // --- geometry, painting and the mouse --------------------------------------------------------------------

    private static int HeaderHeight => (BandLevels + 1) * RowHeight;

    private int VisibleRowCount => Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);

    private static int ColumnLeft(int column)
    {
        int left = 0;
        for (int i = 0; i < column; i++) left += Columns[i].Width;
        return left;
    }

    private Rectangle CellBounds(int row, int column)
        => new(ColumnLeft(column), HeaderHeight + (row - FirstVisibleRow) * RowHeight, Columns[column].Width, RowHeight);

    private static string? BandCaption(int column, int level)
        => level < Columns[column].Bands.Length ? Columns[column].Bands[level] : null;

    private bool HitTest(Point point, out int row, out int column)
    {
        row = -1;
        column = -1;
        for (int c = 0; c < Columns.Length; c++)
            if (point.X >= ColumnLeft(c) && point.X < ColumnLeft(c) + Columns[c].Width) column = c;
        if (point.Y >= HeaderHeight)
        {
            int index = FirstVisibleRow + (point.Y - HeaderHeight) / RowHeight;
            if (index < _view.Count) row = index;
        }
        return column >= 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(SystemColors.Window);
        const TextFormatFlags format = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        // The band rows (columns next to each other with the same caption share one cell), then the column headers.
        for (int level = 0; level <= BandLevels; level++)
        {
            for (int c = 0; c < Columns.Length;)
            {
                string? caption = level < BandLevels ? BandCaption(c, level) : Columns[c].Header;
                int span = 1;
                while (level < BandLevels && c + span < Columns.Length && BandCaption(c + span, level) == caption) span++;
                var cell = new Rectangle(ColumnLeft(c), level * RowHeight, ColumnLeft(c + span) - ColumnLeft(c), RowHeight);
                if (caption != null)
                {
                    g.FillRectangle(SystemBrushes.Control, cell);
                    g.DrawRectangle(SystemPens.ControlDark, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
                    string arrow = level == BandLevels && c == SortColumn ? (SortDescending ? " \u25BC" : " \u25B2") : "";
                    TextRenderer.DrawText(g, caption + arrow, Font, Rectangle.Inflate(cell, -4, 0), SystemColors.ControlText, format);
                }
                c += span;
            }
        }

        int last = Math.Min(_view.Count, FirstVisibleRow + VisibleRowCount + 1);
        for (int row = FirstVisibleRow; row < last; row++)
        {
            bool selected = _view[row] == _selected;
            for (int c = 0; c < Columns.Length; c++)
            {
                Rectangle cell = CellBounds(row, c);
                g.FillRectangle(selected ? SystemBrushes.Highlight : SystemBrushes.Window, cell);
                g.DrawRectangle(SystemPens.ControlLight, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
                TextRenderer.DrawText(g, CellText(row, c), Font, Rectangle.Inflate(cell, -4, 0),
                    selected ? SystemColors.HighlightText : SystemColors.WindowText, format);
                if (_view[row] == _focused && c == FocusedColumn) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(cell, -1, -1));
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        CancelEdit();
        Focus();
        if (!HitTest(e.Location, out int row, out int column)) return;
        if (e.Y >= BandLevels * RowHeight && e.Y < HeaderHeight && e.Button == MouseButtons.Left)
            Sort(column, SortColumn == column && !SortDescending);
        else if (row >= 0)
            SelectCell(row, column);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (HitTest(e.Location, out int row, out int column) && row >= 0) BeginEdit(row, column);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int top = Math.Max(0, _view.Count - VisibleRowCount);
        FirstVisibleRow = Math.Max(0, Math.Min(top, FirstVisibleRow - Math.Sign(e.Delta) * 3));
        CancelEdit();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
