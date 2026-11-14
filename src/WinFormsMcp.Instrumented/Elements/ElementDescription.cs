using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WinFormsMcp.Instrumented.Elements;

/// <summary>
/// The state an adapter reports for one element. The bridge validates role and capability
/// strings and bounds all text; it fills the CLR type names itself. Text and properties are
/// exposed verbatim apart from the length bounds: adapters must redact secrets (for example
/// the text of a password editor) themselves.
/// </summary>
public sealed class ElementDescription
{
    internal const int MaxProperties = 16;
    private static readonly Regex RolePattern = new(@"^[a-z_]{1,32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CapabilityPattern = new(@"^[a-z_]{1,32}\z", RegexOptions.CultureInvariant);
    private readonly List<string> capabilities = new();
    private readonly List<KeyValuePair<string, string?>> properties = new();
    private string role = "unknown";

    internal ElementDescription()
    {
    }

    /// <summary>Semantic role: lowercase letters and underscores, at most 32 characters.</summary>
    public string Role
    {
        get => role;
        set
        {
            if (value == null || !RolePattern.IsMatch(value))
                throw new ArgumentException("A role must match [a-z_]{1,32}.", nameof(value));
            role = value;
        }
    }

    public string? Name { get; set; }

    public string? Text { get; set; }

    public string? Shortcut { get; set; }

    /// <summary>Effective enabled state, including the state of the element's holder.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Configured to show (as opposed to realized on screen now, see <see cref="Visible"/>).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Realized on screen now.</summary>
    public bool Visible { get; set; }

    public bool? Checked { get; set; }

    /// <summary>Whether the element's dropdown or submenu is open; null when it has none.</summary>
    public bool? Opened { get; set; }

    public int? ChildCount { get; set; }

    /// <summary>Screen coordinates, or null when the element has no on-screen layout.</summary>
    public Rectangle? ScreenBounds { get; set; }

    /// <summary>A control the element hosts, which stays a control node of the tree.</summary>
    public Control? HostedControl { get; set; }

    internal IReadOnlyList<string> Capabilities => capabilities;

    internal IReadOnlyList<KeyValuePair<string, string?>> Properties => properties;

    /// <summary>Declares an action the element supports: click, open, close or a name matching [a-z_]{1,32}.</summary>
    public void AddCapability(string action)
    {
        if (action == null || !CapabilityPattern.IsMatch(action))
            throw new ArgumentException("A capability must match [a-z_]{1,32}.", nameof(action));
        if (!capabilities.Contains(action)) capabilities.Add(action);
    }

    /// <summary>Adds a named text property; at most 16 per element. Adding a name again replaces its value.</summary>
    public void AddProperty(string name, string? value)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A property needs a name.", nameof(name));
        int existing = properties.FindIndex(p => p.Key == name);
        if (existing >= 0)
        {
            properties[existing] = new KeyValuePair<string, string?>(name, value);
            return;
        }
        if (properties.Count >= MaxProperties)
            throw new InvalidOperationException("An element can have at most 16 properties.");
        properties.Add(new KeyValuePair<string, string?>(name, value));
    }
}
