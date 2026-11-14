using System;
using System.Collections.Generic;

namespace WinFormsMcp.Instrumented.Elements;

/// <summary>
/// Receives the elements an adapter reports for one parent. The bridge sets the budget;
/// an adapter that ignores a false result cannot exceed it because later adds are dropped.
/// The bridge visits forms and controls that have a parent itself, so it drops a
/// <see cref="System.Windows.Forms.Form"/> or a <see cref="System.Windows.Forms.Control"/> with a
/// non-null <c>Parent</c> and records a diagnostic; a control without a parent (a context menu)
/// may be reported as an element. A hosted control is reported through
/// <see cref="ElementDescription.HostedControl"/>.
/// </summary>
public sealed class ElementSink
{
    private readonly List<object> items = new();
    private readonly int budget;
    private readonly object? seek;

    // A sink given an element to seek stops at it: the bridge uses this to check that a parent still lists
    // an element without collecting the whole child collection.
    internal ElementSink(int budget, object? seek = null)
    {
        this.budget = Math.Max(0, budget);
        this.seek = seek;
    }

    /// <summary>How many more elements are accepted.</summary>
    public int Remaining => Found ? 0 : budget - items.Count;

    internal IReadOnlyList<object> Items => items;

    /// <summary>True when at least one element was dropped because the budget was spent.</summary>
    internal bool Overflowed { get; private set; }

    /// <summary>True when the element this sink seeks was added.</summary>
    internal bool Found { get; private set; }

    /// <summary>
    /// Adds an element; returns false (and drops it) once the budget is spent. Emit reference types
    /// with a stable identity: boxed value types and interned strings have none, so the bridge cannot
    /// tell two such elements apart.
    /// </summary>
    public bool Add(object element)
    {
        if (element == null) throw new ArgumentNullException(nameof(element));
        if (Found) return false;
        if (seek != null && ReferenceEquals(element, seek))
        {
            Found = true;
            return false;
        }
        if (items.Count >= budget)
        {
            Overflowed = true;
            return false;
        }
        items.Add(element);
        return true;
    }
}
