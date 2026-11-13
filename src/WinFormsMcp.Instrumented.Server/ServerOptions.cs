using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Server;

internal sealed class ServerOptions
{
    public string PipeName { get; private set; } = "wfmi-" + Guid.NewGuid().ToString("N");
    public string Token { get; private set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string? ApplicationPath { get; private set; }
    public string? WorkingDirectory { get; private set; }
    public List<string> ApplicationArguments { get; } = new();

    public static ServerOptions Parse(string[] args)
    {
        var options = new ServerOptions();
        string? tokenEnvironmentVariable = null;
        bool pipeSpecified = false;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument == "--")
            {
                for (i++; i < args.Length; i++) options.ApplicationArguments.Add(args[i]);
                break;
            }

            if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + argument);
            string value = args[++i];
            switch (argument)
            {
                case "--app": options.ApplicationPath = Path.GetFullPath(value); break;
                case "--working-directory": options.WorkingDirectory = Path.GetFullPath(value); break;
                case "--pipe": options.PipeName = value; pipeSpecified = true; break;
                case "--token-env": tokenEnvironmentVariable = value; break;
                default: throw new ArgumentException("Unknown argument: " + argument);
            }
        }

        if (!Regex.IsMatch(options.PipeName, @"\A[A-Za-z0-9_.-]{1,128}\z"))
            throw new ArgumentException("Pipe name must contain 1–128 letters, digits, dots, underscores, or hyphens.");

        if (tokenEnvironmentVariable != null)
        {
            options.Token = Environment.GetEnvironmentVariable(tokenEnvironmentVariable)
                ?? throw new ArgumentException("The token environment variable is not set.");
            if (options.Token.Length < 32)
                throw new ArgumentException("The configured token must contain at least 32 characters.");
        }

        if (options.ApplicationPath == null && (!pipeSpecified || tokenEnvironmentVariable == null))
            throw new ArgumentException("Use --app PATH, or --pipe NAME together with --token-env VARIABLE to attach.");
        if (options.ApplicationPath == null && options.ApplicationArguments.Count != 0)
            throw new ArgumentException("Application arguments require --app.");
        return options;
    }

    public const string Help = """
        WinForms MCP Instrumented — MCP stdio server

        winforms-mcp-instrumented --app C:\path\MyApp.exe [--working-directory DIR] [-- app arguments]
        winforms-mcp-instrumented --pipe NAME --token-env VARIABLE

        --app configures the executable used by app_launch/app_restart. It is not launched until requested.
        Launch mode creates a private pipe name and token and passes them through the child's environment.
        Attach mode reads an existing token from the named environment variable. Tokens are never CLI arguments.
        Use app_status to inspect availability. Closing or crashing the app does not close this MCP server.
        The server reserves stdout for MCP; its own logs go to stderr.
        """;
}
