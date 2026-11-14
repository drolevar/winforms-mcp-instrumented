using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WinFormsMcp.Instrumented.Elements;

/// <summary>
/// Teaches the bridge about elements that are not <see cref="Control"/> instances, such as menu
/// items and toolbar commands. Every member runs on the registered UI thread. An adapter must
/// not hold elements strongly (the registry never keeps an element alive), never creates window
/// handles, and never creates dropdowns lazily. The bridge visits every form and every control
/// with a parent itself, so it drops a <see cref="Form"/> or a <see cref="Control"/> with a
/// non-null <see cref="Control.Parent"/> that an adapter reports as an element. A control
/// without a parent, such as a <see cref="ContextMenuStrip"/>, is not visited by the bridge and
/// may be an element (once it gets a parent, the bridge treats it as a control). A control an
/// element hosts is reported through <see cref="ElementDescription.HostedControl"/>. Register with
/// <see cref="McpBridgeBuilder.AddElementAdapter"/>.
/// </summary>
public abstract class ElementAdapter
{
    private static readonly Regex NamePattern = new(@"^[a-z0-9._-]{1,48}\z", RegexOptions.CultureInvariant);

    /// <param name="name">Adapter name advertised in hello, matching [a-z0-9._-]{1,48}.</param>
    protected ElementAdapter(string name)
    {
        if (name == null || !NamePattern.IsMatch(name))
            throw new ArgumentException("An adapter name must match [a-z0-9._-]{1,48}.", nameof(name));
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// Reports the child elements of <paramref name="parent"/>, which is any visited
    /// <see cref="Control"/> (return quickly if not applicable) or an element this adapter
    /// emitted. Stop adding when <see cref="ElementSink.Add"/> returns false.
    /// </summary>
    public abstract void GetChildren(object parent, ElementSink sink);

    /// <summary>Reports elements not reachable from any control; their tree parent is the window.</summary>
    public virtual void GetWindowRoots(Form window, ElementSink sink)
    {
    }

    /// <summary>True while the element is still usable (for example not disposed).</summary>
    public abstract bool IsAlive(object element);

    /// <summary>
    /// The live parent when the framework exposes one; null means the observed parent. A non-null
    /// result must be a <see cref="Control"/> or an element this adapter emits; any other object
    /// makes every element of this adapter permanently <c>stale_target</c>.
    /// </summary>
    public virtual object? GetParent(object element) => null;

    /// <summary>Fills the element's current state.</summary>
    public abstract void Describe(object element, ElementDescription description);

    /// <summary>Performs a declared action. The default refuses every action.</summary>
    public virtual ElementActionOutcome Execute(object element, string action)
        => throw new ElementActionException(ElementActionError.Unsupported,
            "The adapter does not support the requested action.");
}
