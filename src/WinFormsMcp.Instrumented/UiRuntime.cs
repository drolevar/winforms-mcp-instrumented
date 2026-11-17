using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Elements;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Instrumented;

internal sealed class UiRuntime : IDisposable
{
    private const int MaxObservationJsonCharacters = 2 * 1024 * 1024;
    private const int MaxElementChildren = 1_000;
    internal static readonly string[] Methods =
    {
        "hello", "windows", "tree", "inspect", "invoke", "set_text", "set_value",
        "select", "set_checked", "scroll", "ensure_visible", "set_cell", "read_grid", "capture",
        "action_status", "diagnostics"
    };

    private static readonly HashSet<string> Mutations = new(StringComparer.Ordinal)
    {
        "invoke", "set_text", "set_value", "select", "set_checked", "scroll", "ensure_visible", "set_cell"
    };

    private readonly string generation;
    private readonly McpBridgeBuilder options;
    private readonly ElementAdapter[] adapters;
    private readonly GridAdapter[] gridAdapters;
    private readonly GridHost grids;
    private readonly HashSet<string> failedActionPredicates = new(StringComparer.Ordinal);
    private readonly Action<string, string> log;
    private readonly Control dispatcher;
    private readonly ConditionalWeakTable<object, ElementIdentity> identities = new();
    private readonly Dictionary<string, ElementEntry> elements = new(StringComparer.Ordinal);
    private readonly object actionsLock = new();
    private readonly Dictionary<string, ActionReceipt> actions = new(StringComparer.Ordinal);
    private readonly int owningThreadId;
    private volatile bool disposed;

    internal UiRuntime(string generation, McpBridgeBuilder options, Action<string, string> log)
    {
        this.generation = generation;
        this.options = options;
        // Application adapters are consulted in registration order; the built-in ToolStrip adapter is always last.
        adapters = options.ElementAdapters.Concat(new ElementAdapter[] { new ToolStripAdapter() }).ToArray();
        gridAdapters = options.GridAdapters.ToArray();
        grids = new GridHost(gridAdapters, log);
        this.log = log;
        owningThreadId = Thread.CurrentThread.ManagedThreadId;
        // This private dispatcher deliberately owns a handle before Application.Run starts.
        // Inspected application controls are never forced to create their handles.
        dispatcher = new Control();
        dispatcher.CreateControl();
        _ = dispatcher.Handle;
    }

    /// <summary>The adapters frozen at construction, in consultation order.</summary>
    internal IEnumerable<string> AdapterNames => adapters.Select(adapter => adapter.Name);

    /// <summary>The grid adapters frozen at construction, in consultation order, then the built-in handling.</summary>
    internal IEnumerable<string> GridAdapterNames => gridAdapters.Select(adapter => adapter.Name).Concat(new[] { GridAdapter.DataGridViewName });

    internal Task<JToken> ExecuteAsync(string method, JObject parameters, CancellationToken cancellationToken)
    {
        if (method == "action_status") return Task.FromResult<JToken>(ReadAction(parameters));
        if (Mutations.Contains(method)) return Task.FromResult<JToken>(QueueAction(method, parameters));
        switch (method)
        {
            case "windows": return OnUiAsync(() => Windows(), cancellationToken);
            case "tree": return OnUiAsync(() => Tree(parameters), cancellationToken);
            case "inspect": return OnUiAsync(() => Inspect(parameters), cancellationToken);
            case "read_grid": return OnUiAsync(() => ReadGrid(parameters), cancellationToken);
            case "capture": return OnUiAsync(() => Capture(parameters), cancellationToken);
            default: throw new BridgeFailure("unsupported_method", "The requested bridge method is not supported.");
        }
    }

    private async Task<JToken> OnUiAsync(Func<JToken> read, CancellationToken cancellationToken)
    {
        if (disposed) throw new BridgeFailure("bridge_stopped", "The bridge has stopped.");
        var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.UiRequestTimeout);
        CancellationToken deadline = timeout.Token;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (deadline.IsCancellationRequested) return;
                try { completion.TrySetResult(read()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }));
        }
        catch (InvalidOperationException)
        {
            throw new BridgeFailure("ui_unavailable", "The registered UI dispatcher is unavailable.");
        }
        Task elapsed = Task.Delay(Timeout.Infinite, deadline);
        Task finished = await Task.WhenAny(completion.Task, elapsed).ConfigureAwait(false);
        if (finished == completion.Task) return await completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeFailure("ui_timeout", "The registered UI thread did not complete the read before its deadline.");
    }

    private JObject QueueAction(string method, JObject parameters)
    {
        if (disposed) throw new BridgeFailure("bridge_stopped", "The bridge has stopped.");
        var receipt = new ActionReceipt(Guid.NewGuid().ToString("N"));
        lock (actionsLock)
        {
            if (actions.Values.Count(a => a.State == "queued" || a.State == "started") >= 32)
                throw new BridgeFailure("action_limit", "Too many UI operations are still pending.");
            if (actions.Count >= 256)
            {
                ActionReceipt? oldest = actions.Values
                    .Where(a => a.State == "completed" || a.State == "failed")
                    .OrderBy(a => a.CreatedUtc).FirstOrDefault();
                if (oldest == null)
                    throw new BridgeFailure("action_limit", "Too many UI operations are still pending.");
                actions.Remove(oldest.Id);
            }
            actions.Add(receipt.Id, receipt);
        }
        JObject copied = (JObject)parameters.DeepClone();
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed)
                {
                    lock (actionsLock)
                    {
                        receipt.State = "failed";
                        receipt.CompletedUtc = DateTimeOffset.UtcNow;
                        receipt.Error = new JObject { ["code"] = "bridge_stopped", ["message"] = "The bridge stopped before the action started." };
                    }
                    return;
                }
                lock (actionsLock)
                {
                    receipt.State = "started";
                    receipt.StartedUtc = DateTimeOffset.UtcNow;
                }
                try
                {
                    JToken result = Mutate(method, copied);
                    lock (actionsLock)
                    {
                        receipt.Result = result;
                        receipt.State = "completed";
                        receipt.CompletedUtc = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception exception)
                {
                    var error = BridgeFailure.Describe(exception);
                    var failure = new JObject { ["code"] = error.Code, ["message"] = error.Message };
                    if (error.Details != null) failure["details"] = error.Details.DeepClone();
                    lock (actionsLock)
                    {
                        receipt.Error = failure;
                        receipt.State = "failed";
                        receipt.CompletedUtc = DateTimeOffset.UtcNow;
                    }
                    log("action_failed", exception.Message);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            lock (actionsLock) actions.Remove(receipt.Id);
            throw new BridgeFailure("ui_unavailable", "The registered UI dispatcher is unavailable.");
        }
        return new JObject { ["generation"] = generation, ["actionId"] = receipt.Id, ["state"] = "queued" };
    }

    private JObject ReadAction(JObject parameters)
    {
        if (RequiredString(parameters, "generation") != generation)
            throw new BridgeFailure("stale_generation", "The action belongs to a different application generation.");
        string id = RequiredString(parameters, "actionId");
        lock (actionsLock)
        {
            if (!actions.TryGetValue(id, out ActionReceipt? action))
                throw new BridgeFailure("unknown_action", "The action was not found or its completed receipt has expired.");
            return new JObject
            {
                ["generation"] = generation,
                ["actionId"] = action.Id,
                ["state"] = action.State,
                ["createdUtc"] = action.CreatedUtc.ToString("O"),
                ["startedUtc"] = action.StartedUtc?.ToString("O"),
                ["completedUtc"] = action.CompletedUtc?.ToString("O"),
                ["result"] = action.Result?.DeepClone(),
                ["error"] = action.Error?.DeepClone()
            };
        }
    }

    private List<Form> GetWindows(out int skippedOtherUiThreads)
    {
        AssertUiThread();
        skippedOtherUiThreads = 0;
        var result = new List<Form>();
        // OpenForms is global; never inspect properties of a foreign-thread form.
        // A form without a handle is also skipped because InvokeRequired can be ambiguous.
        Form[] snapshot;
        try { snapshot = Application.OpenForms.Cast<Form>().ToArray(); }
        catch (InvalidOperationException)
        {
            throw new BridgeFailure("ui_changed", "The open-window collection changed during observation; retry the read.");
        }
        foreach (Form form in snapshot)
        {
            if (!form.IsHandleCreated || form.InvokeRequired)
            {
                skippedOtherUiThreads++;
                continue;
            }
            if (!form.IsDisposed && !form.Disposing && !options.IsExcluded(form)) result.Add(form);
        }
        PruneElements();
        return result;
    }

    private JObject Windows()
    {
        var windows = GetWindows(out int skipped);
        var nodes = new JArray();
        int remaining = MaxObservationJsonCharacters;
        bool truncated = false;
        foreach (Form window in windows)
        {
            JObject node = Describe(window, null);
            remaining -= node.ToString(Newtonsoft.Json.Formatting.None).Length;
            if (nodes.Count >= options.MaxNodes || remaining < 0) { truncated = true; break; }
            nodes.Add(node);
        }
        return new JObject
        {
            ["generation"] = generation,
            ["windows"] = nodes,
            ["truncated"] = truncated,
            ["skippedOtherUiThreads"] = skipped
        };
    }

    private JObject Tree(JObject parameters, bool includeElements = true)
    {
        int maxNodes = ReadInt(parameters, "maxNodes", options.MaxNodes, 1, options.MaxNodes);
        int maxDepth = ReadInt(parameters, "maxDepth", options.MaxDepth, 0, options.MaxDepth);
        List<Form> windows = GetWindows(out int skipped);
        object? target = HasValue(parameters, "target") || HasValue(parameters, "selector")
            ? parameters["target"] is JObject ? ResolveAny(parameters) : Resolve(parameters)
            : null;
        var nodes = new JArray();
        bool truncated = false;
        int remaining = MaxObservationJsonCharacters;
        ElementWalk? walk = includeElements ? new ElementWalk() : null;
        if (target != null && IsElement(target) && walk != null)
        {
            // A tree rooted at an element expands its whole subtree, closed dropdowns included.
            walk.ExpandClosed = true;
            WalkElementRoot(target, maxDepth, maxNodes, nodes, ref truncated, ref remaining, walk);
        }
        else
        {
            IEnumerable<Control> roots = target is Control rootControl ? new[] { rootControl } : windows.Cast<Control>();
            var gridRedaction = new Dictionary<Control, bool>();
            foreach (Control root in roots) Walk(root, null, 0, maxDepth, maxNodes, nodes, ref truncated, ref remaining, walk, gridRedaction);
        }
        return new JObject
        {
            ["generation"] = generation,
            ["nodes"] = nodes,
            ["nodeCount"] = nodes.Count,
            ["truncated"] = truncated,
            ["skippedOtherUiThreads"] = skipped
        };
    }

    private void Walk(Control control, string? parentId, int depth, int maxDepth, int maxNodes,
        JArray nodes, ref bool truncated, ref int remainingCharacters, ElementWalk? walk,
        Dictionary<Control, bool> gridRedaction)
    {
        if (control.IsDisposed || control.Disposing || options.IsExcluded(control)) return;
        if (nodes.Count >= maxNodes || remainingCharacters <= 0) { truncated = true; return; }
        JObject node = Describe(control, parentId, gridRedaction);
        remainingCharacters -= node.ToString(Newtonsoft.Json.Formatting.None).Length;
        if (remainingCharacters < 0) { truncated = true; return; }
        nodes.Add(node);
        if (depth >= maxDepth)
        {
            if (control.Controls.Count != 0) truncated = true;
        }
        else
        {
            foreach (Control child in control.Controls)
                Walk(child, (string?)node["id"], depth + 1, maxDepth, maxNodes, nodes, ref truncated, ref remainingCharacters, walk, gridRedaction);
        }
        if (walk != null)
            WalkElementChildren(control, null, node, depth, maxDepth, maxNodes, nodes, ref truncated, ref remainingCharacters, walk);
    }

    // Emits the elements that adapters report under a control (every adapter, in registration order)
    // or under an element (only the adapter that emitted it). A parent already at the depth limit gets
    // a zero budget, which emits nothing but still reports whether anything was cut off.
    private void WalkElementChildren(object parent, ElementAdapter? owner, JObject parentNode, int depth, int maxDepth,
        int maxNodes, JArray nodes, ref bool truncated, ref int remainingCharacters, ElementWalk walk)
    {
        string parentId = (string)parentNode["id"]!;
        int budget = depth >= maxDepth ? 0 : Math.Min(MaxElementChildren, maxNodes - nodes.Count);
        var found = new List<KeyValuePair<ElementAdapter, object>>();
        foreach (ElementAdapter adapter in owner != null ? new[] { owner } : adapters)
        {
            if (!CollectElements(adapter, "GetChildren", sink => adapter.GetChildren(parent, sink), ref budget, found, ref truncated))
                parentNode["childrenError"] = "adapter_failed";
            if (parent is Form window &&
                !CollectElements(adapter, "GetWindowRoots", sink => adapter.GetWindowRoots(window, sink), ref budget, found, ref truncated))
                parentNode["childrenError"] = "adapter_failed";
        }
        foreach (KeyValuePair<ElementAdapter, object> pair in found)
        {
            if (walk.EmittedUnder.TryGetValue(pair.Value, out string? emittedUnder))
            {
                // Already emitted in this response: a duplicate or a cycle. A placement that shares its
                // child collection with an earlier placement points to that placement instead.
                if (owner != null && emittedUnder != parentId && !ReferenceEquals(pair.Value, parent)
                    && parentNode["childrenOf"]!.Type == JTokenType.Null)
                    parentNode["childrenOf"] = emittedUnder;
                continue;
            }
            WalkElement(pair.Key, pair.Value, parent, parentId, depth + 1, maxDepth, maxNodes, nodes,
                ref truncated, ref remainingCharacters, walk);
        }
    }

    // The root of an element-rooted tree: the element itself at depth 0, hung under its real parent so an
    // agent can navigate upward. The caller has already resolved the element, so its entry and owner exist.
    private void WalkElementRoot(object element, int maxDepth, int maxNodes, JArray nodes,
        ref bool truncated, ref int remainingCharacters, ElementWalk walk)
    {
        ElementEntry entry = FindEntry(element)!;
        object? parent = ParentOf(entry, element);
        WalkElement(entry.Owner!, element, parent, parent == null ? null : ParentId(parent), 0, maxDepth, maxNodes, nodes,
            ref truncated, ref remainingCharacters, walk);
    }

    private bool CollectElements(ElementAdapter adapter, string operation, Action<ElementSink> call, ref int budget,
        List<KeyValuePair<ElementAdapter, object>> found, ref bool truncated)
    {
        var sink = new ElementSink(budget);
        try { call(sink); }
        catch (Exception exception)
        {
            log("adapter_failed", "Element adapter '" + adapter.Name + "' failed in " + operation + ": " +
                exception.GetType().Name + ": " + exception.Message);
            return false;
        }
        foreach (object element in sink.Items)
        {
            // Controls the bridge walks itself (a form, or any control with a parent) share the control identity
            // table; an element with such a control's identity would duplicate its id and let the adapter own
            // (and prune) a live control. An unparented control (a context menu) is never walked and may be an element.
            if (element is Form || element is Control { Parent: not null })
            {
                log("adapter_failed", "Element adapter '" + adapter.Name + "' reported a control as an element in " +
                    operation + "; it was dropped.");
                continue;
            }
            // An excluded control is hidden like any other excluded control; this is not an adapter failure.
            if (element is Control excluded && IsExcludedFailClosed(excluded)) continue;
            found.Add(new KeyValuePair<ElementAdapter, object>(adapter, element));
        }
        budget -= sink.Items.Count;
        if (sink.Overflowed) truncated = true;
        return true;
    }

    private void WalkElement(ElementAdapter adapter, object element, object? parent, string? parentId, int depth,
        int maxDepth, int maxNodes, JArray nodes, ref bool truncated, ref int remainingCharacters, ElementWalk walk)
    {
        if (nodes.Count >= maxNodes || remainingCharacters <= 0) { truncated = true; return; }
        JObject node = DescribeElement(adapter, element, parentId, parent, out ElementDescription? description);
        remainingCharacters -= node.ToString(Newtonsoft.Json.Formatting.None).Length;
        if (remainingCharacters < 0) { truncated = true; return; }
        nodes.Add(node);
        walk.EmittedUnder[element] = parentId;
        if (description == null) return;
        if (!walk.ExpandClosed && description.Opened == false && description.ChildCount > 0)
        {
            node["childrenOmitted"] = true;
            return;
        }
        WalkElementChildren(element, adapter, node, depth, maxDepth, maxNodes, nodes, ref truncated, ref remainingCharacters, walk);
    }

    private object ResolveAny(JObject parameters)
    {
        AssertUiThread();
        if (parameters["target"] is JObject target)
        {
            if (RequiredString(target, "generation") != generation)
                throw new BridgeFailure("stale_generation", "The target belongs to a different application generation.");
            string id = RequiredString(target, "id");
            if (!elements.TryGetValue(id, out ElementEntry? entry) || !(entry.Target.Target is object live))
                throw new BridgeFailure("stale_target", "The observed control no longer exists.");
            if (entry.Owner == null && live is Control control)
            {
                if (!IsInScope(control))
                    throw new BridgeFailure("stale_target", "The observed control is disposed, detached, or outside the instrumented scope.");
            }
            else if (!IsElementInScope(entry, live))
                throw new BridgeFailure("stale_target", "The observed element is gone, detached, or outside the instrumented scope.");
            return live;
        }
        throw new BridgeFailure("invalid_argument", "Supply target { generation, id } or selector { name, type? }.");
    }

    private Control Resolve(JObject parameters)
    {
        AssertUiThread();
        if (parameters["target"] is JObject)
        {
            // The stale check runs first, so a removed element is stale_target, not unsupported.
            object resolved = ResolveAny(parameters);
            if (!IsElement(resolved) && resolved is Control control) return control;
            throw new BridgeFailure("unsupported_operation", "This operation applies to controls, not elements.");
        }
        if (parameters["selector"] is JObject selector)
        {
            string name = RequiredString(selector, "name");
            string? type = OptionalString(selector, "type");
            JObject tree = Tree(new JObject(), includeElements: false);
            if ((bool)tree["truncated"]!)
                throw new BridgeFailure("scope_limit", "The control tree was truncated; use a concrete target or increase configured limits.");
            var matches = ((JArray)tree["nodes"]!).OfType<JObject>()
                .Where(n => elements[(string)n["id"]!].Target.Target is Control candidate && candidate.Name == name
                    && (type == null || candidate.GetType().FullName == type || candidate.GetType().Name == type))
                .ToList();
            if (matches.Count == 0) throw new BridgeFailure("target_not_found", "No current control matches the selector.");
            if (matches.Count != 1) throw new BridgeFailure("ambiguous_target", "More than one current control matches the selector.");
            string id = (string)matches[0]["id"]!;
            if (elements[id].Target.Target is Control match && IsInScope(match))
            {
                if (IsElement(match)) throw new BridgeFailure("unsupported_operation", "This operation applies to controls, not elements.");
                return match;
            }
            throw new BridgeFailure("stale_target", "The matching control changed during selection.");
        }
        throw new BridgeFailure("invalid_argument", "Supply target { generation, id } or selector { name, type? }.");
    }

    private bool IsInScope(Control control)
    {
        if (control.IsDisposed || control.Disposing || control.InvokeRequired) return false;
        Control? cursor = control;
        int depth = 0;
        while (cursor != null)
        {
            if (++depth > 256 || cursor.IsDisposed || options.IsExcluded(cursor)) return false;
            if (cursor is Form form)
                return GetWindows(out _).Any(window => ReferenceEquals(window, form));
            cursor = cursor.Parent;
        }
        return false;
    }

    // Doc 09 "Stale detection": the element's adapter says it is alive, its parent still lists it, and the
    // chain of parents ends in a control that is in scope. Each adapter call is isolated: a failure counts
    // as "not listed" (the target is reported stale) rather than as an operation failure.
    private bool IsElementInScope(ElementEntry entry, object element, int depth = 0)
    {
        if (depth > 256 || entry.Owner == null || IsDeadElement(entry, failClosed: true)) return false;
        // An excluded control that an adapter emitted (a context menu) is out of scope, and so is everything under it.
        if (element is Control self && IsExcludedFailClosed(self)) return false;
        object? parent = ParentOf(entry, element);
        if (parent == null) return false;
        ElementEntry? parentEntry = FindEntry(parent);
        // An entry with an owner is an element even when its object is a control (a context menu has no parent).
        bool controlParent = parent is Control && (parentEntry == null || parentEntry.Owner == null);
        // Adapters are not called for an excluded, disposed or foreign-thread control.
        if (controlParent && !IsInScope((Control)parent)) return false;
        if (!ListsElement(entry.Owner, parent, element)) return false;
        if (controlParent) return true;
        return parentEntry != null && IsElementInScope(parentEntry, parent, depth + 1);
    }

    // A predicate written for parented controls can throw on an unparented one (a context menu);
    // such a control is treated as excluded rather than failing the request.
    private bool IsExcludedFailClosed(Control control)
    {
        try { return options.IsExcluded(control); }
        catch (Exception) { return true; }
    }

    private object? ParentOf(ElementEntry entry, object element)
    {
        try
        {
            object? parent = entry.Owner?.GetParent(element);
            if (parent != null) return parent;
        }
        catch (Exception exception)
        {
            log("adapter_failed", "Element adapter '" + entry.Owner!.Name + "' failed in GetParent: " +
                exception.GetType().Name + ": " + exception.Message);
        }
        return entry.ObservedParent?.Target;
    }

    private bool ListsElement(ElementAdapter adapter, object parent, object element)
    {
        var sink = new ElementSink(options.MaxNodes, element);
        try
        {
            adapter.GetChildren(parent, sink);
            if (!sink.Found && parent is Form window) adapter.GetWindowRoots(window, sink);
        }
        catch (Exception exception)
        {
            log("adapter_failed", "Element adapter '" + adapter.Name + "' failed in GetChildren: " +
                exception.GetType().Name + ": " + exception.Message);
            return false;
        }
        return sink.Found;
    }

    // Targets are classified by registry ownership, not by type: an owned entry is an element even if it is a Control.
    private bool IsElement(object target) => FindEntry(target)?.Owner != null;

    private ElementEntry? FindEntry(object element)
        => identities.TryGetValue(element, out ElementIdentity? identity) && elements.TryGetValue(identity.Id, out ElementEntry? entry)
            ? entry
            : null;

    private string? ParentId(object parent)
        => parent is Control control ? Identity(control)
            : identities.TryGetValue(parent, out ElementIdentity? identity) ? identity.Id : null;

    private string Identity(object element, ElementAdapter? owner = null, object? parent = null)
    {
        ElementIdentity identity = identities.GetValue(element, _ => new ElementIdentity(Guid.NewGuid().ToString("N")));
        if (!elements.TryGetValue(identity.Id, out ElementEntry? entry))
            elements[identity.Id] = entry = new ElementEntry(element);
        entry.Target = new WeakReference(element);
        if (owner != null) entry.Owner = owner;
        if (parent != null) entry.ObservedParent = new WeakReference(parent);
        return identity.Id;
    }

    private void PruneElements()
    {
        foreach (string id in elements.Where(pair => !pair.Value.Target.IsAlive ||
                     pair.Value.Target.Target is Control control && control.IsDisposed ||
                     IsDeadElement(pair.Value)).Select(pair => pair.Key).ToArray())
            elements.Remove(id);
    }

    // A throwing IsAlive counts as alive for pruning (an entry is never dropped on an adapter failure)
    // and as dead for the stale check (the element is not trusted).
    private bool IsDeadElement(ElementEntry entry, bool failClosed = false)
    {
        if (entry.Owner == null || !(entry.Target.Target is object element)) return false;
        try { return !entry.Owner.IsAlive(element); }
        catch (Exception exception)
        {
            log("adapter_failed", "Element adapter '" + entry.Owner.Name + "' failed in IsAlive: " +
                exception.GetType().Name + ": " + exception.Message);
            return failClosed;
        }
    }

    // Once the bridge walks a control it is a control: an adapter that emitted it while it had no parent
    // no longer owns it.
    private string ControlIdentity(Control control)
    {
        string id = Identity(control);
        ElementEntry entry = elements[id];
        entry.Owner = null;
        entry.ObservedParent = null;
        return id;
    }

    // gridRedaction caches, for one walk, whether a grid an adapter handles has a redacted column.
    private JObject Describe(Control control, string? parentId, Dictionary<Control, bool>? gridRedaction = null)
    {
        // A password box hides its text; so does any control inside a grid with a redacted column (a hosted cell editor
        // can hold that column's text, and keeps it after it is hidden), together with its value, selection and check state.
        bool redacted = IsPassword(control);
        bool inRedactedGrid = grids.IsInsideRedactedGrid(control, gridRedaction);
        redacted |= inRedactedGrid;
        string? text = redacted ? null : control.Text;
        var capabilities = new JArray("inspect", "capture", "focus", "ensure_visible");
        if (control is Button) capabilities.Add("click");
        if (control is TextBoxBase || control is ComboBox combo && combo.DropDownStyle != ComboBoxStyle.DropDownList)
            capabilities.Add("set_text");
        if (control is CheckBox || control is RadioButton) capabilities.Add("set_checked");
        if (control is NumericUpDown || control is TrackBar || control is DateTimePicker) capabilities.Add("set_value");
        // An application grid adapter handles a control before the built-in handling does, for select and scroll too
        // (Mutate routes them to the adapter), so its features alone decide what is advertised for such a control.
        GridAdapter? gridAdapter = grids.AdapterFor(control);
        bool builtInGrid = gridAdapter == null && control is DataGridView;
        if (gridAdapter == null &&
            (control is ListBox || control is ComboBox || control is ListView || control is TabControl || control is TreeView || builtInGrid))
            capabilities.Add("select");
        if (gridAdapter == null &&
            (control is ScrollableControl scroll && scroll.AutoScroll || builtInGrid || control is ListBox || control is ListView))
            capabilities.Add("scroll");
        if (builtInGrid) capabilities.Add("read_grid");
        string? gridName = builtInGrid ? GridAdapter.DataGridViewName : null;
        string? gridError = null;
        if (gridAdapter != null)
        {
            GridFeatures? features = grids.Features(control);
            if (features == null) gridError = "adapter_failed";
            else
            {
                gridName = gridAdapter.Name;
                AddUnique(capabilities, "read_grid");
                if (features.Value.HasFlag(GridFeatures.Select)) AddUnique(capabilities, "select");
                if (features.Value.HasFlag(GridFeatures.Scroll)) AddUnique(capabilities, "scroll");
                if (features.Value.HasFlag(GridFeatures.Edit)) AddUnique(capabilities, "set_cell");
            }
        }
        foreach (var action in options.Actions)
            if (AcceptsAction(action.Key, action.Value, control)) capabilities.Add(action.Key);

        var result = new JObject
        {
            ["kind"] = "control",
            ["id"] = ControlIdentity(control),
            ["generation"] = generation,
            ["parentId"] = parentId,
            ["name"] = LimitText(control.Name),
            ["nameTruncated"] = control.Name.Length > options.MaxTextLength,
            ["type"] = control.GetType().FullName,
            ["shortType"] = control.GetType().Name,
            ["text"] = text == null ? null : LimitText(text),
            ["textRedacted"] = redacted,
            ["textTruncated"] = text != null && text.Length > options.MaxTextLength,
            ["visible"] = control.Visible,
            ["enabled"] = control.Enabled,
            ["focused"] = control.Focused,
            ["hasHandle"] = control.IsHandleCreated,
            ["bounds"] = control.IsHandleCreated ? Bounds(control.RectangleToScreen(control.ClientRectangle)) : null,
            ["capabilities"] = capabilities
        };
        if (control is TextBoxBase textBox) result["readOnly"] = textBox.ReadOnly;
        if (!inRedactedGrid)
        {
            if (control is CheckBox checkBox) result["checked"] = checkBox.Checked;
            if (control is RadioButton radioButton) result["checked"] = radioButton.Checked;
            if (control is NumericUpDown number) result["value"] = number.Value;
            if (control is TrackBar trackBar) result["value"] = trackBar.Value;
            if (control is DateTimePicker dateTime) result["value"] = dateTime.Value.ToString("O");
            if (control is ListBox listBox) result["selectedIndex"] = listBox.SelectedIndex;
            if (control is ComboBox comboBox) result["selectedIndex"] = comboBox.SelectedIndex;
        }
        if (control is TabControl tabs) result["selectedIndex"] = tabs.SelectedIndex;
        if (gridName != null) result["gridAdapter"] = gridName;
        if (gridError != null) result["gridError"] = gridError;
        return result;
    }

    private static void AddUnique(JArray array, string value)
    {
        if (!array.Any(item => (string?)item == value)) array.Add(value);
    }

    // The predicate of an AddAction overload runs for every control in every tree walk and again when the
    // action is invoked. A throw means "not advertised" / "not accepted"; one diagnostic per action name.
    private bool AcceptsAction(string name, CustomControlAction action, Control control)
    {
        try { return action.Accepts(control); }
        catch (Exception exception)
        {
            if (failedActionPredicates.Add(name))
                log("action_predicate_failed", "The accepts predicate of custom action '" + name + "' failed: " +
                    exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    private JObject DescribeElement(ElementAdapter adapter, object element, string? parentId, object? parent,
        out ElementDescription? described)
    {
        string id = Identity(element, adapter, parent);
        var description = new ElementDescription();
        string? error = null;
        try
        {
            adapter.Describe(element, description);
            described = description;
        }
        catch (Exception exception)
        {
            log("adapter_failed", "Element adapter '" + adapter.Name + "' failed in Describe: " +
                exception.GetType().Name + ": " + exception.Message);
            // Do not trust a partly filled description.
            description = new ElementDescription { Enabled = false, Available = false };
            described = null;
            error = "adapter_failed";
        }

        string? name = description.Name;
        string? text = description.Text;
        Control? hosted = description.HostedControl;
        var properties = new JObject();
        foreach (KeyValuePair<string, string?> property in description.Properties)
            properties[LimitText(property.Key)] = property.Value == null ? null : LimitText(property.Value);
        return new JObject
        {
            ["kind"] = "item",
            ["adapter"] = adapter.Name,
            ["role"] = description.Role,
            ["id"] = id,
            ["generation"] = generation,
            ["parentId"] = parentId,
            ["name"] = name == null ? null : LimitText(name),
            ["nameTruncated"] = name != null && name.Length > options.MaxTextLength,
            ["text"] = text == null ? null : LimitText(text),
            ["textTruncated"] = text != null && text.Length > options.MaxTextLength,
            ["type"] = element.GetType().FullName,
            ["shortType"] = element.GetType().Name,
            ["enabled"] = description.Enabled,
            ["available"] = description.Available,
            ["visible"] = description.Visible,
            ["checked"] = description.Checked,
            ["shortcut"] = description.Shortcut == null ? null : LimitText(description.Shortcut),
            ["opened"] = description.Opened,
            ["childCount"] = description.ChildCount,
            ["childrenOmitted"] = false,
            ["bounds"] = description.ScreenBounds.HasValue ? Bounds(description.ScreenBounds.Value) : null,
            ["hostedControlId"] = hosted != null && !hosted.IsDisposed && !options.IsExcluded(hosted) ? Identity(hosted) : null,
            ["childrenOf"] = null,
            ["properties"] = properties,
            ["capabilities"] = new JArray(description.Capabilities),
            ["childrenError"] = error
        };
    }

    private JObject Inspect(JObject parameters)
    {
        object resolved = parameters["target"] is JObject ? ResolveAny(parameters) : Resolve(parameters);
        if (!(resolved is Control control) || IsElement(resolved))
        {
            // An element is described only; its subitems are read with a tree rooted at it.
            ElementEntry entry = FindEntry(resolved)!;
            object? parent = ParentOf(entry, resolved);
            return DescribeElement(entry.Owner!, resolved, parent == null ? null : ParentId(parent), parent, out _);
        }
        var gridRedaction = new Dictionary<Control, bool>();
        JObject result = Describe(control, control.Parent == null ? null : Identity(control.Parent), gridRedaction);
        bool inRedactedGrid = grids.IsInsideRedactedGrid(control, gridRedaction);
        int maximum = ReadInt(parameters, "maxItems", 100, 1, 100);
        int start = ReadInt(parameters, "startIndex", 0, 0, int.MaxValue);
        var items = new JArray();
        int remaining = 256 * 1024;
        bool truncated = false;
        bool supported = true;

        bool AddItem(JObject item)
        {
            remaining -= item.ToString(Newtonsoft.Json.Formatting.None).Length;
            if (items.Count >= maximum || remaining < 0) { truncated = true; return false; }
            items.Add(item);
            return true;
        }

        if (inRedactedGrid && (control is ComboBox || control is ListBox || control is ListView || control is TreeView))
        {
            // The items and which of them is selected could be the redacted column's value.
            result["itemsUnavailable"] = "redacted-grid";
            supported = false;
        }
        else if (control is ComboBox combo)
        {
            if (start > combo.Items.Count) throw new BridgeFailure("invalid_argument", "startIndex exceeds the item count.");
            result["totalItems"] = combo.Items.Count;
            for (int index = start; index < combo.Items.Count; index++)
                if (!AddItem(new JObject
                {
                    ["index"] = index,
                    ["text"] = LimitText(combo.GetItemText(combo.Items[index])),
                    ["selected"] = index == combo.SelectedIndex
                })) break;
        }
        else if (control is ListBox list)
        {
            if (start > list.Items.Count) throw new BridgeFailure("invalid_argument", "startIndex exceeds the item count.");
            result["totalItems"] = list.Items.Count;
            for (int index = start; index < list.Items.Count; index++)
                if (!AddItem(new JObject
                {
                    ["index"] = index,
                    ["text"] = LimitText(list.GetItemText(list.Items[index])),
                    ["selected"] = list.GetSelected(index)
                })) break;
        }
        else if (control is TabControl tabs)
        {
            if (start > tabs.TabPages.Count) throw new BridgeFailure("invalid_argument", "startIndex exceeds the tab count.");
            result["totalItems"] = tabs.TabPages.Count;
            for (int index = start; index < tabs.TabPages.Count; index++)
            {
                TabPage page = tabs.TabPages[index];
                if (!AddItem(new JObject
                {
                    ["index"] = index,
                    ["name"] = LimitText(page.Name),
                    ["text"] = LimitText(page.Text),
                    ["selected"] = index == tabs.SelectedIndex
                })) break;
            }
        }
        else if (control is ListView listView)
        {
            if (listView.VirtualMode)
            {
                result["itemsUnavailable"] = "virtual-listview-not-supported";
                truncated = true;
            }
            else
            {
                if (start > listView.Items.Count) throw new BridgeFailure("invalid_argument", "startIndex exceeds the item count.");
                result["totalItems"] = listView.Items.Count;
                for (int index = start; index < listView.Items.Count; index++)
                {
                    ListViewItem item = listView.Items[index];
                    var subItems = new JArray();
                    for (int sub = 0; sub < Math.Min(item.SubItems.Count, 8); sub++)
                        subItems.Add(LimitText(item.SubItems[sub].Text));
                    if (!AddItem(new JObject
                    {
                        ["index"] = index,
                        ["text"] = LimitText(item.Text),
                        ["selected"] = item.Selected,
                        ["subItems"] = subItems,
                        ["subItemsTruncated"] = item.SubItems.Count > 8
                    })) break;
                }
            }
        }
        else if (control is TreeView tree)
        {
            if (start > options.MaxNodes)
                throw new BridgeFailure("invalid_argument", "Tree startIndex exceeds the configured traversal limit.");
            int visited = 0;
            bool stopped = false;
            void Visit(TreeNodeCollection children, int[] parentPath)
            {
                if (stopped) return;
                if (parentPath.Length >= 16)
                {
                    if (children.Count != 0) truncated = true;
                    return;
                }
                for (int index = 0; index < children.Count; index++)
                {
                    if (visited >= options.MaxNodes) { truncated = true; stopped = true; return; }
                    TreeNode node = children[index];
                    int[] path = parentPath.Concat(new[] { index }).ToArray();
                    if (visited++ >= start && !AddItem(new JObject
                    {
                        ["path"] = new JArray(path),
                        ["text"] = LimitText(node.Text),
                        ["name"] = LimitText(node.Name),
                        ["expanded"] = node.IsExpanded,
                        ["selected"] = ReferenceEquals(tree.SelectedNode, node),
                        ["childCount"] = node.Nodes.Count
                    })) { stopped = true; return; }
                    Visit(node.Nodes, path);
                    if (stopped) return;
                }
            }
            Visit(tree.Nodes, Array.Empty<int>());
            result["itemOrder"] = "depth-first-preorder";
            result["maxItemDepth"] = 16;
        }
        else supported = false;

        if (supported)
        {
            result["items"] = items;
            result["itemsTruncated"] = truncated;
            result["startIndex"] = start;
            result["itemIdentity"] = "current-view-position";
        }
        return result;
    }

    private JToken Mutate(string method, JObject parameters)
    {
        // Only invoke accepts an element target; every other mutation is control-only and refuses an element.
        object target = method == "invoke" && parameters["target"] is JObject ? ResolveAny(parameters) : Resolve(parameters);
        if (!(target is Control control) || IsElement(target))
        {
            if (method != "invoke") throw new BridgeFailure("unsupported_operation", "This operation applies to controls, not elements.");
            return InvokeElement(FindEntry(target)!, target, parameters);
        }
        if (!control.Enabled) throw new BridgeFailure("disabled_control", "The target control is disabled.");
        if (!control.Visible && method != "ensure_visible")
            throw new BridgeFailure("hidden_control", "The target control is not visible.");
        // select, scroll and set_cell on a grid (an adapter grid or a DataGridView) are grid mutations: one at a time per grid.
        bool gridMutation = (method == "select" || method == "scroll" || method == "set_cell") &&
            (grids.AdapterFor(control) != null || control is DataGridView);
        if (gridMutation) grids.BeginMutation(control);
        try
        {
            switch (method)
            {
                case "invoke": Invoke(control, parameters); break;
                case "set_text": SetText(control, parameters); break;
                case "set_checked": SetChecked(control, parameters); break;
                case "set_value": SetValue(control, parameters); break;
                // An application grid adapter handles a control before the static type switch does.
                case "select":
                    if (grids.AdapterFor(control) != null) grids.Select(control, parameters);
                    else Select(control, parameters);
                    break;
                case "scroll":
                    if (grids.AdapterFor(control) != null) grids.Scroll(control, parameters);
                    else Scroll(control, parameters);
                    break;
                case "ensure_visible": EnsureVisible(control); break;
                // Only a grid an adapter handles can be edited; the result says whether the edit was committed.
                case "set_cell": return grids.SetCell(control, parameters, options.MaxTextLength);
                default: throw new BridgeFailure("unsupported_method", "The requested mutation is not supported.");
            }
        }
        finally
        {
            if (gridMutation) grids.EndMutation(control);
        }
        // A handler can dispose or replace its own control. Do not inspect it afterward.
        return new JObject { ["operation"] = method, ["handlerReturned"] = true };
    }

    // Doc 09 "Prechecks before Execute": the state is described afresh, then the adapter performs the action.
    private JToken InvokeElement(ElementEntry entry, object element, JObject parameters)
    {
        ElementAdapter adapter = entry.Owner!;
        object? parent = ParentOf(entry, element);
        DescribeElement(adapter, element, parent == null ? null : ParentId(parent), parent, out ElementDescription? description);
        if (description == null)
            throw new BridgeFailure("operation_failed", "The element could not be described.");
        string action = OptionalString(parameters, "action") ?? "click";
        if (!description.Capabilities.Contains(action))
            throw new BridgeFailure("unsupported_operation", "The element does not support the requested action.");
        if (!description.Enabled) throw new BridgeFailure("disabled_control", "The target element is disabled.");
        if (!description.Available) throw new BridgeFailure("hidden_control", "The target element is not available.");
        if ((action == "open" || action == "close") && !description.Visible)
            throw new BridgeFailure("not_realized",
                "The element has no on-screen layout; open its parent menu or show the window first.");

        ElementActionOutcome outcome;
        try
        {
            outcome = adapter.Execute(element, action);
        }
        catch (ElementActionException refusal)
        {
            throw new BridgeFailure(refusal.Error switch
            {
                ElementActionError.Disabled => "disabled_control",
                ElementActionError.Hidden => "hidden_control",
                ElementActionError.NotRealized => "not_realized",
                _ => "unsupported_operation"
            }, refusal.Message);
        }

        // A handler can remove or dispose its own element. Describe it again only while it is still in scope,
        // and report what is observed now, never what was requested.
        bool? opened = null;
        if (IsElementInScope(entry, element))
        {
            DescribeElement(adapter, element, parent == null ? null : ParentId(parent), parent, out ElementDescription? after);
            opened = after?.Opened;
        }
        var result = new JObject
        {
            ["operation"] = "invoke",
            ["action"] = action,
            ["handlerReturned"] = true,
            ["handlerDeferred"] = outcome == ElementActionOutcome.HandlerDeferred
        };
        if (opened.HasValue) result["opened"] = opened.Value;
        return result;
    }

    private void Invoke(Control control, JObject parameters)
    {
        string action = OptionalString(parameters, "action") ?? "click";
        if (action == "focus")
        {
            if (!control.Focus()) throw new BridgeFailure("focus_failed", "The control could not receive focus.");
            return;
        }
        if (action == "click" && control is Button button) { button.PerformClick(); return; }
        if (options.TryGetAction(action, out CustomControlAction custom) && AcceptsAction(action, custom, control))
        {
            custom.Execute(control);
            return;
        }
        throw new BridgeFailure("unsupported_operation", "This control does not support the requested semantic action.");
    }

    private static void SetText(Control control, JObject parameters)
    {
        string text = RequiredString(parameters, "text", allowEmpty: true);
        if (text.Length > 1_048_576) throw new BridgeFailure("invalid_argument", "Text input exceeds one million characters.");
        if (control is TextBoxBase textBox)
        {
            if (textBox.ReadOnly) throw new BridgeFailure("read_only", "The text control is read-only.");
            textBox.Text = text;
            return;
        }
        if (control is ComboBox combo && combo.DropDownStyle != ComboBoxStyle.DropDownList)
        {
            combo.Text = text;
            return;
        }
        throw new BridgeFailure("unsupported_operation", "This control does not support editable text.");
    }

    private static void SetChecked(Control control, JObject parameters)
    {
        bool value = ReadBool(parameters, "checked", null);
        if (control is CheckBox check) { check.Checked = value; return; }
        if (control is RadioButton radio) { radio.Checked = value; return; }
        throw new BridgeFailure("unsupported_operation", "This control does not have a supported checked state.");
    }

    private static void SetValue(Control control, JObject parameters)
    {
        JToken? value = parameters["value"];
        if (value == null || value.Type == JTokenType.Null)
            throw new BridgeFailure("invalid_argument", "A value is required.");
        string text = value.Type == JTokenType.String ? (string)value! : value.ToString(Newtonsoft.Json.Formatting.None);
        if (control is NumericUpDown number)
        {
            if (number.ReadOnly) throw new BridgeFailure("read_only", "The numeric control is read-only.");
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
                || parsed < number.Minimum || parsed > number.Maximum)
                throw new BridgeFailure("invalid_argument", "The numeric value is invalid or outside the control's range.");
            number.Value = parsed;
            return;
        }
        if (control is TrackBar track)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                || parsed < track.Minimum || parsed > track.Maximum)
                throw new BridgeFailure("invalid_argument", "The integer value is invalid or outside the control's range.");
            track.Value = parsed;
            return;
        }
        if (control is DateTimePicker date)
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
                || parsed < date.MinDate || parsed > date.MaxDate)
                throw new BridgeFailure("invalid_argument", "The date is invalid or outside the control's range; use an ISO 8601 value.");
            date.Value = parsed;
            return;
        }
        throw new BridgeFailure("unsupported_operation", "This control does not support a typed value.");
    }

    private void Select(Control control, JObject parameters)
    {
        if (control is ComboBox combo)
        {
            combo.SelectedIndex = ReadInt(parameters, "index", null, -1, combo.Items.Count - 1);
            return;
        }
        if (control is ListBox list)
        {
            list.SelectedIndex = ReadInt(parameters, "index", null, -1, list.Items.Count - 1);
            return;
        }
        if (control is TabControl tabs)
        {
            tabs.SelectedIndex = ReadInt(parameters, "index", null, 0, tabs.TabPages.Count - 1);
            return;
        }
        if (control is ListView listView)
        {
            if (listView.VirtualMode) throw new BridgeFailure("unsupported_operation", "Virtual ListView selection is not supported by this adapter.");
            int index = ReadInt(parameters, "index", null, 0, listView.Items.Count - 1);
            listView.SelectedIndices.Clear();
            listView.Items[index].Selected = true;
            listView.Items[index].Focused = true;
            return;
        }
        if (control is TreeView tree)
        {
            if (!(parameters["path"] is JArray path) || path.Count == 0 || path.Count > 128)
                throw new BridgeFailure("invalid_argument", "Tree selection requires a nonempty array of child indices in path.");
            TreeNodeCollection children = tree.Nodes;
            TreeNode? node = null;
            foreach (JToken entry in path)
            {
                int index = ReadInt(new JObject { ["index"] = entry.DeepClone() }, "index", null, 0, children.Count - 1);
                node = children[index];
                children = node.Nodes;
            }
            tree.SelectedNode = node;
            node!.EnsureVisible();
            return;
        }
        if (control is DataGridView grid)
        {
            int row = ReadInt(parameters, "row", null, 0, grid.Rows.Count - 1);
            int column = ReadInt(parameters, "column", null, 0, grid.Columns.Count - 1);
            // The token is optional here and checked when supplied, after the range checks.
            grids.CheckTokenIfSupplied(grid, OptionalString(parameters, "viewToken"));
            if (!grid.Rows[row].Visible || !grid.Columns[column].Visible)
                throw new BridgeFailure("hidden_control", "The selected grid row or column is hidden.");
            grid.CurrentCell = grid.Rows[row].Cells[column];
            grid.CurrentCell.Selected = true;
            return;
        }
        throw new BridgeFailure("unsupported_operation", "This control does not support selection.");
    }

    private void Scroll(Control control, JObject parameters)
    {
        if (control is DataGridView grid)
        {
            // The token is optional here and checked when supplied, before anything is scrolled.
            grids.CheckTokenIfSupplied(grid, OptionalString(parameters, "viewToken"));
            if (HasValue(parameters, "row"))
                grid.FirstDisplayedScrollingRowIndex = ReadInt(parameters, "row", null, 0, grid.Rows.Count - 1);
            if (HasValue(parameters, "column"))
                grid.FirstDisplayedScrollingColumnIndex = ReadInt(parameters, "column", null, 0, grid.Columns.Count - 1);
            if (!HasValue(parameters, "row") && !HasValue(parameters, "column"))
                throw new BridgeFailure("invalid_argument", "Grid scrolling requires row or column.");
            return;
        }
        if (control is ListBox list)
        {
            list.TopIndex = ReadInt(parameters, "row", null, 0, list.Items.Count - 1);
            return;
        }
        if (control is ListView listView)
        {
            listView.EnsureVisible(ReadInt(parameters, "row", null, 0, listView.Items.Count - 1));
            return;
        }
        if (control is ScrollableControl scroll && scroll.AutoScroll)
        {
            int x = ReadInt(parameters, "x", 0, 0, int.MaxValue);
            int y = ReadInt(parameters, "y", 0, 0, int.MaxValue);
            scroll.AutoScrollPosition = new Point(x, y);
            return;
        }
        throw new BridgeFailure("unsupported_operation", "This control does not have a supported scrolling adapter.");
    }

    private static void EnsureVisible(Control control)
    {
        // Two passes, deliberately. Scrolling a container whose ancestor tab page is not yet
        // current runs against a hidden, never-laid-out panel: the scroll is lost or leaves the
        // container with a bogus offset. First make every ancestor tab current, then scroll
        // from the innermost container outward.
        var chain = new List<Control>();
        for (Control? cursor = control; cursor != null && chain.Count <= 256; cursor = cursor.Parent)
            chain.Add(cursor);
        for (int index = 0; index + 1 < chain.Count; index++)
            if (chain[index] is TabPage page && chain[index + 1] is TabControl tabs && !ReferenceEquals(tabs.SelectedTab, page))
                tabs.SelectedTab = page;
        for (int index = 0; index + 1 < chain.Count; index++)
            if (chain[index + 1] is ScrollableControl scroll && scroll.AutoScroll)
            {
                scroll.PerformLayout();
                scroll.ScrollControlIntoView(chain[index]);
            }
        if (!control.Visible)
            throw new BridgeFailure("hidden_control", "The control remains hidden; ensure_visible does not change its Visible property.");
    }

    private JObject ReadGrid(JObject parameters)
    {
        Control target = Resolve(parameters);
        // An application grid adapter handles a control before the built-in DataGridView handling does.
        if (grids.AdapterFor(target) != null)
            return grids.ReadPage(target, parameters, generation, options.MaxTextLength);
        if (!(target is DataGridView grid))
            throw new BridgeFailure("unsupported_operation", "read_grid supports DataGridView and controls with a registered grid adapter only.");
        int rowCount = ReadInt(parameters, "rowCount", 25, 1, 200);
        int columnCount = ReadInt(parameters, "columnCount", 20, 1, 50);
        int startRow = ReadInt(parameters, "startRow", 0, 0, grid.Rows.Count);
        int startColumn = ReadInt(parameters, "startColumn", 0, 0, grid.Columns.Count);
        int endRow = startRow + Math.Min(rowCount, grid.Rows.Count - startRow);
        int endColumn = startColumn + Math.Min(columnCount, grid.Columns.Count - startColumn);
        var columns = new JArray();
        int textCharacters = 0;
        for (int c = startColumn; c < endColumn; c++)
        {
            string name = LimitText(grid.Columns[c].Name);
            string header = LimitText(grid.Columns[c].HeaderText);
            textCharacters += name.Length + header.Length;
            if (textCharacters > 256 * 1024)
                throw new BridgeFailure("response_limit", "The grid headers contain too much text; reduce columnCount.");
            columns.Add(new JObject
            {
                ["index"] = c,
                ["name"] = name,
                ["header"] = header,
                ["visible"] = grid.Columns[c].Visible
            });
        }
        var rows = new JArray();
        for (int r = startRow; r < endRow; r++)
        {
            DataGridViewRow row = grid.Rows[r];
            if (row.IsNewRow) continue;
            var cells = new JArray();
            for (int c = startColumn; c < endColumn; c++)
            {
                string text = LimitText(Convert.ToString(row.Cells[c].FormattedValue, CultureInfo.InvariantCulture) ?? string.Empty);
                textCharacters += text.Length;
                if (textCharacters > 256 * 1024)
                    throw new BridgeFailure("response_limit", "The grid range contains too much text; reduce rowCount or columnCount.");
                cells.Add(text);
            }
            rows.Add(new JObject { ["index"] = r, ["visible"] = row.Visible, ["cells"] = cells });
        }
        return new JObject
        {
            ["generation"] = generation,
            ["dataKind"] = "formatted-display-values",
            ["totalRowsIncludingNewRow"] = grid.Rows.Count,
            ["totalColumns"] = grid.Columns.Count,
            ["startRow"] = startRow,
            ["startColumn"] = startColumn,
            ["columns"] = columns,
            ["rows"] = rows,
            ["hasMoreRows"] = endRow < grid.Rows.Count,
            ["hasMoreColumns"] = endColumn < grid.Columns.Count,
            ["gridAdapter"] = GridAdapter.DataGridViewName,
            ["viewToken"] = grids.CurrentToken(grid)
        };
    }

    private JObject Capture(JObject parameters)
    {
        string scope = OptionalString(parameters, "scope") ?? "control";
        bool includeFrame = ReadBool(parameters, "includeFrame", false);
        if (HasValue(parameters, "region") && !(parameters["region"] is JObject))
            throw new BridgeFailure("invalid_argument", "Capture region must be an object.");
        var targets = new List<Control>();
        int skipped = 0;
        if (scope == "application")
        {
            if (HasValue(parameters, "region"))
                throw new BridgeFailure("invalid_argument", "A region cannot be combined with application scope.");
            targets.AddRange(GetWindows(out skipped).Where(form => form.Visible));
            if (targets.Count > 32)
                throw new BridgeFailure("capture_limit", "Application capture is limited to 32 windows; capture individual windows instead.");
        }
        else if (scope == "window" || scope == "control")
        {
            Control target = Resolve(parameters);
            if (scope == "window") target = target.FindForm() ?? target;
            targets.Add(target);
        }
        else throw new BridgeFailure("invalid_argument", "Capture scope must be control, window, or application.");

        if (targets.Count == 0) throw new BridgeFailure("no_windows", "There are no visible windows on the registered UI thread.");
        var images = new JArray();
        int totalBytes = 0;
        foreach (Control target in targets)
        {
            JObject image = ScreenCapture.Capture(target, parameters["region"] as JObject,
                includeFrame, options.MaxCapturePixels, options.MaxImageBytes - totalBytes, out int imageBytes);
            totalBytes += imageBytes;
            image["target"] = new JObject { ["generation"] = generation, ["id"] = Identity(target) };
            images.Add(image);
        }
        return new JObject
        {
            ["generation"] = generation,
            ["captureMode"] = "screen_copy",
            ["images"] = images,
            ["skippedOtherUiThreads"] = skipped
        };
    }

    private string LimitText(string text) => text.Length > options.MaxTextLength ? text.Substring(0, options.MaxTextLength) : text;

    private static bool IsPassword(Control control)
        => control is TextBox text && (text.UseSystemPasswordChar || text.PasswordChar != '\0')
            || control is MaskedTextBox masked && (masked.UseSystemPasswordChar || masked.PasswordChar != '\0');

    internal static JObject Bounds(Rectangle rectangle) => new()
    {
        ["x"] = rectangle.X,
        ["y"] = rectangle.Y,
        ["width"] = rectangle.Width,
        ["height"] = rectangle.Height,
        ["coordinateSpace"] = "screen-in-app-dpi-context"
    };

    internal static int ReadInt(JObject input, string name, int? fallback, int minimum, int maximum)
    {
        JToken? token = input[name];
        if ((token == null || token.Type == JTokenType.Null) && fallback.HasValue) return fallback.Value;
        if (token == null || token.Type != JTokenType.Integer ||
            !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ||
            value < minimum || value > maximum)
            throw new BridgeFailure("invalid_argument", name + " must be an integer within the supported range.");
        return (int)value;
    }

    internal static bool ReadBool(JObject input, string name, bool? fallback)
    {
        JToken? token = input[name];
        if ((token == null || token.Type == JTokenType.Null) && fallback.HasValue) return fallback.Value;
        if (token == null || token.Type != JTokenType.Boolean)
            throw new BridgeFailure("invalid_argument", name + " must be a Boolean.");
        return (bool)token;
    }

    internal static string RequiredString(JObject input, string name, bool allowEmpty = false)
    {
        string? value = OptionalString(input, name);
        if (value == null || !allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new BridgeFailure("invalid_argument", name + " must be a nonempty string.");
        return value;
    }

    internal static string? OptionalString(JObject input, string name)
    {
        JToken? value = input[name];
        if (value == null || value.Type == JTokenType.Null) return null;
        if (value.Type != JTokenType.String)
            throw new BridgeFailure("invalid_argument", name + " must be a string.");
        return (string?)value;
    }

    internal static bool HasValue(JObject input, string name)
        => input[name] != null && input[name]!.Type != JTokenType.Null;

    private void AssertUiThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != owningThreadId)
            throw new InvalidOperationException("UI state must be accessed on the registered thread.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Thread.CurrentThread.ManagedThreadId == owningThreadId)
            dispatcher.Dispose();
        else
        {
            try { dispatcher.BeginInvoke(new Action(dispatcher.Dispose)); }
            catch (InvalidOperationException) { /* Process shutdown can already have removed the dispatcher. */ }
        }
    }

    private sealed class ElementIdentity
    {
        internal ElementIdentity(string id) => Id = id;
        internal string Id { get; }
    }

    private sealed class ElementEntry
    {
        internal ElementEntry(object element) => Target = new WeakReference(element);
        internal WeakReference Target { get; set; }
        internal WeakReference? ObservedParent { get; set; }
        // Adapters must not hold elements strongly; the entry must not keep an element alive.
        internal ElementAdapter? Owner { get; set; }
    }

    /// <summary>Per-response state of an element walk: each element with the node id it was emitted under.</summary>
    private sealed class ElementWalk
    {
        // The parent id an element was emitted under; null for the root of an element-rooted tree.
        internal Dictionary<object, string?> EmittedUnder { get; } = new(ReferenceComparer.Instance);

        // True for a tree rooted at an element: closed dropdowns expand instead of collapsing.
        internal bool ExpandClosed { get; set; }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
        int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class ActionReceipt
    {
        internal ActionReceipt(string id) { Id = id; CreatedUtc = DateTimeOffset.UtcNow; }
        internal string Id { get; }
        internal DateTimeOffset CreatedUtc { get; }
        internal string State { get; set; } = "queued";
        internal DateTimeOffset? StartedUtc { get; set; }
        internal DateTimeOffset? CompletedUtc { get; set; }
        internal JToken? Result { get; set; }
        internal JObject? Error { get; set; }
    }
}
