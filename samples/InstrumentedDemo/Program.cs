using System;
using System.Windows.Forms;

namespace InstrumentedDemo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#if WINFORMS_MCP_INSTRUMENTED
        // What the bridge is taught about the demo (the grid adapter and the context menu action) is in DemoGridAdapter.RegisterWith.
        using (WinFormsMcp.Instrumented.McpBridge.Start(DemoGridAdapter.RegisterWith))
        {
            Application.Run(new MainForm());
        }
#else
        Application.Run(new MainForm());
#endif
    }
}
