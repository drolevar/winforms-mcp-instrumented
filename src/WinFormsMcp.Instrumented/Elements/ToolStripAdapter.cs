using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Forms;

namespace WinFormsMcp.Instrumented.Elements;

/// <summary>
/// Built-in adapter for <see cref="ToolStripItem"/> trees (MenuStrip, ToolStrip, StatusStrip and context
/// menus). It never touches <c>DropDown</c> or <c>DropDownItems</c> of an item that has no dropdown yet, because
/// that would create one, and never creates a window handle.
/// </summary>
internal sealed class ToolStripAdapter : ElementAdapter
{
    internal const string AdapterName = "toolstrip";

    internal ToolStripAdapter() : base(AdapterName)
    {
    }

    public override void GetChildren(object parent, ElementSink sink)
    {
        if (parent is ToolStrip strip)
        {
            foreach (ToolStripItem item in strip.Items)
                if (!sink.Add(item)) return;
        }
        else if (parent is ToolStripDropDownItem owner)
        {
            if (!owner.HasDropDown) return;
            foreach (ToolStripItem item in owner.DropDownItems)
                if (!sink.Add(item)) return;
        }
        if (parent is Control control && control.ContextMenuStrip != null)
            sink.Add(control.ContextMenuStrip);
    }

    public override bool IsAlive(object element)
        => element is ContextMenuStrip strip ? !strip.IsDisposed && !strip.Disposing
            : element is ToolStripItem item && !item.IsDisposed;

    public override object? GetParent(object element)
        => element is ToolStripItem item ? item.OwnerItem ?? (object?)item.Owner : null;

    public override void Describe(object element, ElementDescription description)
    {
        if (element is ContextMenuStrip strip)
        {
            description.Role = "context_menu";
            description.Name = strip.Name;
            description.Enabled = strip.Enabled;
            description.Visible = strip.Visible;
            description.Opened = strip.Visible;
            description.ChildCount = strip.Items.Count;
            description.AddCapability("close");
            return;
        }
        var item = (ToolStripItem)element;
        description.Name = item.Name;
        description.Enabled = item.Enabled;
        description.Available = item.Available;
        description.Visible = item.Visible;
        if (item is ToolStripControlHost host)
        {
            // The hosted control's text can be a password; the control itself is reported as a control node.
            description.Role = "host";
            description.Text = null;
            description.HostedControl = host.Control;
        }
        else
        {
            description.Text = item.Text;
            description.Role = item is ToolStripMenuItem ? "menu_item"
                : item is ToolStripSeparator ? "separator"
                : item is ToolStripLabel ? "label"
                : item is ToolStripButton || item is ToolStripSplitButton || item is ToolStripDropDownButton ? "button"
                : "unknown";
        }

        if (item is ToolStripMenuItem menuItem)
        {
            description.Shortcut = !string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString) ? menuItem.ShortcutKeyDisplayString
                : menuItem.ShortcutKeys != Keys.None
                    ? TypeDescriptor.GetConverter(typeof(Keys)).ConvertToString(null, CultureInfo.InvariantCulture, menuItem.ShortcutKeys)
                    : null;
            description.Checked = menuItem.Checked;
        }
        else if (item is ToolStripButton button) description.Checked = button.Checked;

        if (item is ToolStripMenuItem || item is ToolStripButton || item is ToolStripSplitButton || item is ToolStripDropDownButton)
            description.AddCapability("click");
        if (item is ToolStripDropDownItem dropDownItem && dropDownItem.HasDropDown)
        {
            description.Opened = dropDownItem.DropDown.Visible;
            description.ChildCount = dropDownItem.DropDownItems.Count;
            description.AddCapability("open");
            description.AddCapability("close");
        }

        ToolStrip? owner = item.Owner;
        if (owner != null && owner.IsHandleCreated && item.Visible)
            description.ScreenBounds = owner.RectangleToScreen(item.Bounds);
    }

    public override ElementActionOutcome Execute(object element, string action)
    {
        if (element is ContextMenuStrip strip && action == "close")
        {
            strip.Close();
            return ElementActionOutcome.HandlerReturned;
        }
        if (element is ToolStripItem item)
        {
            if (action == "click" && (item is ToolStripMenuItem || item is ToolStripButton ||
                                      item is ToolStripSplitButton || item is ToolStripDropDownButton))
            {
                // A split button's normal handler is ButtonClick, which PerformClick does not raise.
                if (item is ToolStripSplitButton split) split.PerformButtonClick();
                else item.PerformClick();
                return ElementActionOutcome.HandlerReturned;
            }
            if (item is ToolStripDropDownItem dropDownItem && dropDownItem.HasDropDown)
            {
                if (action == "open")
                {
                    dropDownItem.ShowDropDown();
                    return ElementActionOutcome.HandlerReturned;
                }
                if (action == "close")
                {
                    dropDownItem.HideDropDown();
                    return ElementActionOutcome.HandlerReturned;
                }
            }
        }
        throw new ElementActionException(ElementActionError.Unsupported, "The element does not support the requested action.");
    }
}
