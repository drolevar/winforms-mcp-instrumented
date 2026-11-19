#if WINFORMS_MCP_INSTRUMENTED
using System.Windows.Forms;
using DevExpress.XtraBars;
using DevExpress.XtraBars.Controls;
using DevExpress.XtraBars.Ribbon;
using WinFormsMcp.Instrumented.Elements;

namespace WinFormsMcp.Examples.Adapters
{
    /// <summary>
    /// Teaches the WinForms MCP bridge about DevExpress bar elements (bars and their item links).
    /// Compiled only when WINFORMS_MCP_INSTRUMENTED is defined; every member runs on the UI thread. It never creates
    /// window handles and keeps no references to the elements it reports.
    /// </summary>
    internal sealed class DevExpressBarsAdapter : ElementAdapter
    {
        internal const string AdapterName = "devexpress.bars";

        internal DevExpressBarsAdapter()
            : base(AdapterName)
        {
        }

        /// <summary>
        /// Bars are reachable through a dock control or a ribbon of their manager: BarManager.Bars is
        /// listed under every visited dock control and ribbon that carries the manager (the bridge keeps
        /// the first placement of an element it reaches twice). A manager with no dock control or ribbon
        /// is not reachable. Transient bar controls (docked, floating, submenu bar controls) are never
        /// parents: they come and go and would leave every bar stale.
        /// </summary>
        public override void GetChildren(object parent, ElementSink sink)
        {
            // The node of an item is its link, not the item: only the link carries bounds, the effective
            // enabled state and the open state, and one item may have several links.
            if (parent is Bar parentBar)
            {
                AddLinks(parentBar.ItemLinks, sink);
                return;
            }
            if (parent is BarSubItemLink subLink)
            {
                BarLinkContainerItem container = subLink.Item as BarLinkContainerItem;
                if (container != null) AddLinks(container.ItemLinks, sink);
                return;
            }

            BarManager manager = ManagerOf(parent);
            if (manager == null) return;
            foreach (Bar bar in manager.Bars)
            {
                if (!sink.Add(bar)) return;
            }
        }

        private static void AddLinks(BarItemLinkCollection links, ElementSink sink)
        {
            foreach (BarItemLink link in links)
            {
                // A disposed link has no item left and is not a usable element.
                if (link.Item == null) continue;
                if (!sink.Add(link)) return;
            }
        }

        private static BarManager ManagerOf(object parent)
        {
            // BarDockControl also covers StandaloneBarDockControl, which derives from it.
            if (parent is BarDockControl dockControl) return dockControl.Manager;
            if (parent is RibbonControl ribbon) return ribbon.Manager;
            return null;
        }

        /// <summary>
        /// A bar whose manager was released (disposed or removed) is stale; so is a link whose item was
        /// released (a disposed link). A link only removed from its collection is caught by the bridge's
        /// membership check of the parent.
        /// </summary>
        public override bool IsAlive(object element)
        {
            if (element is Bar bar) return bar.Manager != null;
            if (element is BarItemLink link) return link.Item != null;
            return false;
        }

        /// <summary>
        /// A bar's observed parent (the dock control it was listed under) is the anchor. A link of a bar
        /// has the bar as live parent; a link inside a submenu has none (its Bar is null), so the bridge
        /// uses the observed parent, the submenu link.
        /// </summary>
        public override object GetParent(object element)
        {
            if (element is BarItemLink link) return link.Bar;
            return null;
        }

        public override void Describe(object element, ElementDescription description)
        {
            if (element is BarItemLink link)
            {
                DescribeLink(link, description);
                return;
            }
            if (!(element is Bar bar)) return;
            description.Role = "toolbar";
            description.Name = bar.BarName;
            description.Text = bar.Text;
            description.Enabled = true;
            description.Available = bar.Visible;
            description.Visible = bar.Visible;
            description.ChildCount = bar.ItemLinks.Count;
            description.Opened = null;
        }

        private static void DescribeLink(BarItemLink link, ElementDescription description)
        {
            BarItem item = link.Item;
            if (item == null) return;

            BarSubItemLink subLink = link as BarSubItemLink;
            BarLinkContainerItem container = subLink == null ? null : item as BarLinkContainerItem;

            description.Role = RoleOf(item);
            description.Name = item.Name;
            description.Text = link.Caption;
            BarShortcut shortcut = item.ItemShortcut;
            if (shortcut != null && shortcut.IsExist) description.Shortcut = shortcut.ToString();
            description.Enabled = link.Enabled;
            // Available means configured to show: the item is visible at runtime and the link is visible.
            bool available = link.Visible && IsRuntimeVisible(item);
            description.Available = available;
            // Visible means realized on screen now. The layout of a link (Bounds) outlives its popup or bar
            // control in DevExpress, so it is only trusted while the host is shown.
            System.Drawing.Rectangle bounds = link.ScreenBounds;
            bool realized = available && !bounds.IsEmpty && IsHostShown(link) && HasScreenCoordinates(link, bounds);
            description.Visible = realized;
            if (realized) description.ScreenBounds = bounds;

            BarCheckItem check = item as BarCheckItem;
            if (check != null) description.Checked = check.Checked;

            if (subLink != null)
            {
                description.Opened = subLink.Opened;
                if (container != null) description.ChildCount = container.ItemLinks.Count;
            }

            if (HasClickCapability(item)) description.AddCapability("click");
            if (container != null && container.ItemLinks.Count > 0)
            {
                description.AddCapability("open");
                description.AddCapability("close");
            }
        }

        private static bool HasClickCapability(BarItem item)
        {
            return item is BarButtonItem || item is BarCheckItem;
        }

        /// <summary>
        /// The bridge checks capability, enabled state, availability and (for open/close) realization from a
        /// fresh Describe before it calls this. The guards here only keep a forced call safe: they refuse an
        /// unsupported action, a disabled link (click, open) and an open without on-screen layout. There is
        /// no guard for hidden or unavailable items; that refusal (hidden_control) is the bridge's alone.
        /// Never calls ShowPopup/HidePopup and never creates a handle.
        /// </summary>
        public override ElementActionOutcome Execute(object element, string action)
        {
            BarItemLink link = element as BarItemLink;
            BarItem item = link == null ? null : link.Item;
            if (item == null) throw Unsupported(action);

            switch (action)
            {
                case "click":
                    if (!HasClickCapability(item)) throw Unsupported(action);
                    // PerformClick only looks at the item's own Enabled; the link folds in its holder.
                    if (!link.Enabled) throw new ElementActionException(ElementActionError.Disabled, "The item is disabled.");
                    item.PerformClick(link);
                    // A postponed handler is queued on the manager's form and may not have run yet.
                    return item.ItemClickFireMode == BarItemEventFireMode.Postponed
                        ? ElementActionOutcome.HandlerDeferred
                        : ElementActionOutcome.HandlerReturned;

                case "open":
                {
                    BarSubItemLink subLink = OpenableLink(link, action);
                    if (!subLink.CanOpenMenu || subLink.ScreenBounds.IsEmpty)
                    {
                        if (!subLink.Enabled) throw new ElementActionException(ElementActionError.Disabled, "The menu is disabled.");
                        if (subLink.ScreenBounds.IsEmpty)
                            throw new ElementActionException(ElementActionError.NotRealized, "The menu has no on-screen layout; open its parent menu or show the window first.");
                        throw Unsupported(action);
                    }
                    subLink.OpenMenu();
                    return ElementActionOutcome.HandlerReturned;
                }

                case "close":
                    OpenableLink(link, action).CloseMenu();
                    return ElementActionOutcome.HandlerReturned;

                default:
                    throw Unsupported(action);
            }
        }

        private static BarSubItemLink OpenableLink(BarItemLink link, string action)
        {
            BarSubItemLink subLink = link as BarSubItemLink;
            BarLinkContainerItem container = subLink == null ? null : subLink.Item as BarLinkContainerItem;
            if (container == null || container.ItemLinks.Count == 0) throw Unsupported(action);
            return subLink;
        }

        private static ElementActionException Unsupported(string action)
        {
            return new ElementActionException(ElementActionError.Unsupported, "The element does not support the action '" + action + "'.");
        }

        private static bool IsRuntimeVisible(BarItem item)
        {
            return item.Visibility == BarItemVisibility.Always || item.Visibility == BarItemVisibility.OnlyInRuntime;
        }

        /// <summary>
        /// Whether what hosts the link is on screen now. A link of a bar needs a visible bar and a shown host
        /// form (or user control); a link of a submenu needs one of its owner's links to have the popup open.
        /// Floating bars are best effort (judged by the manager's host).
        /// </summary>
        private static bool IsHostShown(BarItemLink link)
        {
            Bar bar = link.Bar;
            if (bar != null)
            {
                if (!bar.Visible) return false;
            }
            else
            {
                BarLinkContainerItem owner = link.OwnerItem as BarLinkContainerItem;
                if (owner != null) return IsPopupOpen(owner);
            }
            BarManager manager = link.Manager;
            Control host = manager == null ? null : manager.Form;
            return host != null && host.IsHandleCreated && host.Visible;
        }

        private static bool IsPopupOpen(BarLinkContainerItem owner)
        {
            foreach (BarItemLink ownerLink in owner.Links)
            {
                BarSubItemLink subLink = ownerLink as BarSubItemLink;
                if (subLink != null && subLink.Opened) return true;
            }
            return false;
        }

        /// <summary>
        /// ScreenBounds returns the layout rectangle unconverted when the link has no control or the control
        /// has no handle; equal locations therefore mean the coordinates are not screen coordinates (a host
        /// whose client origin is exactly the screen origin is wrongly judged not visible, the safe side).
        /// </summary>
        private static bool HasScreenCoordinates(BarItemLink link, System.Drawing.Rectangle screenBounds)
        {
            return screenBounds.Location != link.Bounds.Location;
        }

        private static string RoleOf(BarItem item)
        {
            if (item is BarSubItem) return "menu_item";
            if (item is BarCheckItem) return "check_item";
            if (item is BarButtonItem) return "button"; // includes BarLargeButtonItem
            if (item is BarEditItem) return "edit_item";
            if (item is BarStaticItem) return "label";
            return "item";
        }
    }
}
#endif
