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
        using (WinFormsMcp.Instrumented.McpBridge.Start())
        {
            Application.Run(new MainForm());
        }
#else
        Application.Run(new MainForm());
#endif
    }
}
