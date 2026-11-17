using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WinFormsMcp.Instrumented.Elements;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Instrumented;

/// <summary>Optional policy and extensions for the standard WinForms bridge.</summary>
public sealed class McpBridgeBuilder
{
    private readonly List<Func<Control, bool>> exclusions = new();
    private readonly Dictionary<string, CustomControlAction> actions = new(StringComparer.Ordinal);
    private readonly List<ElementAdapter> elementAdapters = new();
    private readonly List<GridAdapter> gridAdapters = new();

    /// <summary>Maximum controls visited by one tree or selector request.</summary>
    public int MaxNodes { get; set; } = 2_000;

    /// <summary>Maximum child depth returned by a tree request.</summary>
    public int MaxDepth { get; set; } = 32;

    /// <summary>Maximum characters exposed for one text or cell value.</summary>
    public int MaxTextLength { get; set; } = 4_096;

    /// <summary>How long a read can wait for the UI thread.</summary>
    public TimeSpan UiRequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum combined PNG bytes in one capture response.</summary>
    public int MaxImageBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Maximum pixel area of one captured image.</summary>
    public int MaxCapturePixels { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Excludes matching controls and their descendants from discovery and actions.
    /// Screen capture is a view of visible pixels and does not redact excluded controls.
    /// The predicate runs on the registered UI thread. It also runs on unparented controls
    /// that an element adapter reports as elements (for example a ContextMenuStrip), and
    /// an exception thrown by the predicate hides that element.
    /// </summary>
    public McpBridgeBuilder Exclude(Func<Control, bool> predicate)
    {
        exclusions.Add(predicate ?? throw new ArgumentNullException(nameof(predicate)));
        return this;
    }

    /// <summary>
    /// Registers an explicitly named semantic action for matching controls.
    /// Invoke it with the standard invoke operation's action parameter.
    /// The callback runs on the control's UI thread and can open modal dialogs.
    /// </summary>
    public McpBridgeBuilder AddAction<TControl>(string name, Action<TControl> callback)
        where TControl : Control
    {
        return Register(name, c => c is TControl, callback == null ? null : c => callback((TControl)c));
    }

    /// <summary>
    /// Registers an explicitly named semantic action for the controls <paramref name="accepts"/> matches.
    /// Invoke it with the standard invoke operation's action parameter. Names are unique across both
    /// <c>AddAction</c> overloads. The predicate runs on the registered UI thread for every control in every
    /// tree walk, so it must be cheap; an exception means "not advertised". The bridge evaluates it again
    /// when the action is invoked. The callback runs on the control's UI thread and can open modal dialogs.
    /// </summary>
    public McpBridgeBuilder AddAction(string name, Func<Control, bool> accepts, Action<Control> callback)
        => Register(name, accepts, callback);

    // One validation for both AddAction overloads (the generic one passes null for a null callback).
    private McpBridgeBuilder Register(string name, Func<Control, bool>? accepts, Action<Control>? callback)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "click" || name == "focus" || name == "open" || name == "close")
            throw new ArgumentException("Choose a nonempty custom action name other than click, focus, open, or close.", nameof(name));
        if (accepts == null) throw new ArgumentNullException(nameof(accepts));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        if (actions.ContainsKey(name)) throw new ArgumentException("That action is already registered.", nameof(name));
        actions.Add(name, new CustomControlAction(accepts, callback));
        return this;
    }

    /// <summary>
    /// Registers an adapter that exposes non-control elements (such as menu items) in the tree.
    /// Adapters are consulted in registration order. The adapter runs on the registered UI thread.
    /// </summary>
    public McpBridgeBuilder AddElementAdapter(ElementAdapter adapter)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        if (adapter.Name == ToolStripAdapter.AdapterName)
            throw new ArgumentException("The adapter name 'toolstrip' is reserved for the built-in ToolStrip adapter.", nameof(adapter));
        if (elementAdapters.Any(existing => existing.Name == adapter.Name))
            throw new ArgumentException("An element adapter with that name is already registered.", nameof(adapter));
        elementAdapters.Add(adapter);
        return this;
    }

    /// <summary>
    /// Registers an adapter that lets the bridge read, select and edit a family of grid controls.
    /// Adapters are consulted in registration order, before the built-in DataGridView handling.
    /// The adapter runs on the registered UI thread.
    /// </summary>
    public McpBridgeBuilder AddGridAdapter(GridAdapter adapter)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        if (adapter.Name == GridAdapter.DataGridViewName)
            throw new ArgumentException("The adapter name 'datagridview' is reserved for the built-in DataGridView handling.", nameof(adapter));
        if (gridAdapters.Any(existing => existing.Name == adapter.Name))
            throw new ArgumentException("A grid adapter with that name is already registered.", nameof(adapter));
        gridAdapters.Add(adapter);
        return this;
    }

    internal bool IsExcluded(Control control)
    {
        foreach (var predicate in exclusions)
            if (predicate(control)) return true;
        return false;
    }

    internal IEnumerable<KeyValuePair<string, CustomControlAction>> Actions => actions;

    internal IReadOnlyList<ElementAdapter> ElementAdapters => elementAdapters;

    internal IReadOnlyList<GridAdapter> GridAdapters => gridAdapters;

    internal bool TryGetAction(string name, out CustomControlAction action)
        => actions.TryGetValue(name, out action!);

    internal void Validate()
    {
        if (MaxNodes < 1 || MaxNodes > 20_000) throw new ArgumentOutOfRangeException(nameof(MaxNodes));
        if (MaxDepth < 1 || MaxDepth > 128) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MaxTextLength < 1 || MaxTextLength > 65_536) throw new ArgumentOutOfRangeException(nameof(MaxTextLength));
        if (UiRequestTimeout < TimeSpan.FromMilliseconds(100) || UiRequestTimeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(UiRequestTimeout));
        if (MaxImageBytes < 1 || MaxImageBytes > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxImageBytes));
        if (MaxCapturePixels < 1 || MaxCapturePixels > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxCapturePixels));
    }
}

internal sealed class CustomControlAction
{
    internal CustomControlAction(Func<Control, bool> accepts, Action<Control> execute)
    {
        Accepts = accepts;
        Execute = execute;
    }

    internal Func<Control, bool> Accepts { get; }
    internal Action<Control> Execute { get; }
}
