using System;

namespace WinFormsMcp.Instrumented.Elements;

/// <summary>How an adapter's action returned to the bridge.</summary>
public enum ElementActionOutcome
{
    /// <summary>The element's handler ran and returned.</summary>
    HandlerReturned,

    /// <summary>The framework postponed the handler; it has not necessarily run yet.</summary>
    HandlerDeferred
}

/// <summary>Why an adapter refuses an action.</summary>
public enum ElementActionError
{
    /// <summary>The element does not support the action.</summary>
    Unsupported,

    /// <summary>The element is disabled.</summary>
    Disabled,

    /// <summary>The element is not available to show.</summary>
    Hidden,

    /// <summary>The element has no on-screen layout yet.</summary>
    NotRealized
}

/// <summary>Thrown by <see cref="ElementAdapter.Execute"/> to refuse an action with a specific reason.</summary>
public sealed class ElementActionException : Exception
{
    public ElementActionException(ElementActionError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>The reason the action was refused.</summary>
    public ElementActionError Error { get; }
}
