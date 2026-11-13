using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace WinFormsMcp.Instrumented;

/// <summary>Optional policy and extensions for the standard WinForms bridge.</summary>
public sealed class McpBridgeBuilder
{
    private readonly List<Func<Control, bool>> exclusions = new();
    private readonly Dictionary<string, CustomControlAction> actions = new(StringComparer.Ordinal);

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
    /// The predicate runs on the registered UI thread.
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
        if (string.IsNullOrWhiteSpace(name) || name == "click" || name == "focus")
            throw new ArgumentException("Choose a nonempty custom action name other than click or focus.", nameof(name));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        if (actions.ContainsKey(name)) throw new ArgumentException("That action is already registered.", nameof(name));
        actions.Add(name, new CustomControlAction(c => c is TControl, c => callback((TControl)c)));
        return this;
    }

    internal bool IsExcluded(Control control)
    {
        foreach (var predicate in exclusions)
            if (predicate(control)) return true;
        return false;
    }

    internal IEnumerable<KeyValuePair<string, CustomControlAction>> Actions => actions;

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
