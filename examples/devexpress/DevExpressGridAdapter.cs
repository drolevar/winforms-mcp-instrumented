#if WINFORMS_MCP_INSTRUMENTED
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraEditors.Repository;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Columns;
using DevExpress.XtraGrid.Views.BandedGrid;
using DevExpress.XtraGrid.Views.Base;
using DevExpress.XtraGrid.Views.Grid;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Examples.Adapters
{
    /// <summary>
    /// Teaches the WinForms MCP bridge to read DevExpress grids: a <see cref="GridControl"/> whose main view is
    /// a <see cref="GridView"/> or a <see cref="BandedGridView"/> (this covers the application's wrappers, which
    /// derive from GridControl). Compiled only when WINFORMS_MCP_INSTRUMENTED is defined; every member runs on the UI thread.
    /// It never creates window handles, shows UI or changes focus or selection when reading, and keeps no grid,
    /// view or signal in a field or a static.
    ///
    /// Rows are the data rows of the current view, positions 0..DataRowCount-1; with no grouping the row handle
    /// of the view equals the position, so a position is used as the handle. Columns are indexes in
    /// <c>view.Columns</c> (all columns, hidden ones included).
    ///
    /// Not supported (GetFeatures is None, GetViewInfo refuses with Unsupported): a main view that is not a
    /// GridView or BandedGridView (card, layout, WinExplorer views), a detail view, a grouped view, a server-mode
    /// view (the group and server handles are not positions) and a master-detail grid.
    ///
    /// Known limits: a plain List&lt;T&gt; raises no list events, so a same-count change made in code is not
    /// detected (a re-read is the truth); the events are attached to the main view and the data controller the grid
    /// has when the bridge first accepts it, so a main view or a data controller replaced later is seen only through
    /// the fingerprint (which covers the main view and the data source identity).
    /// </summary>
    internal sealed class DevExpressGridAdapter : GridAdapter
    {
        internal const string AdapterName = "devexpress.grid";

        internal DevExpressGridAdapter()
            : base(AdapterName)
        {
        }

        // Runs for every control in every tree walk: keep it a type test.
        public override bool Accepts(Control control) => control is GridControl;

        public override void Attach(Control grid, GridViewSignal signal)
        {
            // The handlers capture only the signal, which refers only to the bridge's per-grid state. They do not
            // capture the grid or the view, so grid -> handler -> signal -> state keeps nothing alive that the
            // grid does not.
            var control = (GridControl)grid;
            control.DataSourceChanged += (sender, e) => signal.Changed();

            var view = control.MainView as GridView;
            if (view == null) return;
            view.DataController.ListSourceChanged += (sender, e) => signal.Changed();
            view.DataController.VisibleRowCountChanged += (sender, e) => signal.Changed();
            // Every change the data source reports (an item that changed and moved in a sorted view, a reset
            // with the same count): none of the events above or the fingerprint sees those.
            view.DataController.ListChanged += (sender, e) => signal.Changed();
            view.ColumnFilterChanged += (sender, e) => signal.Changed();
            view.EndSorting += (sender, e) => signal.Changed();
            view.EndGrouping += (sender, e) => signal.Changed();
            view.ColumnPositionChanged += (sender, e) => signal.Changed();
            view.CellValueChanged += (sender, e) => signal.Changed();
        }

        public override GridFeatures GetFeatures(Control grid)
        {
            GridView view = SupportedViewOrNull(grid);
            if (view == null) return GridFeatures.None;
            GridFeatures features = GridFeatures.Select | GridFeatures.Scroll;
            if (IsViewEditable(view)) features |= GridFeatures.Edit;
            return features;
        }

        public override GridViewInfo GetViewInfo(Control grid)
        {
            GridView view = SupportedViewOrNull(grid);
            if (view == null)
                throw new GridOperationException(
                    GridOperationError.Unsupported,
                    "Only a plain or banded grid view without grouping, server mode or detail views is supported.");

            // What moves rows or columns: the row and column counts, the sort and the filter, and the identity of
            // the view and of the data source. Cell values are covered by the CellValueChanged signal.
            int rows = view.DataRowCount;
            int columns = view.Columns.Count;
            unchecked
            {
                long fingerprint = rows;
                fingerprint = fingerprint * 31 + columns;
                foreach (GridColumn column in view.Columns)
                {
                    fingerprint = fingerprint * 31 + (int)column.SortOrder;
                    fingerprint = fingerprint * 31 + column.SortIndex;
                }
                string filter = view.ActiveFilterString;
                fingerprint = fingerprint * 31 + (filter == null ? 0 : filter.GetHashCode());
                fingerprint = fingerprint * 31 + RuntimeHelpers.GetHashCode(view);
                object source = ((GridControl)grid).DataSource;
                fingerprint = fingerprint * 31 + (source == null ? 0 : RuntimeHelpers.GetHashCode(source));
                return new GridViewInfo(rows, columns, fingerprint);
            }
        }

        public override void DescribeColumn(Control grid, int column, GridColumnDescription d)
        {
            GridView view = SupportedViewOrNull(grid);
            if (view == null) throw new GridOperationException(GridOperationError.Unsupported, "The grid view is not supported.");
            GridColumn source = view.Columns[column];

            d.Name = string.IsNullOrEmpty(source.Name) ? source.FieldName : source.Name;
            d.Field = source.FieldName;
            d.Header = source.GetTextCaption();
            d.Visible = IsColumnVisible(source);
            RepositoryItem edit = source.RealColumnEdit;
            // GridColumn.ReadOnly does not look at the repository item: a read-only editor makes the column read-only.
            d.Editable = IsViewEditable(view) && source.OptionsColumn.AllowEdit && !source.ReadOnly
                         && (edit == null || !edit.ReadOnly);

            d.ValueKind = KindOf(edit, source.ColumnType);
            d.Redacted = DevExpressGridEditors.IsRedacted(edit);

            // Choices come from an editor that owns a list of its own. A lookup editor's list is a data source
            // and is not enumerated; a redacted column's choices could be the secrets, so none are listed.
            var combo = edit as RepositoryItemComboBox;
            if (combo != null && !d.Redacted)
            {
                foreach (object item in combo.Items)
                {
                    string choice = item == null ? string.Empty : (item.ToString() ?? string.Empty);
                    if (!d.AddChoice(choice)) break;
                }
            }

            // The band path, outermost band first: the owner band is the innermost one.
            var banded = source as BandedGridColumn;
            if (banded != null && banded.OwnerBand != null)
            {
                var captions = new List<string>();
                for (GridBand band = banded.OwnerBand; band != null; band = band.ParentBand)
                    captions.Add(band.Caption);
                captions.Reverse();
                foreach (string caption in captions)
                {
                    if (!d.AddBand(caption)) break;
                }
            }
        }

        public override string GetCellText(Control grid, int row, int column)
        {
            GridView view = SupportedViewOrNull(grid);
            if (view == null) throw new GridOperationException(GridOperationError.Unsupported, "The grid view is not supported.");
            return view.GetRowCellDisplayText(row, view.Columns[column]);
        }

        public override bool? IsRowSelected(Control grid, int row)
        {
            GridView view = SupportedViewOrNull(grid);
            // In single-select mode DevExpress reports the focused row as the selected one.
            return view == null ? (bool?)null : view.IsRowSelected(row);
        }

        public override bool TryGetFocusedCell(Control grid, out int row, out int column)
        {
            row = -1;
            column = -1;
            GridView view = SupportedViewOrNull(grid);
            if (view == null || view.FocusedRowHandle < 0 || view.FocusedColumn == null) return false;
            int index = view.Columns.IndexOf(view.FocusedColumn);
            if (index < 0) return false;
            row = view.FocusedRowHandle;
            column = index;
            return true;
        }

        // Select and Scroll need a layout. Without one DevExpress does not throw: MakeRowVisible and MakeColumnVisible
        // silently do nothing, so a success would be a lie. A layout exists when the grid has a window handle AND
        // the view has a non-empty ViewRect (a grid created with an empty size, e.g. in a collapsed splitter panel,
        // has a handle but no layout). Both operations refuse with NotRealized before anything is changed, and never
        // create the handle or force a layout: IsHandleCreated and ViewRect are plain reads (the view info itself is
        // not public). Chosen over "verify after scrolling" because it refuses before the focus or selection moves;
        // the row is verified afterwards as well (IsRowVisible, a public read) for any other state in which DevExpress
        // skips the scroll. A column needs no such check: in an auto-width grid (ColumnAutoWidth) every column is
        // always in view and MakeColumnVisible is rightly a no-op, which must not be refused.
        // The bridge has already clamped the row and the column and checked the token; the range checks here only
        // guard a direct call. Exception messages are fixed texts: they never contain cell text.
        public override void Select(Control grid, int row, int? column)
        {
            GridView view = RealizedViewOrThrow(grid);
            GridColumn target = column.HasValue
                ? VisibleColumnOrThrow(view, column.Value)
                : FirstVisibleColumnOrThrow(view);
            CheckRow(view, row);
            // Moving the focus validates and commits a pending edit (and opens a modal if it is invalid): the same
            // entry check as SetCell, so another party's edit is never touched.
            if (HasPendingEdit(view))
                throw new InvalidOperationException("Another edit is in progress.");

            // Focusing is what a user's click does: it can be refused (BeforeLeaveRow, an editor that does not
            // validate, an invalid pending row). Each step is checked before the next one, so a refused row change
            // leaves the old row's focused column alone and nothing is selected or scrolled.
            view.FocusedRowHandle = row;
            if (view.FocusedRowHandle != row)
                throw new InvalidOperationException("The grid did not move its focus to the requested cell.");
            view.FocusedColumn = target;
            if (view.FocusedColumn != target)
                throw new InvalidOperationException("The grid did not move its focus to the requested cell.");

            // Make it the only selection. Single-select mode selects the focused row by itself.
            if (view.OptionsSelection.MultiSelect)
            {
                view.ClearSelection();
                if (view.OptionsSelection.MultiSelectMode == GridMultiSelectMode.CellSelect)
                    view.SelectCell(row, target);
                else
                    view.SelectRow(row);
            }

            view.MakeRowVisible(row);
            view.MakeColumnVisible(target);
            VerifyRowInView(view, row);
        }

        public override void Scroll(Control grid, int? row, int? column)
        {
            GridView view = RealizedViewOrThrow(grid);
            // Both targets are checked before the first scroll, so a refusal leaves the view where it was.
            GridColumn target = column.HasValue ? VisibleColumnOrThrow(view, column.Value) : null;
            if (row.HasValue) CheckRow(view, row.Value);

            if (row.HasValue) view.MakeRowVisible(row.Value);
            if (target != null) view.MakeColumnVisible(target);
            if (row.HasValue) VerifyRowInView(view, row.Value);
        }

        // The edit sequence of docs/10-grid-adapters.md: focus the cell, check the view did not change, show the
        // editor, set its value from the typed text, post it, close the editor and commit the row. The bridge has
        // already checked the token, the range, the visibility and the editability, so the guards here are for a
        // direct call.
        //
        // Everything that could open a modal window is switched to no-modal for the duration of this call: the
        // grid's invalid-value and invalid-row events are set to NoAction while their error text is captured (the
        // application's own handlers run first, ours is the last one subscribed). A message box the application's
        // code shows by itself keeps the receipt started, by design.
        //
        // Committed means: the row commit succeeded, the editor is closed, and the cell holds the value the editor
        // validated (the ValidatingEditor value, which already carries the application's editor-level normalisation,
        // converted to the column's type). A setter that ignores or clamps the value, or a CellValueChanged handler
        // that rewrites it, is therefore a refusal (RowNotCommitted) and is not undone. When the intended value cannot
        // be converted to the column's type, the cell text right after posting is compared with the text after the
        // commit instead. The editor's own DisplayText is not used: it is the edit format, which legitimately differs
        // from the cell's display format (dates, numbers with a display format).
        //
        // A refused row on a plain list: the editor has already written the value into the object by then, and
        // CancelUpdateCurrentRow can undo that only for an IEditableObject (or a DataRowView). The adapter therefore
        // writes the old cell value back through the view, like the editor did, so that a refused edit leaves the data
        // as it was; on a plain object this fires CellValueChanged and RowEditCanceled a second time. The restore is
        // checked, and the refusal's error text is kept across it. An IEditableObject is rolled back by the grid and
        // is not written to again.
        //
        // A row that is already modified (a user's or another caller's pending edit) is never touched: focusing
        // another row would validate it before our handlers are subscribed (DevExpress would open a modal), and
        // cancelling would throw the other edit away. Such a call is refused up front.
        //
        // Exception messages are fixed texts: they never contain the typed text or cell text.
        public override GridEditResult SetCell(Control grid, GridEditRequest request)
        {
            GridView view = RealizedViewOrThrow(grid);
            GridColumn column = VisibleColumnOrThrow(view, request.Column);
            int row = request.Row;
            CheckRow(view, row);

            if (HasPendingEdit(view))
                return GridEditResult.Rejected(GridEditRejection.EditorNotShown, "Another edit is in progress.", DisplayTextOrNull(view, row, column));
            if (!IsViewEditable(view) || !column.OptionsColumn.AllowEdit || column.ReadOnly
                || (column.RealColumnEdit != null && column.RealColumnEdit.ReadOnly))
                return GridEditResult.Rejected(GridEditRejection.NotEditable, "The cell cannot be edited.", DisplayTextOrNull(view, row, column));

            // Focusing is what a user's click does and can be refused; see Select.
            view.FocusedRowHandle = row;
            if (view.FocusedRowHandle != row)
                throw new InvalidOperationException("The grid did not move its focus to the requested cell.");
            view.FocusedColumn = column;
            if (view.FocusedColumn != column)
                throw new InvalidOperationException("The grid did not move its focus to the requested cell.");
            if (request.ViewChanged)
                throw new GridOperationException(GridOperationError.StaleView, "The view changed before the editor opened.");

            // The edited record is tracked by its list-source index and checked by reference: rows that are equal by
            // value (Equals overridden) must not be mistaken for each other after a sort or a refused edit.
            object record = view.GetRow(row);
            int source = view.GetDataSourceRowIndex(row);
            object oldValue = view.GetRowCellValue(row, column);
            bool redacted = DevExpressGridEditors.IsRedacted(column.RealColumnEdit);

            string errorText = null;
            bool validatorRefused = false;
            bool valueRefused = false;
            bool valueWritten = false;
            bool haveIntended = false;
            object intended = null;
            InvalidValueExceptionEventHandler onInvalidValue = (sender, e) =>
            {
                e.ExceptionMode = ExceptionMode.NoAction;
                valueRefused = true;
                if (!string.IsNullOrEmpty(e.ErrorText)) errorText = e.ErrorText;
            };
            InvalidRowExceptionEventHandler onInvalidRow = (sender, e) =>
            {
                e.ExceptionMode = ExceptionMode.NoAction;
                if (!string.IsNullOrEmpty(e.ErrorText)) errorText = e.ErrorText;
            };
            BaseContainerValidateEditorEventHandler onValidating = (sender, e) =>
            {
                if (e.Valid)
                {
                    // What the application's validation left as the editor's value: the value the commit must hold.
                    intended = e.Value;
                    haveIntended = true;
                    return;
                }
                validatorRefused = true;
                if (!string.IsNullOrEmpty(e.ErrorText)) errorText = e.ErrorText;
            };
            CellValueChangedEventHandler onValueChanged = (sender, e) =>
            {
                if (e.RowHandle == row && e.Column == column) valueWritten = true;
            };

            bool rejected = false;
            GridEditRejection reason = GridEditRejection.Other;
            string message = null;
            BaseEdit editor = null;
            bool finished = false;
            bool gone = false;
            view.InvalidValueException += onInvalidValue;
            view.InvalidRowException += onInvalidRow;
            view.ValidatingEditor += onValidating;
            view.CellValueChanged += onValueChanged;
            try
            {
                view.ShowEditor();
                editor = view.ActiveEditor;
                if (editor == null)
                {
                    rejected = true;
                    reason = GridEditRejection.EditorNotShown;
                }
                else if (editor.Properties.ReadOnly)
                {
                    // Checked before anything is put into the editor: setting a value would already raise the
                    // application's EditValueChanged handlers, and a read-only editor still posts a value it was given.
                    // (GridColumn.ReadOnly ignores the repository item, and a per-row editor is only known now.)
                    rejected = true;
                    reason = GridEditRejection.NotEditable;
                    message = "The editor is read-only.";
                }
                else if (!DevExpressGridEditors.TryApply(editor, request.Text, out reason, out message))
                {
                    rejected = true;
                }
                else if (!view.PostEditor())
                {
                    rejected = true;
                    reason = validatorRefused ? GridEditRejection.ValidationFailed : GridEditRejection.InvalidInput;
                    message = errorText;
                }
                else
                {
                    string posted = DisplayTextOrNull(view, RowOf(view, record, source, row), column);
                    view.HideEditor();
                    if (valueRefused || !view.UpdateCurrentRow())
                    {
                        // Either the application's code threw while the value was written (an exception in a
                        // CellValueChanged handler is routed to InvalidValueException, PostEditor still returns true and
                        // the data controller has begun the row edit), or the row was refused. In both cases the row
                        // is not committed: cancel it and give the old value back.
                        view.CancelUpdateCurrentRow();
                        // The restore can route through InvalidValueException and overwrite the captured text with a
                        // message that quotes values: keep the application's text.
                        string kept = errorText;
                        bool undone = RestoreCell(view, record, source, row, column, oldValue);
                        errorText = kept;
                        rejected = true;
                        reason = valueRefused ? GridEditRejection.ValidationFailed : GridEditRejection.RowNotCommitted;
                        message = undone ? kept : "The refused value could not be undone.";
                    }
                    else if (!valueWritten)
                    {
                        rejected = true;
                        message = "The editor did not post the value.";
                    }
                    else if (view.IsEditing)
                    {
                        rejected = true;
                        reason = GridEditRejection.RowNotCommitted;
                    }
                    else
                    {
                        // A record that left the view (a filter on the edited column) is read through the list source;
                        // the commit stands and the receipt carries no row.
                        int at = RowOf(view, record, source, row);
                        bool? holds = null;
                        if (haveIntended)
                        {
                            object now = at >= 0 ? view.GetRowCellValue(at, column) : view.GetListSourceRowCellValue(source, column);
                            holds = HoldsValue(intended, now, column.ColumnType);
                        }
                        bool same = holds ?? (at < 0 || string.Equals(posted, DisplayTextOrNull(view, at, column), StringComparison.Ordinal));
                        if (!same)
                        {
                            // The value was lost or changed after the post (a setter, a CellValueChanged handler, a
                            // row handler): not undone, the shown text comes back.
                            rejected = true;
                            reason = GridEditRejection.RowNotCommitted;
                        }
                    }
                }
                finished = true;
            }
            catch (Exception) when (IsGone(grid, view))
            {
                // The application closed the window from one of its handlers; the grid is not usable any more.
                gone = true;
            }
            finally
            {
                // Always unsubscribed, whatever happened to the grid; only touching the grid is skipped once it is gone.
                view.InvalidValueException -= onInvalidValue;
                view.InvalidRowException -= onInvalidRow;
                view.ValidatingEditor -= onValidating;
                view.CellValueChanged -= onValueChanged;
                if (!IsGone(grid, view))
                {
                    if (view.IsEditing) view.HideEditor();
                    // The row was not modified when the call began (see the entry check), so a modified row now is ours:
                    // typing into the editor marks it, and a refusal before the commit would leave it marked.
                    if (!finished || view.FocusedRowModified || view.DataController.IsCurrentRowModified)
                        view.CancelUpdateCurrentRow();
                    // A hidden editor keeps its text: a redacted column's text must not stay in the grid's controls.
                    if (editor != null && (redacted || DevExpressGridEditors.IsRedacted(editor.Properties)))
                        editor.EditValue = null;
                }
            }
            if (gone || IsGone(grid, view)) return GridEditResult.Rejected(GridEditRejection.Other, null);

            int rowAfter = RowOf(view, record, source, row);
            string shown = DisplayTextOrNull(view, rowAfter, column);
            if (rejected) return GridEditResult.Rejected(reason, message, shown);
            return GridEditResult.Committed(shown, rowAfter);
        }

        // True once the grid or its view is disposed (a handler closed the window): nothing may be touched then.
        private static bool IsGone(Control grid, GridView view)
            => grid.IsDisposed || grid.Disposing || view.GridControl == null;

        // The edit entry check shared by SetCell and Select: an editor is open, or the focused row is modified (a user's
        // or another caller's pending edit, which moving the focus would validate and commit).
        private static bool HasPendingEdit(GridView view)
            => view.IsEditing || view.FocusedRowModified || view.DataController.IsCurrentRowModified;

        // The position of the edited record after the commit (sorting and filtering can move it), or -1 when it left
        // the view. The record is found by its list-source index and verified by reference, never by Equals (items that
        // are equal by value must stay apart). Without a list-source index the record is looked up with FindRow and
        // the focused row is the last resort.
        private static int RowOf(GridView view, object record, int source, int row)
        {
            int after;
            if (source >= 0)
            {
                after = view.GetRowHandle(source);
                if (after >= 0 && after < view.DataRowCount && record != null && !ReferenceEquals(view.GetRow(after), record)) after = -1;
            }
            else
            {
                after = record == null ? view.FocusedRowHandle : view.FindRow(record);
            }
            return after >= 0 && after < view.DataRowCount ? after : -1;
        }

        private static string DisplayTextOrNull(GridView view, int row, GridColumn column)
            => row >= 0 && row < view.DataRowCount ? view.GetRowCellDisplayText(row, column) : null;

        // Writes the old value back when the data object kept the refused one (see SetCell); true when the cell holds
        // the old value afterwards. A record that is not in the view cannot be written to; it is only checked through
        // the list source.
        private static bool RestoreCell(GridView view, object record, int source, int row, GridColumn column, object oldValue)
        {
            int at = RowOf(view, record, source, row);
            if (at < 0) return source < 0 || Equals(view.GetListSourceRowCellValue(source, column), oldValue);
            if (Equals(view.GetRowCellValue(at, column), oldValue)) return true;
            view.SetRowCellValue(at, column, oldValue);
            view.CancelUpdateCurrentRow();
            at = RowOf(view, record, source, row);
            if (at < 0) return source < 0 || Equals(view.GetListSourceRowCellValue(source, column), oldValue);
            return Equals(view.GetRowCellValue(at, column), oldValue);
        }

        // true: the cell holds the intended value (after conversion to the column's type); false: it holds something
        // else; null: the intended value cannot be converted, the caller falls back to comparing texts.
        private static bool? HoldsValue(object intended, object actual, Type columnType)
        {
            if (intended is DBNull) intended = null;
            if (actual is DBNull) actual = null;
            // A cleared text cell is an empty string in the editor and may be null in the data.
            if (intended is string && ((string)intended).Length == 0 && actual == null) return true;
            if (intended == null || actual == null) return intended == null && actual == null;

            Type target = columnType == null ? null : (Nullable.GetUnderlyingType(columnType) ?? columnType);
            object converted;
            try
            {
                converted = target == null || target == typeof(object) || target.IsInstanceOfType(intended)
                    ? intended
                    : Convert.ChangeType(intended, target, CultureInfo.CurrentCulture);
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
            return Equals(converted, actual);
        }

        private static void VerifyRowInView(GridView view, int row)
        {
            if (view.IsRowVisible(row) == RowVisibleState.Hidden)
                throw new GridOperationException(GridOperationError.NotRealized, "The grid could not bring the row into view.");
        }

        private static GridView RealizedViewOrThrow(Control grid)
        {
            GridView view = SupportedViewOrNull(grid);
            if (view == null)
                throw new GridOperationException(GridOperationError.Unsupported, "The grid view is not supported.");
            if (!grid.IsHandleCreated)
                throw new GridOperationException(GridOperationError.NotRealized, "The grid has no window handle yet.");
            if (view.ViewRect.Width <= 0 || view.ViewRect.Height <= 0)
                throw new GridOperationException(GridOperationError.NotRealized, "The grid has no layout yet.");
            return view;
        }

        private static GridColumn VisibleColumnOrThrow(GridView view, int column)
        {
            if (column < 0 || column >= view.Columns.Count)
                throw new GridOperationException(GridOperationError.StaleView, "The column is outside the grid.");
            GridColumn target = view.Columns[column];
            if (!IsColumnVisible(target))
                throw new GridOperationException(GridOperationError.Hidden, "The column is hidden.");
            return target;
        }

        // What read_grid reports and what Select, Scroll and SetCell refuse: the column is shown. A banded column is
        // shown only inside a band, and only when every band up the chain is visible.
        private static bool IsColumnVisible(GridColumn column)
        {
            if (!column.Visible || column.VisibleIndex < 0) return false;
            var banded = column as BandedGridColumn;
            if (banded == null) return true;
            if (banded.OwnerBand == null) return false;
            for (GridBand band = banded.OwnerBand; band != null; band = band.ParentBand)
            {
                if (!band.Visible) return false;
            }
            return true;
        }

        private static GridColumn FirstVisibleColumnOrThrow(GridView view)
        {
            if (view.VisibleColumns.Count == 0)
                throw new GridOperationException(GridOperationError.Hidden, "The grid has no visible column.");
            return view.VisibleColumns[0];
        }

        private static void CheckRow(GridView view, int row)
        {
            if (row < 0 || row >= view.DataRowCount)
                throw new GridOperationException(GridOperationError.StaleView, "The row is outside the grid.");
        }

        /// <summary>
        /// The main view when it is a supported one, else null. Cheap: no handle is created and nothing is
        /// initialized. A band hierarchy and a column list are read as they are.
        /// </summary>
        private static GridView SupportedViewOrNull(Control grid)
        {
            var control = grid as GridControl;
            if (control == null) return null;
            // BandedGridView (and AdvBandedGridView) derive from GridView; card and layout views do not.
            var view = control.MainView as GridView;
            if (view == null) return null;
            if (view.IsDetailView || view.IsServerMode || view.GroupCount > 0) return null;
            if (control.LevelTree.Nodes.Count > 0) return null;
            return view;
        }

        private static bool IsViewEditable(GridView view) => view.OptionsBehavior.Editable && !view.OptionsBehavior.ReadOnly;

        private static GridValueKind KindOf(RepositoryItem edit, Type columnType)
        {
            // The most derived editors first: the time-span editor derives from the time editor.
            if (edit is RepositoryItemCheckEdit) return GridValueKind.Boolean;
            if (edit is RepositoryItemDateEdit) return GridValueKind.Date;
            if (edit is RepositoryItemTimeSpanEdit) return GridValueKind.Duration;
            if (edit is RepositoryItemTimeEdit) return GridValueKind.Time;
            if (edit is RepositoryItemSpinEdit) return GridValueKind.Number;
            if (edit is RepositoryItemComboBox || edit is RepositoryItemLookUpEdit) return GridValueKind.Choice;

            Type type = columnType == null ? null : (Nullable.GetUnderlyingType(columnType) ?? columnType);
            if (type == null) return GridValueKind.Text;
            if (type == typeof(TimeSpan)) return GridValueKind.Duration;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return GridValueKind.Date;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                    return GridValueKind.Boolean;
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return GridValueKind.Number;
                case TypeCode.String:
                case TypeCode.Char:
                case TypeCode.Object when type == typeof(object):
                    return GridValueKind.Text;
                default:
                    return GridValueKind.Other;
            }
        }
    }
}
#endif
