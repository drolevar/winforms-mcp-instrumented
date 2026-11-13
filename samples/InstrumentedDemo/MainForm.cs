using System;
using System.Drawing;
using System.Windows.Forms;

namespace InstrumentedDemo;

internal sealed class MainForm : Form
{
    private readonly Label _status = new() { Name = "lblStatus", Text = "Ready", AutoSize = true, Padding = new Padding(6) };
    private readonly FlowLayoutPanel _dynamicLeft = Panel("dynamicLeft");
    private readonly FlowLayoutPanel _dynamicRight = Panel("dynamicRight");
    private ProbeTextBox _dynamicEditor = null!;
    private int _replacementCount;

    public MainForm()
    {
        Name = "MainForm";
        Text = "WinForms MCP Instrumented — sample application";
        Font = new Font("Segoe UI", 10F);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 690);
        MinimumSize = new Size(700, 450);

        var tabs = new TabControl { Name = "mainTabs", Dock = DockStyle.Fill };
        tabs.TabPages.Add(StandardControls());
        tabs.TabPages.Add(DynamicControls());
        tabs.TabPages.Add(GridControls());
        tabs.TabPages.Add(ScrollingControls());
        Controls.Add(tabs);
        var footer = new Panel { Name = "statusPanel", Dock = DockStyle.Bottom, Height = 42 };
        footer.Controls.Add(_status);
        Controls.Add(footer);
    }

    private TabPage StandardControls()
    {
        var page = new TabPage("Standard controls") { Name = "tabStandard" };
        FlowLayoutPanel panel = Panel("standardPanel");
        panel.Dock = DockStyle.Fill;
        page.Controls.Add(panel);
        panel.Controls.Add(Note("Inspect this tree, set a value, and invoke a button. Controls need no individual registration."));
        var text = new TextBox { Name = "txtName", Text = "World", Width = 240 };
        panel.Controls.Add(text);
        panel.Controls.Add(Button("btnGreet", "Greet", () => _status.Text = "Hello, " + text.Text + "!"));
        panel.Controls.Add(new CheckBox { Name = "chkEnabled", Text = "An ordinary check box", AutoSize = true });
        panel.Controls.Add(new RadioButton { Name = "radioChoice", Text = "An ordinary radio button", AutoSize = true });
        panel.Controls.Add(new NumericUpDown { Name = "numericValue", Minimum = -100, Maximum = 100, DecimalPlaces = 2, Value = 12.5M, Width = 180 });
        panel.Controls.Add(new TrackBar { Name = "trackValue", Minimum = 0, Maximum = 100, Value = 25, Width = 240 });
        panel.Controls.Add(new DateTimePicker { Name = "dateValue", Width = 260, Value = new DateTime(2026, 1, 1) });
        var choices = new ComboBox { Name = "comboChoice", DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        choices.Items.AddRange(new object[] { "Alpha", "Beta", "Gamma" });
        choices.SelectedIndex = 0;
        panel.Controls.Add(choices);
        var list = new ListBox { Name = "listChoice", Width = 240, Height = 90 };
        list.Items.AddRange(new object[] { "First", "Second", "Third", "Fourth" });
        panel.Controls.Add(list);
        panel.Controls.Add(Button("btnOpenDialog", "Open a modal dialog", ShowModalDialog));
        panel.Controls.Add(new TextBox { Name = "txtPassword", UseSystemPasswordChar = true, Text = "demo-only-password", Width = 240 });
        panel.Controls.Add(Note("Password values are hidden in tree output. Screenshots show the actual screen; exclusions are not image redaction."));
        return page;
    }

    private TabPage DynamicControls()
    {
        var page = new TabPage("Dynamic identity") { Name = "tabDynamic" };
        var layout = new TableLayoutPanel { Name = "dynamicLayout", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var explanation = Note("The editor has a stable Name. Replacing its managed object must change its ID; recreating its native handle or moving the object must preserve its ID.");
        explanation.Dock = DockStyle.Fill;
        layout.Controls.Add(explanation, 0, 0); layout.SetColumnSpan(explanation, 2);
        var actions = new FlowLayoutPanel { Name = "dynamicActions", Dock = DockStyle.Fill };
        actions.Controls.Add(Button("btnReplaceEditor", "Replace object", ReplaceEditor));
        actions.Controls.Add(Button("btnRecreateHandle", "Recreate HWND", () => { _dynamicEditor.RecreateNativeHandle(); _status.Text = "Recreated the same object's HWND."; }));
        actions.Controls.Add(Button("btnMoveEditor", "Move object", () =>
        {
            Control destination = _dynamicEditor.Parent == _dynamicLeft ? _dynamicRight : _dynamicLeft;
            destination.Controls.Add(_dynamicEditor);
            _status.Text = "Reparented the same managed object.";
        }));
        layout.Controls.Add(actions, 0, 1); layout.SetColumnSpan(actions, 2);
        _dynamicLeft.Dock = DockStyle.Fill; _dynamicRight.Dock = DockStyle.Fill;
        _dynamicLeft.BorderStyle = BorderStyle.FixedSingle; _dynamicRight.BorderStyle = BorderStyle.FixedSingle;
        layout.Controls.Add(_dynamicLeft, 0, 2); layout.Controls.Add(_dynamicRight, 1, 2);
        page.Controls.Add(layout);
        ReplaceEditor();
        return page;
    }

    private TabPage GridControls()
    {
        var page = new TabPage("Grid and tree") { Name = "tabGrid" };
        var split = new SplitContainer { Name = "gridSplit", Dock = DockStyle.Fill };
        var grid = new DataGridView
        {
            Name = "dataGrid", Dock = DockStyle.Fill, AllowUserToAddRows = false,
            AllowUserToOrderColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add("number", "Number"); grid.Columns.Add("name", "Name"); grid.Columns.Add("value", "Value");
        for (int i = 0; i < 120; i++) grid.Rows.Add(i, "Item " + i, (i * 1.25).ToString("F2"));
        split.Panel1.Controls.Add(grid);
        var tree = new TreeView { Name = "treeItems", Dock = DockStyle.Fill };
        for (int i = 0; i < 4; i++)
        {
            var parent = new TreeNode("Group " + i) { Name = "group" + i };
            for (int j = 0; j < 6; j++) parent.Nodes.Add(new TreeNode("Item " + i + "." + j));
            tree.Nodes.Add(parent);
        }
        tree.ExpandAll(); split.Panel2.Controls.Add(tree); page.Controls.Add(split);
        return page;
    }

    private static TabPage ScrollingControls()
    {
        var page = new TabPage("Scrolling") { Name = "tabScroll" };
        var panel = new Panel { Name = "scrollPanel", Dock = DockStyle.Fill, AutoScroll = true };
        for (int i = 0; i < 45; i++)
            panel.Controls.Add(new TextBox { Name = "scrollText" + i, Text = "Row " + i, Location = new Point(20, 20 + i * 42), Width = 360 });
        page.Controls.Add(panel);
        return page;
    }

    private void ReplaceEditor()
    {
        Control parent = _dynamicEditor?.Parent ?? _dynamicLeft;
        _dynamicEditor?.Dispose();
        _dynamicEditor = new ProbeTextBox { Name = "txtDynamic", Text = "Replacement " + _replacementCount++, Width = 320 };
        parent.Controls.Add(_dynamicEditor);
        _status.Text = "Created a new managed editor with the same Name.";
    }

    private void ShowModalDialog()
    {
        using var dialog = new Form { Name = "DetailsDialog", Text = "Modal dialog", ClientSize = new Size(420, 180), StartPosition = FormStartPosition.CenterParent };
        var panel = Panel("dialogPanel"); panel.Dock = DockStyle.Fill;
        panel.Controls.Add(Note("The opening action is still running. Inspect this window and invoke btnCloseDialog to complete it."));
        var close = new Button { Name = "btnCloseDialog", Text = "Close dialog", AutoSize = true, DialogResult = DialogResult.OK };
        panel.Controls.Add(close); dialog.Controls.Add(panel); dialog.AcceptButton = close;
        dialog.ShowDialog(this);
        _status.Text = "Modal dialog closed; the opening action can complete.";
    }

    private static FlowLayoutPanel Panel(string name) => new()
    {
        Name = name, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Padding = new Padding(14)
    };
    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(3, 3, 3, 12) };
    private static Button Button(string name, string text, Action action)
    {
        var button = new Button { Name = name, Text = text, AutoSize = true, Padding = new Padding(6) };
        button.Click += (_, _) => action();
        return button;
    }
    private sealed class ProbeTextBox : TextBox
    {
        public void RecreateNativeHandle() => RecreateHandle();
    }
}
