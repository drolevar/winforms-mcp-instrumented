using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace WinFormsMcp.Instrumented.Grids;

/// <summary>
/// The bridge's side of the grid seam: which adapter handles a control (asked once per control
/// instance), the per-grid freshness state and its token. Everything runs on the UI thread. Neither
/// table keeps a grid alive: the adapter cache and the state table are keyed weakly by the grid, and
/// the state and the <see cref="GridViewSignal"/> that points at it hold no reference to the grid.
/// Exceptions from <c>GetViewInfo</c> are not translated by the token members; the caller maps them with
/// <see cref="Translate"/>, as <see cref="ReadPage"/> does.
/// </summary>
internal sealed class GridHost
{
    private const int ResponseBudget = 256 * 1024;
    private const int MaxFailureMessage = 200;
    private const int MaxErrorText = 1024;
    private readonly GridAdapter[] adapters;
    private readonly Action<string, string> log;
    private readonly ConditionalWeakTable<Control, GridEntry> entries = new();

    internal GridHost(IEnumerable<GridAdapter> adapters, Action<string, string> log)
    {
        this.adapters = adapters.ToArray();
        this.log = log;
    }

    /// <summary>
    /// The adapter that handles <paramref name="control"/>, or null. <c>Accepts</c> is called once per control
    /// instance and the answer is cached; a throwing <c>Accepts</c> means "not accepted" (one diagnostic).
    /// The first time an adapter accepts a control its <c>Attach</c> is called, once.
    /// A non-null result only says the control is handled, not that the grid is usable: when <c>Attach</c> or
    /// <c>GetFeatures</c> failed the grid is handled but unusable. Code that reads or mutates a grid goes
    /// through <see cref="Resolve"/>, which makes that distinction.
    /// </summary>
    internal GridAdapter? AdapterFor(Control control) => EntryFor(control).Adapter;

    /// <summary>
    /// True when <paramref name="control"/> is a descendant of a grid an adapter handles and that grid has at least one
    /// redacted column: a hosted cell editor can hold the text of such a column, so the bridge never describes a
    /// descendant's text. Fails closed: when the adapter handles the grid but its view or a column cannot be read (or
    /// <c>Attach</c> failed), the grid counts as redacted. <paramref name="cache"/>, when given, holds the answer per grid
    /// for one walk. Cost: a lookup per ancestor; the adapter is asked only for a grid that has a descendant.
    /// </summary>
    internal bool IsInsideRedactedGrid(Control control, Dictionary<Control, bool>? cache)
    {
        if (adapters.Length == 0) return false;
        for (Control? ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (EntryFor(ancestor).Adapter == null) continue;
            bool redacted;
            if (cache == null || !cache.TryGetValue(ancestor, out redacted))
            {
                redacted = HasRedactedColumn(ancestor);
                if (cache != null) cache[ancestor] = redacted;
            }
            if (redacted) return true;
        }
        return false;
    }

    // Reads the columns directly: this must not refresh the freshness state, and it fails closed.
    private bool HasRedactedColumn(Control grid)
    {
        GridEntry entry = EntryFor(grid);
        string operation = nameof(GridAdapter.GetViewInfo);
        try
        {
            if (entry.AttachFailed) return true;
            GridViewInfo info = entry.Adapter!.GetViewInfo(grid);
            operation = nameof(GridAdapter.DescribeColumn);
            for (int column = 0; column < info.ColumnCount; column++)
            {
                var description = new GridColumnDescription();
                entry.Adapter.DescribeColumn(grid, column, description);
                if (description.Redacted) return true;
            }
            return false;
        }
        catch (Exception exception)
        {
            if (!entry.RedactionFailureLogged) Fail(entry.Adapter!, operation, exception);
            entry.RedactionFailureLogged = true;
            return true;
        }
    }

    /// <summary>
    /// The adapter to use for an operation on <paramref name="grid"/> that needs <paramref name="required"/>
    /// (<see cref="GridFeatures.None"/> for reading). Throws <c>unsupported_operation</c> when no adapter
    /// handles the control or the adapter lacks the feature, and <c>adapter_failed</c> when the grid is handled
    /// but <c>Attach</c> or <c>GetFeatures</c> failed for it.
    /// </summary>
    internal GridAdapter Resolve(Control grid, GridFeatures required)
    {
        GridEntry entry = EntryFor(grid);
        if (entry.Adapter == null)
            throw new BridgeFailure("unsupported_operation", "No grid adapter handles this control.");
        GridFeatures? features = Features(grid);
        if (features == null)
            throw new BridgeFailure("adapter_failed", "The grid adapter failed for this grid.");
        if ((features.Value & required) != required)
            throw new BridgeFailure("unsupported_operation", "The grid adapter does not support this operation on this grid.");
        return entry.Adapter;
    }

    /// <summary>
    /// The features the adapter reports for the grid now, or null when the adapter failed for this grid
    /// (<c>Attach</c> or <c>GetFeatures</c> threw) or does not handle the control. The caller tells these
    /// apart with <see cref="AdapterFor"/>: a handled grid with null features is an adapter failure.
    /// </summary>
    internal GridFeatures? Features(Control control)
    {
        GridEntry entry = EntryFor(control);
        if (entry.Adapter == null || entry.AttachFailed) return null;
        try
        {
            return entry.Adapter.GetFeatures(control);
        }
        catch (Exception exception)
        {
            // GetFeatures runs in every tree walk: one diagnostic per grid keeps the bounded ring useful.
            if (!entry.FeaturesFailureLogged) Fail(entry.Adapter, "GetFeatures", exception);
            entry.FeaturesFailureLogged = true;
            return null;
        }
    }

    /// <summary>Refreshes the freshness state from the view, then returns the grid's token.</summary>
    internal string CurrentToken(Control control)
    {
        Refresh(control);
        return EntryFor(control).State!.Token;
    }

    /// <summary>
    /// Throws <c>stale_view</c> when <paramref name="token"/> is not the grid's current token. Precondition: the
    /// caller has already refused a missing token as <c>invalid_argument</c>; a null token here counts as stale.
    /// The current token is deliberately not in the error.
    /// </summary>
    internal void CheckToken(Control control, string token)
    {
        Refresh(control);
        CompareToken(EntryFor(control).State!, token);
    }

    // The state has just been refreshed by the caller.
    private static void CompareToken(GridState state, string token)
    {
        if (!string.Equals(token, state.Token, StringComparison.Ordinal))
            throw new BridgeFailure("stale_view", "The grid view changed after it was read; read the grid again.",
                new JObject { ["reason"] = "view_changed" });
    }

    /// <summary>
    /// Marks a grid mutation (select, scroll, set_cell) as started on <paramref name="grid"/>, or throws <c>grid_busy</c>
    /// when one already is: for example one waiting inside a modal dialog. Other grids are independent. The caller
    /// pairs it with <see cref="EndMutation"/> in a <c>finally</c> and does not call that when this threw.
    /// </summary>
    internal void BeginMutation(Control grid)
    {
        GridState state = EntryFor(grid).BuiltInState();
        if (state.MutationInProgress)
            throw new BridgeFailure("grid_busy", "Another operation on this grid is still in progress; wait for it to finish.");
        state.MutationInProgress = true;
    }

    /// <summary>Clears the flag <see cref="BeginMutation"/> set. Safe for a grid that was disposed meanwhile.</summary>
    internal void EndMutation(Control grid) => EntryFor(grid).BuiltInState().MutationInProgress = false;

    /// <summary>
    /// <c>select</c> on a grid an adapter handles: focuses the cell, makes it the only selection and brings it into view.
    /// Checks, in order: the adapter has the <c>Select</c> feature (<c>unsupported_operation</c>); <c>viewToken</c> and
    /// <c>row</c> are present and <c>row</c> and the optional <c>column</c> are in range (<c>invalid_argument</c>); the
    /// token is current (<c>stale_view</c>); the column is visible (<c>hidden_control</c>); then the adapter's
    /// <c>Select</c>. Adapter failures become wire errors (see <see cref="Translate"/>).
    /// </summary>
    internal void Select(Control grid, JObject parameters)
    {
        GridAdapter adapter = Resolve(grid, GridFeatures.Select);
        string token = UiRuntime.RequiredString(parameters, "viewToken");
        string operation = nameof(GridAdapter.GetViewInfo);
        try
        {
            GridEntry entry = EntryFor(grid);
            GridViewInfo info = RefreshAdapter(entry, grid);
            int row = UiRuntime.ReadInt(parameters, "row", null, 0, info.RowCount - 1);
            int? column = UiRuntime.HasValue(parameters, "column")
                ? UiRuntime.ReadInt(parameters, "column", null, 0, info.ColumnCount - 1)
                : (int?)null;
            CompareToken(entry.State!, token);
            if (column.HasValue)
            {
                operation = nameof(GridAdapter.DescribeColumn);
                var description = new GridColumnDescription();
                adapter.DescribeColumn(grid, column.Value, description);
                if (!description.Visible)
                    throw new BridgeFailure("hidden_control", "The selected grid column is hidden.");
            }
            operation = nameof(GridAdapter.Select);
            adapter.Select(grid, row, column);
        }
        catch (Exception exception)
        {
            throw Translate(adapter, operation, exception);
        }
    }

    /// <summary>
    /// <c>scroll</c> on a grid an adapter handles: brings a row and/or a column into view without moving focus or
    /// selection. The adapter needs the <c>Scroll</c> feature (<c>unsupported_operation</c>); <c>row</c> or <c>column</c>
    /// is required and each must be in range (<c>invalid_argument</c>); <c>viewToken</c> is checked only when supplied
    /// (<c>stale_view</c>).
    /// </summary>
    internal void Scroll(Control grid, JObject parameters)
    {
        GridAdapter adapter = Resolve(grid, GridFeatures.Scroll);
        bool hasRow = UiRuntime.HasValue(parameters, "row");
        bool hasColumn = UiRuntime.HasValue(parameters, "column");
        if (!hasRow && !hasColumn)
            throw new BridgeFailure("invalid_argument", "Grid scrolling requires row or column.");
        string? token = UiRuntime.OptionalString(parameters, "viewToken");
        string operation = nameof(GridAdapter.GetViewInfo);
        try
        {
            GridEntry entry = EntryFor(grid);
            GridViewInfo info = RefreshAdapter(entry, grid);
            int? row = hasRow ? UiRuntime.ReadInt(parameters, "row", null, 0, info.RowCount - 1) : (int?)null;
            int? column = hasColumn ? UiRuntime.ReadInt(parameters, "column", null, 0, info.ColumnCount - 1) : (int?)null;
            if (token != null) CompareToken(entry.State!, token);
            operation = nameof(GridAdapter.Scroll);
            adapter.Scroll(grid, row, column);
        }
        catch (Exception exception)
        {
            throw Translate(adapter, operation, exception);
        }
    }

    /// <summary>
    /// <c>set_cell</c> on a grid an adapter handles: edits one cell through the adapter's editor and reports whether the
    /// edit was committed. Malformed arguments (<c>row</c>, <c>column</c>, <c>text</c>, <c>viewToken</c>, <c>expectedText</c>)
    /// are <c>invalid_argument</c> before the adapter is asked anything. Then, in order: the adapter has the <c>Edit</c>
    /// feature (<c>unsupported_operation</c>); <c>text</c> is at most <paramref name="maxTextLength"/> characters and
    /// <c>row</c> and <c>column</c> are in range (<c>invalid_argument</c>); the token is current (<c>stale_view</c>); the
    /// column is visible (<c>hidden_control</c>) and editable (<c>read_only</c>); <c>expectedText</c>, when given, equals the
    /// full current cell text (<c>stale_view</c> with reason <c>cell_changed</c>; refused as <c>invalid_argument</c> for a
    /// redacted column, whose text is never requested, and for a cell longer than <paramref name="maxTextLength"/>); then the
    /// adapter's <c>SetCell</c>. A refused edit is the failed receipt <c>edit_rejected</c>. The caller holds the grid-busy
    /// flag. The bridge never retries, never writes at data level and never converts the text; neither the edit text nor the
    /// cell text is logged, and no adapter message is relayed (see <see cref="Translate"/>).
    /// </summary>
    internal JObject SetCell(Control grid, JObject parameters, int maxTextLength)
    {
        UiRuntime.ReadInt(parameters, "row", null, int.MinValue, int.MaxValue);
        UiRuntime.ReadInt(parameters, "column", null, int.MinValue, int.MaxValue);
        string text = UiRuntime.RequiredString(parameters, "text", allowEmpty: true);
        string token = UiRuntime.RequiredString(parameters, "viewToken");
        string? expectedText = UiRuntime.OptionalString(parameters, "expectedText");
        GridAdapter adapter = Resolve(grid, GridFeatures.Edit);
        if (text.Length > maxTextLength)
            throw new BridgeFailure("invalid_argument", "text is longer than the configured text limit.");
        string operation = nameof(GridAdapter.GetViewInfo);
        try
        {
            GridEntry entry = EntryFor(grid);
            GridViewInfo info = RefreshAdapter(entry, grid);
            int row = UiRuntime.ReadInt(parameters, "row", null, 0, info.RowCount - 1);
            int column = UiRuntime.ReadInt(parameters, "column", null, 0, info.ColumnCount - 1);
            CompareToken(entry.State!, token);
            operation = nameof(GridAdapter.DescribeColumn);
            var description = new GridColumnDescription();
            adapter.DescribeColumn(grid, column, description);
            if (!description.Visible)
                throw new BridgeFailure("hidden_control", "The grid column is hidden.");
            if (!description.Editable)
                throw new BridgeFailure("read_only", "The grid column is read-only.");
            bool redacted = description.Redacted;
            if (expectedText != null)
            {
                if (redacted)
                    throw new BridgeFailure("invalid_argument", "expectedText cannot be used on a redacted column.",
                        new JObject { ["reason"] = "column_redacted" });
                operation = nameof(GridAdapter.GetCellText);
                string current = adapter.GetCellText(grid, row, column) ?? string.Empty;
                if (current.Length > maxTextLength)
                    throw new BridgeFailure("invalid_argument", "expectedText cannot be used on a cell whose text is longer than the text limit.",
                        new JObject { ["reason"] = "cell_truncated" });
                if (!string.Equals(expectedText, current, StringComparison.Ordinal))
                    throw new BridgeFailure("stale_view", "The cell text is not the expected text; read the grid again.",
                        new JObject { ["reason"] = "cell_changed" });
            }

            // The request checks the view live: the token now against the token at the moment the edit started.
            string atRequest = entry.State!.Token;
            var weakGrid = new WeakReference(grid);
            var request = new GridEditRequest(row, column, text, () => ViewChangedSince(entry, weakGrid, atRequest));
            operation = nameof(GridAdapter.SetCell);
            GridEditResult? returned = adapter.SetCell(grid, request);
            if (returned == null)
            {
                // Authored here, so it is told apart from an adapter's own exception in the diagnostics.
                string noResult = "Grid adapter '" + adapter.Name + "' returned no result from SetCell.";
                log("adapter_failed", noResult);
                throw new BridgeFailure("adapter_failed", noResult);
            }
            GridEditResult result = returned;
            string? displayText = redacted || result.DisplayText == null ? null : Cut(result.DisplayText, maxTextLength);
            if (!result.IsCommitted)
            {
                var details = new JObject
                {
                    ["reason"] = SnakeCase(result.Reason.ToString()),
                    // The application's text is untrusted and may quote the value; for a redacted column it is not relayed.
                    ["errorText"] = NullableString(redacted ? null : result.ErrorText == null ? null : Cut(result.ErrorText, MaxErrorText)),
                    ["displayText"] = NullableString(displayText)
                };
                if (redacted) details["displayTextRedacted"] = true;
                throw new BridgeFailure("edit_rejected", "The grid refused the edit.", details);
            }

            // The handler can dispose its own grid; a disposed grid has no view to read the token from.
            string? viewToken = null;
            if (!grid.IsDisposed)
            {
                try
                {
                    viewToken = CurrentToken(grid);
                }
                catch (Exception exception)
                {
                    // The edit is committed: it is not reported as failed. The agent reads the grid again.
                    Translate(adapter, nameof(GridAdapter.GetViewInfo), exception);
                }
            }
            var committed = new JObject
            {
                ["operation"] = "set_cell",
                ["handlerReturned"] = true,
                ["committed"] = true,
                ["displayText"] = NullableString(displayText),
                ["displayTextRedacted"] = redacted,
                ["rowAfter"] = result.RowAfter < 0 ? JValue.CreateNull() : new JValue(result.RowAfter),
                ["viewToken"] = NullableString(viewToken)
            };
            if (!redacted && result.DisplayText != null && result.DisplayText.Length > maxTextLength)
                committed["displayTextTruncated"] = true;
            return committed;
        }
        catch (Exception exception)
        {
            throw Translate(adapter, operation, exception);
        }
    }

    // The live check behind GridEditRequest.ViewChanged. A view that cannot be read counts as changed.
    private static bool ViewChangedSince(GridEntry entry, WeakReference weakGrid, string tokenAtRequest)
    {
        if (!(weakGrid.Target is Control grid) || grid.IsDisposed) return true;
        try
        {
            RefreshAdapter(entry, grid);
            return !string.Equals(entry.State!.Token, tokenAtRequest, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static JToken NullableString(string? text) => text == null ? JValue.CreateNull() : new JValue(text);

    private static string Cut(string text, int maximum) => text.Length > maximum ? text.Substring(0, maximum) : text;

    // "RowNotCommitted" -> "row_not_committed".
    private static string SnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder();
        for (int index = 0; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[index]));
        }
        return builder.ToString();
    }

    /// <summary>
    /// For the cases where the token is optional (DataGridView select and scroll, adapter scroll): no check at all
    /// when none was supplied, otherwise <see cref="CheckToken"/>.
    /// </summary>
    internal void CheckTokenIfSupplied(Control control, string? token)
    {
        if (token != null) CheckToken(control, token);
    }

    /// <summary>
    /// Compares the fingerprint the adapter reports now with the stored one and counts a difference as a change.
    /// Events that the adapter forwards through the signal have already counted theirs. A <see cref="DataGridView"/>
    /// that no adapter handles has a built-in, fingerprint-only token (no events are subscribed).
    /// </summary>
    internal void Refresh(Control control)
    {
        GridEntry entry = EntryFor(control);
        if (entry.Adapter == null)
        {
            if (!(control is DataGridView view))
                throw new BridgeFailure("unsupported_operation", "No grid adapter handles this control.");
            entry.BuiltInState().Observe(DataGridViewFingerprint(view));
            return;
        }
        RefreshAdapter(entry, control);
    }

    // Refreshes the state of a grid an adapter handles and returns the view the adapter reported.
    private static GridViewInfo RefreshAdapter(GridEntry entry, Control control)
    {
        // Attach failed, so the adapter's events were never wired to the signal: the token would see only
        // fingerprint changes and could pass a stale view.
        if (entry.AttachFailed)
            throw new BridgeFailure("adapter_failed", "The grid adapter failed to attach to this grid.");
        GridViewInfo info = entry.Adapter!.GetViewInfo(control);
        entry.State!.Observe(info.Fingerprint);
        return info;
    }

    // Row count, column count, data-source identity and sort state. Each field is folded in with an
    // xor-multiply step, so a change in any one field always changes the result.
    private static long DataGridViewFingerprint(DataGridView view)
    {
        DataGridViewColumn? sorted = view.SortedColumn;
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (int part in new[]
            {
                view.Rows.Count,
                view.Columns.Count,
                RuntimeHelpers.GetHashCode(view.DataSource),
                sorted == null ? -1 : sorted.Index,
                RuntimeHelpers.GetHashCode(sorted),
                (int)view.SortOrder
            })
                hash = (hash ^ (uint)part) * 1099511628211UL;
            return (long)hash;
        }
    }

    /// <summary>
    /// Builds the <c>read_grid</c> response for a grid an adapter handles: one page of rows and columns, clamped and
    /// bounded like the DataGridView read, plus the freshness token of the view the page is based on. The adapter is
    /// asked about the clamped page only. Failures of the adapter become wire errors (see <see cref="Translate"/>).
    /// </summary>
    internal JObject ReadPage(Control grid, JObject parameters, string generation, int maxTextLength)
    {
        GridAdapter adapter = Resolve(grid, GridFeatures.None);
        int rowCount = UiRuntime.ReadInt(parameters, "rowCount", 25, 1, 200);
        int columnCount = UiRuntime.ReadInt(parameters, "columnCount", 20, 1, 50);
        string operation = nameof(GridAdapter.GetViewInfo);
        try
        {
            GridEntry entry = EntryFor(grid);
            GridViewInfo info = RefreshAdapter(entry, grid);
            string token = entry.State!.Token;
            int startRow = UiRuntime.ReadInt(parameters, "startRow", 0, 0, info.RowCount);
            int startColumn = UiRuntime.ReadInt(parameters, "startColumn", 0, 0, info.ColumnCount);
            int endRow = startRow + Math.Min(rowCount, info.RowCount - startRow);
            int endColumn = startColumn + Math.Min(columnCount, info.ColumnCount - startColumn);

            int characters = 0;
            string Limit(string text) => text.Length > maxTextLength ? text.Substring(0, maxTextLength) : text;
            void Spend(int length, string failure)
            {
                characters += length;
                if (characters > ResponseBudget) throw new BridgeFailure("response_limit", failure);
            }

            var columns = new JArray();
            var redacted = new bool[endColumn - startColumn];
            for (int c = startColumn; c < endColumn; c++)
            {
                operation = nameof(GridAdapter.DescribeColumn);
                var description = new GridColumnDescription();
                adapter.DescribeColumn(grid, c, description);
                redacted[c - startColumn] = description.Redacted;
                const string headerLimit = "The grid headers contain too much text; reduce columnCount.";
                string name = Limit(description.Name ?? string.Empty);
                string header = Limit(description.Header ?? string.Empty);
                string? field = description.Field == null ? null : Limit(description.Field);
                Spend(name.Length + header.Length + (field?.Length ?? 0), headerLimit);
                JToken bandPath = JValue.CreateNull();
                if (description.Bands.Count > 0)
                {
                    var bands = new JArray();
                    foreach (string? band in description.Bands)
                    {
                        string? caption = band == null ? null : Limit(band);
                        Spend(caption?.Length ?? 0, headerLimit);
                        bands.Add(caption);
                    }
                    bandPath = bands;
                }
                var choices = new JArray();
                foreach (string choice in description.Choices)
                {
                    string limited = Limit(choice);
                    Spend(limited.Length, headerLimit);
                    choices.Add(limited);
                }
                columns.Add(new JObject
                {
                    ["index"] = c,
                    ["name"] = name,
                    ["header"] = header,
                    ["visible"] = description.Visible,
                    ["field"] = field,
                    ["editable"] = description.Editable,
                    ["valueKind"] = description.ValueKind.ToString().ToLowerInvariant(),
                    ["bandPath"] = bandPath,
                    ["redacted"] = description.Redacted,
                    ["choices"] = choices,
                    ["choicesTruncated"] = description.ChoicesTruncated
                });
            }

            var rows = new JArray();
            for (int r = startRow; r < endRow; r++)
            {
                var cells = new JArray();
                JArray? truncated = null;
                for (int c = startColumn; c < endColumn; c++)
                {
                    if (redacted[c - startColumn])
                    {
                        cells.Add(JValue.CreateNull());
                        continue;
                    }
                    operation = nameof(GridAdapter.GetCellText);
                    string text = adapter.GetCellText(grid, r, c) ?? string.Empty;
                    if (text.Length > maxTextLength)
                    {
                        text = text.Substring(0, maxTextLength);
                        (truncated ??= new JArray()).Add(c);
                    }
                    Spend(text.Length, "The grid range contains too much text; reduce rowCount or columnCount.");
                    cells.Add(text);
                }
                var row = new JObject { ["index"] = r, ["visible"] = true };
                operation = nameof(GridAdapter.IsRowSelected);
                bool? selected = adapter.IsRowSelected(grid, r);
                if (selected.HasValue) row["selected"] = selected.Value;
                row["cells"] = cells;
                if (truncated != null) row["truncated"] = truncated;
                rows.Add(row);
            }

            operation = nameof(GridAdapter.TryGetFocusedCell);
            bool focused = adapter.TryGetFocusedCell(grid, out int focusedRow, out int focusedColumn)
                && focusedRow >= 0 && focusedRow < info.RowCount && focusedColumn >= 0 && focusedColumn < info.ColumnCount;
            return new JObject
            {
                ["generation"] = generation,
                ["dataKind"] = adapter.Name,
                ["gridAdapter"] = adapter.Name,
                ["viewToken"] = token,
                ["totalRows"] = info.RowCount,
                ["totalRowsIncludingNewRow"] = info.RowCount,
                ["totalColumns"] = info.ColumnCount,
                ["startRow"] = startRow,
                ["startColumn"] = startColumn,
                ["focusedRow"] = focused ? new JValue(focusedRow) : JValue.CreateNull(),
                ["focusedColumn"] = focused ? new JValue(focusedColumn) : JValue.CreateNull(),
                ["columns"] = columns,
                ["rows"] = rows,
                ["hasMoreRows"] = endRow < info.RowCount,
                ["hasMoreColumns"] = endColumn < info.ColumnCount
            };
        }
        catch (Exception exception)
        {
            throw Translate(adapter, operation, exception);
        }
    }

    /// <summary>
    /// Maps an exception from an adapter member to the wire error: our own <see cref="BridgeFailure"/> passes through, a
    /// <see cref="GridOperationException"/> maps to its code, anything else is <c>adapter_failed</c> (and logged once) with
    /// the adapter's name, the member, the exception type and a bounded message. The message of an exception from a member that
    /// handles cell data (see <see cref="HandlesData"/>) is left out, for a refusal as well: a data-bound adapter may quote a
    /// row, cell or edit value in it, and neither the wire nor the diagnostics may carry one.
    /// </summary>
    internal BridgeFailure Translate(GridAdapter adapter, string operation, Exception exception)
    {
        if (exception is BridgeFailure failure) return failure;
        string detail = HandlesData(operation) ? string.Empty : ": " + Bounded(exception.Message);
        if (exception is GridOperationException refusal)
        {
            string message = "The grid adapter refused " + operation + detail;
            switch (refusal.Error)
            {
                case GridOperationError.ReadOnly: return new BridgeFailure("read_only", message);
                case GridOperationError.Hidden: return new BridgeFailure("hidden_control", message);
                case GridOperationError.NotRealized: return new BridgeFailure("not_realized", message);
                case GridOperationError.StaleView:
                    return new BridgeFailure("stale_view", message, new JObject { ["reason"] = "view_changed" });
                default: return new BridgeFailure("unsupported_operation", message);
            }
        }
        string text = "Grid adapter '" + adapter.Name + "' failed in " + operation + ": " + exception.GetType().Name + detail;
        log("adapter_failed", text);
        return new BridgeFailure("adapter_failed", text);
    }

    // The members that read or write cell, row or edit data. SetCell joins them with the edit task.
    private static bool HandlesData(string operation)
        => operation == nameof(GridAdapter.GetCellText) || operation == nameof(GridAdapter.IsRowSelected) ||
           operation == nameof(GridAdapter.TryGetFocusedCell) || operation == nameof(GridAdapter.Select) ||
           operation == nameof(GridAdapter.Scroll) || operation == nameof(GridAdapter.SetCell);

    private static string Bounded(string? message)
    {
        string text = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > MaxFailureMessage ? text.Substring(0, MaxFailureMessage) : text;
    }

    private GridEntry EntryFor(Control control)
    {
        if (entries.TryGetValue(control, out GridEntry? cached)) return cached;
        // Cached before Accepts and Attach: an adapter that reaches back into the bridge for the same control
        // (a nested request while it pumps messages) gets this entry, "not handled yet", instead of recursing.
        var entry = new GridEntry();
        entries.Add(control, entry);
        foreach (GridAdapter adapter in adapters)
        {
            if (!TryAccepts(adapter, control)) continue;
            entry.Accept(adapter);
            break;
        }
        if (entry.Adapter != null)
        {
            try
            {
                entry.Adapter.Attach(control, new GridViewSignal(entry.State!));
            }
            catch (Exception exception)
            {
                entry.AttachFailed = true;
                Fail(entry.Adapter, "Attach", exception);
            }
        }
        return entry;
    }

    private bool TryAccepts(GridAdapter adapter, Control control)
    {
        try
        {
            return adapter.Accepts(control);
        }
        catch (Exception exception)
        {
            Fail(adapter, "Accepts", exception);
            return false;
        }
    }

    private void Fail(GridAdapter adapter, string operation, Exception exception)
        => log("adapter_failed", "Grid adapter '" + adapter.Name + "' failed in " + operation + ": " +
            exception.GetType().Name + ": " + Bounded(exception.Message));

    // The cached answer for one control instance. It holds the adapter and the state, never the control.
    private sealed class GridEntry
    {
        internal GridAdapter? Adapter { get; private set; }
        internal GridState? State { get; private set; }
        internal bool AttachFailed { get; set; }
        internal bool FeaturesFailureLogged { get; set; }
        internal bool RedactionFailureLogged { get; set; }

        internal void Accept(GridAdapter adapter)
        {
            Adapter = adapter;
            State = new GridState();
        }

        // The state of a DataGridView that no adapter handles, created when its token is first needed.
        internal GridState BuiltInState() => State ??= new GridState();
    }
}
