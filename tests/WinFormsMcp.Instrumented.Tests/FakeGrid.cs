using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>One row of <see cref="FakeGrid"/> data: text values by field name. A missing field reads as empty.</summary>
    public sealed class FakeRecord
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string this[string field]
        {
            get => values.TryGetValue(field, out string? value) ? value : "";
            set => values[field] = value;
        }
    }

    /// <summary>One column of <see cref="FakeGrid"/>, with the settings the adapter reports through the public seam.</summary>
    public sealed class FakeGridColumn
    {
        public FakeGridColumn(string field, string? header = null)
        {
            Field = field;
            Name = field + "Column";
            Header = header ?? field;
        }

        public string Name { get; set; }
        public string Field { get; set; }
        public string Header { get; set; }
        public bool Visible { get; set; } = true;
        public bool ReadOnly { get; set; }
        public bool Redacted { get; set; }
        public GridValueKind Kind { get; set; } = GridValueKind.Text;

        /// <summary>Band (group header) captions, outermost first; null is an unnamed band.</summary>
        public List<string?> Bands { get; } = new List<string?>();

        public List<string> Choices { get; } = new List<string>();
    }

    /// <summary>What the validation hook sees and decides while an edit is being posted.</summary>
    public sealed class FakeEditContext
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public string Field { get; set; } = "";
        public string Text { get; set; } = "";

        /// <summary>Set to refuse the edit.</summary>
        public bool Reject { get; set; }

        /// <summary>The application's error message; a non-null value also refuses the edit.</summary>
        public string? ErrorText { get; set; }
    }

    /// <summary>
    /// A hand-written stand-in for a third-party grid control: a plain <see cref="Control"/> over a list of
    /// records, with a sort, per-column settings, a validation hook and view-change events. Rows are
    /// positions in <see cref="View"/>: the records in sort order.
    /// </summary>
    public sealed class FakeGrid : Control
    {
        public FakeGrid()
        {
            // The cell editor is a child control of the grid, hidden until an edit starts (as in several real grids).
            Editor = new TextBox { Visible = false, Width = 80 };
            Controls.Add(Editor);
        }

        /// <summary>The hosted cell editor: a hidden child text box that holds the edit text while an edit is open.</summary>
        public TextBox Editor { get; }

        /// <summary>Opens the editor with <paramref name="text"/>.</summary>
        public void ShowEditor(string text)
        {
            Editor.Text = text;
            Editor.Visible = true;
        }

        /// <summary>Hides the editor and, like a grid that only hides it, leaves its text in place.</summary>
        public void HideEditor() => Editor.Visible = false;

        public List<FakeGridColumn> Columns { get; } = new List<FakeGridColumn>();

        /// <summary>The data. Changing it directly raises no event.</summary>
        public List<FakeRecord> Records { get; } = new List<FakeRecord>();

        /// <summary>The sorted column index, or -1.</summary>
        public int SortColumn { get; private set; } = -1;

        public bool SortDescending { get; private set; }

        /// <summary>Raised when the view changes through <see cref="Sort"/>, <see cref="ReplaceRecords"/> or an edit on the sort column.</summary>
        public event EventHandler? ViewChanged;

        /// <summary>Runs while an edit is posted; may reject it, set error text or open a modal form.</summary>
        public Action<FakeEditContext>? EditValidation { get; set; }

        public int FocusedRow { get; set; } = -1;
        public int FocusedColumn { get; set; } = -1;
        public int SelectedRow { get; set; } = -1;
        public int FirstVisibleRow { get; set; }
        public int FirstVisibleColumn { get; set; }

        /// <summary>The records in view order.</summary>
        public IReadOnlyList<FakeRecord> View
        {
            get
            {
                if (SortColumn < 0 || SortColumn >= Columns.Count) return Records.ToList();
                string field = Columns[SortColumn].Field;
                IOrderedEnumerable<FakeRecord> ordered = SortDescending
                    ? Records.OrderByDescending(record => record[field], StringComparer.Ordinal)
                    : Records.OrderBy(record => record[field], StringComparer.Ordinal);
                return ordered.ToList();
            }
        }

        public void Sort(int column, bool descending)
        {
            SortColumn = column;
            SortDescending = descending;
            NotifyViewChanged();
        }

        public void ReplaceRecords(IEnumerable<FakeRecord> records)
        {
            Records.Clear();
            Records.AddRange(records);
            NotifyViewChanged();
        }

        public void NotifyViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);

        public string CellText(int row, int column) => View[row][Columns[column].Field];

        /// <summary>The view position of a record, or -1 when it is not in the view.</summary>
        public int ViewRowOf(FakeRecord record) => View.ToList().IndexOf(record);

        /// <summary>Runs <see cref="EditValidation"/> for an edit about to be posted.</summary>
        public FakeEditContext ValidateEdit(int row, int column, string text)
        {
            var context = new FakeEditContext { Row = row, Column = column, Field = Columns[column].Field, Text = text };
            EditValidation?.Invoke(context);
            return context;
        }

        /// <summary>Writes a value into the record shown at <paramref name="row"/>; an edit on the sort column re-sorts the view.</summary>
        public void SetCellValue(int row, int column, string text)
        {
            View[row][Columns[column].Field] = text;
            if (column == SortColumn) NotifyViewChanged();
        }

        /// <summary>A modal form with a close button named <paramref name="name"/> + "Close", for hooks that must block.</summary>
        public static Form NewModal(string name)
        {
            var dialog = new Form { Name = name, Text = name, Width = 240, Height = 150 };
            var close = new Button { Name = name + "Close", Text = "Close", Top = 20 };
            close.Click += (sender, eventArgs) => dialog.Close();
            dialog.Controls.Add(close);
            return dialog;
        }
    }
}
