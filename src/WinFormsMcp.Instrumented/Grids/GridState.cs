using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;

namespace WinFormsMcp.Instrumented.Grids;

/// <summary>
/// The bridge's freshness state for one grid: a random salt, a change counter and the last fingerprint.
/// The token is <c>"&lt;salt&gt;.&lt;counter&gt;"</c>. The state refers to nothing else, in particular not to
/// the grid, so that a <see cref="GridViewSignal"/> that points at it does not keep the grid alive.
/// The salt keeps one grid's token from being accepted for another and is never logged.
/// </summary>
internal sealed class GridState
{
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();
    private long counter;
    private bool hasFingerprint;
    private long fingerprint;

    internal GridState()
    {
        var bytes = new byte[8];
        Random.GetBytes(bytes);
        Salt = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    internal string Salt { get; }

    /// <summary>
    /// True while a grid mutation (select, scroll, set_cell) is started on this grid. A second one is refused
    /// as <c>grid_busy</c>. It is set and cleared on the UI thread only, so it needs no synchronization.
    /// </summary>
    internal bool MutationInProgress { get; set; }

    /// <summary>The token for the state as of now; it does not read the view, <see cref="GridHost"/> refreshes first.</summary>
    internal string Token => Salt + "." + Interlocked.Read(ref counter).ToString(CultureInfo.InvariantCulture);

    /// <summary>Raised from the adapter's events (any thread that raises the grid's event; the counter is atomic).</summary>
    internal void MarkChanged() => Interlocked.Increment(ref counter);

    /// <summary>
    /// Compares the fingerprint of the current view with the stored one and counts a difference as a
    /// change. The first observation only records the baseline. UI thread only.
    /// </summary>
    internal void Observe(long current)
    {
        if (hasFingerprint && fingerprint != current) MarkChanged();
        fingerprint = current;
        hasFingerprint = true;
    }
}
