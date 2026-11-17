using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>
    /// A stand-in for a third-party grid adapter that uses only the public seam (no InternalsVisibleTo).
    /// It accepts <see cref="FakeGrid"/>, counts every call, can throw from every member, can open a
    /// modal form in <see cref="Select"/> and <see cref="SetCell"/>, and returns a fingerprint that a
    /// test can change without raising any event. It keeps no reference to a grid.
    /// </summary>
    public sealed class FakeGridAdapter : GridAdapter
    {
        private readonly ConcurrentDictionary<string, int> calls = new ConcurrentDictionary<string, int>();
        private int features = (int)(GridFeatures.Select | GridFeatures.Scroll | GridFeatures.Edit);
        private long fingerprint;
        private readonly object signalLock = new object();
        private readonly List<GridViewSignal> keptSignals = new List<GridViewSignal>();
        private readonly List<WeakReference> signalReferences = new List<WeakReference>();
        private readonly List<Control> keptGrids = new List<Control>();

        public FakeGridAdapter(string name = "fake-grid") : base(name)
        {
        }

        /// <summary>The features GetFeatures reports; tests change it between requests.</summary>
        public GridFeatures Features
        {
            get => (GridFeatures)Volatile.Read(ref features);
            set => Volatile.Write(ref features, (int)value);
        }

        /// <summary>The fingerprint GetViewInfo reports. Changing it raises no event.</summary>
        public long Fingerprint
        {
            get => Interlocked.Read(ref fingerprint);
            set => Interlocked.Exchange(ref fingerprint, value);
        }

        /// <summary>When false, Attach does not forward the grid's ViewChanged event to the signal.</summary>
        public volatile bool ForwardViewEvents = true;

        /// <summary>Narrows Accepts further; null accepts every FakeGrid.</summary>
        public Func<Control, bool>? AcceptFilter { get; set; }

        // Throwing switches. Accepts throws only for a FakeGrid, so that one grid gives one diagnostic.
        public volatile bool ThrowInAccepts;
        public volatile bool ThrowInAttach;
        public volatile bool ThrowInGetFeatures;
        public volatile bool ThrowInGetViewInfo;
        public volatile bool ThrowInDescribeColumn;
        public volatile bool ThrowInIsRowSelected;
        public volatile bool ThrowInTryGetFocusedCell;
        public volatile bool ThrowInSelect;
        public volatile bool ThrowInScroll;
        public volatile bool ThrowInSetCell;

        /// <summary>When true, GetViewInfo refuses with GridOperationError.Unsupported, as for a grouped or virtual view.</summary>
        public volatile bool UnsupportedView;

        /// <summary>When true, IsRowSelected answers null ("cannot tell").</summary>
        public volatile bool NoRowSelection;

        /// <summary>Appended to the message of the exception ThrowInGetCellText throws, as an adapter that quotes the cell value would.</summary>
        public string? GetCellTextFailureDetail { get; set; }

        /// <summary>When set, Select refuses with a GridOperationException of this kind; its message quotes a cell value.</summary>
        public GridOperationError? RefuseSelectWith { get; set; }

        /// <summary>When set, Scroll refuses with a GridOperationException of this kind; its message quotes a cell value.</summary>
        public GridOperationError? RefuseScrollWith { get; set; }

        /// <summary>The message of a refusal from Select or Scroll: an adapter may quote the value it was handling.</summary>
        public const string RefusalMessage = "The value 'Bravo' (cell text) is invalid.";

        /// <summary>Appended to the message of the exception ThrowInSelect throws, as an adapter that quotes the cell value would.</summary>
        public string? SelectFailureDetail { get; set; }

        /// <summary>Appended to the message of the exception ThrowInScroll throws, as an adapter that quotes the cell value would.</summary>
        public string? ScrollFailureDetail { get; set; }

        /// <summary>
        /// When true, Attach keeps every signal it receives in an instance field, as an adapter may (the contract
        /// forbids keeping grids, not signals). A signal must not keep its grid alive.
        /// </summary>
        public volatile bool KeepSignals;

        /// <summary>
        /// When true, Attach keeps every grid it receives in an instance field, which the contract forbids. It is the
        /// negative control of the leak tests: the grid must then stay alive, so their collection loop can detect a leak.
        /// </summary>
        public volatile bool KeepGrids;

        /// <summary>When true, SetCell returns null instead of a result, which an adapter must never do.</summary>
        public volatile bool ReturnNoResult;

        /// <summary>Runs inside Accepts for a FakeGrid, once per call; a test uses it to re-enter the bridge.</summary>
        public Action<Control>? OnAccepts { get; set; }

        /// <summary>Weak references to every signal Attach received (kept or not).</summary>
        public WeakReference[] SignalReferences
        {
            get { lock (signalLock) return signalReferences.ToArray(); }
        }

        /// <summary>Returns true for the (row, column) whose GetCellText must throw.</summary>
        public Func<int, int, bool>? ThrowInGetCellText { get; set; }

        /// <summary>Appended to the message of the exception ThrowInSetCell throws, as an adapter that quotes the edit value would.</summary>
        public string? SetCellFailureDetail { get; set; }

        /// <summary>When set, SetCell refuses with a GridOperationException of this kind; its message quotes a cell value.</summary>
        public GridOperationError? RefuseSetCellWith { get; set; }

        /// <summary>When set, SetCell returns this rejection (with <see cref="ForcedErrorText"/> and the text now shown in the cell).</summary>
        public GridEditRejection? ForcedRejection { get; set; }

        /// <summary>The error text of <see cref="ForcedRejection"/>.</summary>
        public string? ForcedErrorText { get; set; }

        /// <summary>When set, a committed edit reports this as its row position after the edit (negative: not in the view).</summary>
        public int? ReportRowAfter { get; set; }

        /// <summary>Runs in SetCell after the modal form and before the view-changed check.</summary>
        public Action<FakeGrid>? BeforeViewCheck { get; set; }

        /// <summary>Runs in SetCell after the value was written, before the result is returned.</summary>
        public Action<FakeGrid>? AfterCommit { get; set; }

        /// <summary>Opens the returned form modally from Select, before the selection is applied.</summary>
        public Func<Form>? ModalFormInSelect { get; set; }

        /// <summary>Opens the returned form modally from SetCell, before the edit is applied.</summary>
        public Func<Form>? ModalFormInSetCell { get; set; }

        /// <summary>How many times a member was called (use nameof on the GridAdapter member).</summary>
        public int CallsOf(string member) => calls.TryGetValue(member, out int count) ? count : 0;

        /// <summary>How many times a member was called for the control with this name.</summary>
        public int CallsOf(string member, string controlName)
            => calls.TryGetValue(member + "|" + controlName, out int count) ? count : 0;

        private void Count(Control control, [CallerMemberName] string member = "")
        {
            calls.AddOrUpdate(member, 1, (key, count) => count + 1);
            calls.AddOrUpdate(member + "|" + control.Name, 1, (key, count) => count + 1);
        }

        /// <summary>Appended to the message of the exceptions thrown by ThrowInAccepts, ThrowInAttach and the other non-data throwing switches.</summary>
        public string? FailureDetail { get; set; }

        private InvalidOperationException Failure(string member) => new InvalidOperationException("Fake " + member + " failure." + FailureDetail);

        public override bool Accepts(Control control)
        {
            Count(control);
            if (control is FakeGrid) OnAccepts?.Invoke(control);
            if (control is FakeGrid && ThrowInAccepts) throw Failure(nameof(Accepts));
            return control is FakeGrid && (AcceptFilter == null || AcceptFilter(control));
        }

        public override void Attach(Control grid, GridViewSignal signal)
        {
            Count(grid);
            if (ThrowInAttach) throw Failure(nameof(Attach));
            lock (signalLock)
            {
                signalReferences.Add(new WeakReference(signal));
                if (KeepSignals) keptSignals.Add(signal);
                if (KeepGrids) keptGrids.Add(grid);
            }
            // The handler captures only the signal; the grid is not kept alive through it.
            if (ForwardViewEvents && grid is FakeGrid fake) fake.ViewChanged += (sender, eventArgs) => signal.Changed();
        }

        public override GridFeatures GetFeatures(Control grid)
        {
            Count(grid);
            if (ThrowInGetFeatures) throw Failure(nameof(GetFeatures));
            return Features;
        }

        public override GridViewInfo GetViewInfo(Control grid)
        {
            Count(grid);
            if (ThrowInGetViewInfo) throw Failure(nameof(GetViewInfo));
            if (UnsupportedView) throw new GridOperationException(GridOperationError.Unsupported, "Grouped views are not supported.");
            if (grid.IsDisposed) throw new ObjectDisposedException(grid.GetType().Name);
            var fake = (FakeGrid)grid;
            return new GridViewInfo(fake.Records.Count, fake.Columns.Count, Fingerprint);
        }

        public override void DescribeColumn(Control grid, int column, GridColumnDescription d)
        {
            Count(grid);
            if (ThrowInDescribeColumn) throw Failure(nameof(DescribeColumn));
            FakeGridColumn source = ((FakeGrid)grid).Columns[column];
            d.Name = source.Name;
            d.Field = source.Field;
            d.Header = source.Header;
            d.Visible = source.Visible;
            d.Editable = !source.ReadOnly;
            d.Redacted = source.Redacted;
            d.ValueKind = source.Kind;
            foreach (string? band in source.Bands) d.AddBand(band);
            foreach (string choice in source.Choices) d.AddChoice(choice);
        }

        public override string? GetCellText(Control grid, int row, int column)
        {
            Count(grid);
            if (ThrowInGetCellText != null && ThrowInGetCellText(row, column))
                throw new InvalidOperationException("Fake " + nameof(GetCellText) + " failure." + GetCellTextFailureDetail);
            return ((FakeGrid)grid).CellText(row, column);
        }

        public override bool? IsRowSelected(Control grid, int row)
        {
            Count(grid);
            if (ThrowInIsRowSelected) throw Failure(nameof(IsRowSelected));
            if (NoRowSelection) return null;
            return ((FakeGrid)grid).SelectedRow == row;
        }

        public override bool TryGetFocusedCell(Control grid, out int row, out int column)
        {
            Count(grid);
            if (ThrowInTryGetFocusedCell) throw Failure(nameof(TryGetFocusedCell));
            var fake = (FakeGrid)grid;
            row = fake.FocusedRow;
            column = fake.FocusedColumn;
            return row >= 0;
        }

        public override void Select(Control grid, int row, int? column)
        {
            Count(grid);
            if (ThrowInSelect) throw new InvalidOperationException("Fake " + nameof(Select) + " failure." + SelectFailureDetail);
            if (RefuseSelectWith.HasValue) throw new GridOperationException(RefuseSelectWith.Value, RefusalMessage);
            ShowModal(grid, ModalFormInSelect);
            var fake = (FakeGrid)grid;
            if (fake.IsDisposed) return;
            fake.FocusedRow = row;
            fake.FocusedColumn = column ?? fake.Columns.FindIndex(candidate => candidate.Visible);
            fake.SelectedRow = row;
            fake.FirstVisibleRow = row;
        }

        public override void Scroll(Control grid, int? row, int? column)
        {
            Count(grid);
            if (ThrowInScroll) throw new InvalidOperationException("Fake " + nameof(Scroll) + " failure." + ScrollFailureDetail);
            if (RefuseScrollWith.HasValue) throw new GridOperationException(RefuseScrollWith.Value, RefusalMessage);
            var fake = (FakeGrid)grid;
            if (row.HasValue) fake.FirstVisibleRow = row.Value;
            if (column.HasValue) fake.FirstVisibleColumn = column.Value;
        }

        public override GridEditResult SetCell(Control grid, GridEditRequest request)
        {
            Count(grid);
            if (ThrowInSetCell) throw new InvalidOperationException("Fake " + nameof(SetCell) + " failure." + SetCellFailureDetail);
            if (RefuseSetCellWith.HasValue) throw new GridOperationException(RefuseSetCellWith.Value, RefusalMessage);
            if (ReturnNoResult) return null!;
            var fake = (FakeGrid)grid;
            // The edit text sits in the hosted editor from here on, also while a modal dialog is up, and stays there
            // (hidden) afterwards: the bridge, not the grid, must keep it out of the tree.
            fake.ShowEditor(request.Text);
            try
            {
                return EditCell(fake, request);
            }
            finally
            {
                if (!fake.IsDisposed) fake.HideEditor();
            }
        }

        private GridEditResult EditCell(FakeGrid fake, GridEditRequest request)
        {
            ShowModal(fake, ModalFormInSetCell);
            if (fake.IsDisposed) return GridEditResult.Rejected(GridEditRejection.Other, null);
            BeforeViewCheck?.Invoke(fake);
            if (request.ViewChanged)
                throw new GridOperationException(GridOperationError.StaleView, "The view changed during the edit.");

            if (ForcedRejection.HasValue)
                return GridEditResult.Rejected(ForcedRejection.Value, ForcedErrorText, fake.CellText(request.Row, request.Column));

            FakeGridColumn column = fake.Columns[request.Column];
            if (column.ReadOnly)
                return GridEditResult.Rejected(GridEditRejection.NotEditable, null, fake.CellText(request.Row, request.Column));

            FakeEditContext context = fake.ValidateEdit(request.Row, request.Column, request.Text);
            if (fake.IsDisposed) return GridEditResult.Rejected(GridEditRejection.Other, null);
            if (context.Reject || context.ErrorText != null)
                return GridEditResult.Rejected(GridEditRejection.ValidationFailed, context.ErrorText,
                    fake.CellText(request.Row, request.Column));

            FakeRecord record = fake.View[request.Row];
            fake.SetCellValue(request.Row, request.Column, request.Text);
            int rowAfter = fake.ViewRowOf(record);
            GridEditResult committed = GridEditResult.Committed(rowAfter < 0 ? null : fake.CellText(rowAfter, request.Column), ReportRowAfter ?? rowAfter);
            AfterCommit?.Invoke(fake);
            return committed;
        }

        private static void ShowModal(Control owner, Func<Form>? factory)
        {
            if (factory == null) return;
            using (Form dialog = factory()) dialog.ShowDialog(owner);
        }
    }
}
