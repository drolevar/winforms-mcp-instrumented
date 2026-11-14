using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using WinFormsMcp.Instrumented.Elements;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>A shared command object, like a toolbar or ribbon command in a third-party control suite.</summary>
    public sealed class FakeCommand : Component
    {
        public string CommandName { get; set; } = "";
        public string? Text { get; set; }
        public bool Enabled { get; set; } = true;
        public bool? Opened { get; set; }

        /// <summary>The child collection every placement of this command shares.</summary>
        public List<FakePlacement> Children { get; } = new List<FakePlacement>();
    }

    /// <summary>
    /// One place a command appears: not a Component, and not alive once its command is gone.
    /// This is the element the bridge sees; the shared command carries no layout or state of its own.
    /// </summary>
    public sealed class FakePlacement
    {
        public FakeCommand? Command { get; set; }
        public object? Parent { get; set; }
    }

    /// <summary>
    /// A stand-in for a third-party adapter that uses only the public seam (no InternalsVisibleTo).
    /// Its elements hang under a control named FakeHost, plus optional window roots.
    /// </summary>
    public sealed class FakeElementAdapter : ElementAdapter
    {
        public const string HostName = "FakeHost";

        public FakeElementAdapter(string name = "fake") : base(name)
        {
        }

        /// <summary>Placements reported for the control named FakeHost.</summary>
        public List<FakePlacement> HostPlacements { get; } = new List<FakePlacement>();

        /// <summary>Placements reported for other controls, keyed by control name.</summary>
        public Dictionary<string, List<FakePlacement>> NamedHosts { get; } = new Dictionary<string, List<FakePlacement>>();

        /// <summary>Placements reported through GetWindowRoots for every window.</summary>
        public List<FakePlacement> WindowRoots { get; } = new List<FakePlacement>();

        /// <summary>Arbitrary objects reported for FakeHost as they are (used to emit things that are not placements).</summary>
        public List<object> RawElements { get; } = new List<object>();

        /// <summary>When positive, FakeHost reports this many extra placements and ignores Add's result.</summary>
        public int AddCountToIgnoreBudget { get; set; }

        /// <summary>Returns true for the parent whose GetChildren must throw.</summary>
        public Func<object, bool>? ThrowInGetChildren { get; set; }

        /// <summary>Returns true for the element whose Describe must throw.</summary>
        public Func<object, bool>? ThrowInDescribe { get; set; }

        /// <summary>Returns true for the element whose IsAlive must throw.</summary>
        public Func<object, bool>? ThrowInIsAlive { get; set; }

        /// <summary>The sink's Remaining as seen right after Add refused a placement (host placements only).</summary>
        public List<int> RemainingWhenAddRefused { get; } = new List<int>();

        /// <summary>Role set by Describe; tests change it to provoke validation failures.</summary>
        public string Role { get; set; } = "fake_item";

        /// <summary>Capability set by Describe.</summary>
        public string Capability { get; set; } = "click";

        /// <summary>Text set by Describe, overriding the command's text when not null.</summary>
        public string? TextOverride { get; set; }

        /// <summary>Additional capabilities declared by Describe (for example open and close).</summary>
        public List<string> ExtraCapabilities { get; } = new List<string>();

        /// <summary>Available state set by Describe.</summary>
        public bool Available { get; set; } = true;

        /// <summary>Visible (realized) state set by Describe.</summary>
        public bool Visible { get; set; } = true;

        public Func<object, string, ElementActionOutcome>? OnExecute { get; set; }

        public override void GetChildren(object parent, ElementSink sink)
        {
            if (ThrowInGetChildren != null && ThrowInGetChildren(parent))
                throw new InvalidOperationException("Fake GetChildren failure.");
            if (parent is Control control && control.Name == HostName)
            {
                foreach (FakePlacement placement in HostPlacements)
                    if (!sink.Add(placement))
                    {
                        RemainingWhenAddRefused.Add(sink.Remaining);
                        break;
                    }
                foreach (object raw in RawElements)
                    if (!sink.Add(raw)) break;
                var filler = new FakeCommand { CommandName = "Filler" };
                for (int index = 0; index < AddCountToIgnoreBudget; index++)
                    sink.Add(new FakePlacement { Command = filler, Parent = control });
            }
            else if (parent is Control named && NamedHosts.TryGetValue(named.Name, out List<FakePlacement>? placements))
            {
                foreach (FakePlacement placement in placements)
                    if (!sink.Add(placement)) break;
            }
            else if (parent is FakePlacement owner && owner.Command != null)
            {
                foreach (FakePlacement child in owner.Command.Children)
                    if (!sink.Add(child)) break;
            }
        }

        public override void GetWindowRoots(Form window, ElementSink sink)
        {
            foreach (FakePlacement placement in WindowRoots)
                if (!sink.Add(placement)) break;
        }

        /// <summary>The placement's own Parent when set; null (the observed parent applies) otherwise.</summary>
        public override object? GetParent(object element) => (element as FakePlacement)?.Parent;

        public override bool IsAlive(object element)
        {
            if (ThrowInIsAlive != null && ThrowInIsAlive(element))
                throw new InvalidOperationException("Fake IsAlive failure.");
            if (element is Control control) return !control.IsDisposed;
            return element is FakePlacement placement && placement.Command != null;
        }

        public override void Describe(object element, ElementDescription description)
        {
            if (ThrowInDescribe != null && ThrowInDescribe(element))
                throw new InvalidOperationException("Fake Describe failure.");
            if (element is Control hostedElement)
            {
                // A control emitted as an element (it had no parent when the adapter reported it).
                description.Role = Role;
                description.Name = hostedElement.Name;
                description.Enabled = hostedElement.Enabled;
                description.Available = Available;
                description.Visible = Visible;
                description.AddCapability(Capability);
                return;
            }
            var placement = (FakePlacement)element;
            FakeCommand? command = placement.Command;
            description.Role = Role;
            description.Name = command?.CommandName;
            description.Text = TextOverride ?? command?.Text;
            description.Enabled = command != null && command.Enabled;
            description.Available = Available;
            description.Visible = Visible;
            description.Opened = command?.Opened;
            description.ChildCount = command?.Children.Count;
            description.AddCapability(Capability);
            foreach (string capability in ExtraCapabilities) description.AddCapability(capability);
        }

        public override ElementActionOutcome Execute(object element, string action)
            => OnExecute != null ? OnExecute(element, action) : base.Execute(element, action);
    }
}
