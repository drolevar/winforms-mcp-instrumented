using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WinFormsMcp.Instrumented.Server;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(x => x == "--help" || x == "-h"))
        {
            Console.Error.WriteLine(ServerOptions.Help);
            return 0;
        }
        ServerOptions options;
        try { options = ServerOptions.Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(ServerOptions.Help);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(logging => logging.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ApplicationSession>();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<WinFormsTools>();
        using IHost host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
