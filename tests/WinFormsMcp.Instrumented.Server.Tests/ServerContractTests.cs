using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Server.Tests;

/// <summary>Portable checks of sidecar behavior that does not need a running application.</summary>
[TestClass]
public sealed class ServerContractTests
{
    [TestMethod]
    public void BridgeErrorsUseTheDocumentedCamelCaseKeys()
    {
        // Regression: serializing RpcError directly produced Code/Message/Details, so an error from
        // the bridge looked different from one produced by the sidecar itself.
        JObject error = ApplicationSession.ErrorObject(new RpcError
        {
            Code = "stale_target",
            Message = "The observed control no longer exists.",
            Details = new JObject { ["delivery"] = "not_sent" }
        });

        CollectionAssert.AreEqual(new[] { "code", "message", "details" }, error.Properties().Select(p => p.Name).ToArray());
        Assert.AreEqual("stale_target", (string?)error["code"]);
        Assert.AreEqual("not_sent", (string?)error["details"]!["delivery"]);
    }

    [TestMethod]
    public void BridgeAndSidecarErrorsHaveTheSameShape()
    {
        JObject sidecar = (JObject)ApplicationSession.Error("not_configured", "m")["error"]!;
        JObject bridge = ApplicationSession.ErrorObject(new RpcError { Code = "stale_target", Message = "m" });

        foreach (string key in new[] { "code", "message" })
        {
            Assert.IsNotNull(sidecar[key], "sidecar error lacks " + key);
            Assert.IsNotNull(bridge[key], "bridge error lacks " + key);
        }
    }

    [TestMethod]
    public void ErrorWithoutDetailsSerializesDetailsAsNull()
    {
        JObject error = ApplicationSession.ErrorObject(new RpcError { Code = "c", Message = "m" });
        Assert.AreEqual(JTokenType.Null, error["details"]!.Type);
    }

    [TestMethod]
    public void LaunchModeRequiresNoPipeOrToken()
    {
        ServerOptions options = ServerOptions.Parse(new[] { "--app", @"C:\apps\demo.exe" });
        StringAssert.StartsWith(options.PipeName, "wfmi-");
        Assert.AreEqual(64, options.Token.Length);
    }

    [TestMethod]
    public void AttachModeNeedsBothAPipeAndATokenVariable()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "demo-pipe" }));
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--token-env", "SOME_VARIABLE" }));
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(Array.Empty<string>()));
    }

    [TestMethod]
    public void ATokenFromTheEnvironmentMustBeLongEnoughAndIsNeverAnArgument()
    {
        const string variable = "WFMI_TEST_TOKEN_SERVER_CONTRACT";
        try
        {
            Environment.SetEnvironmentVariable(variable, "too-short");
            Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "p", "--token-env", variable }));

            string token = new('a', 32);
            Environment.SetEnvironmentVariable(variable, token);
            ServerOptions options = ServerOptions.Parse(new[] { "--pipe", "p", "--token-env", variable });
            Assert.AreEqual(token, options.Token);
            Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "p", "--token", token }));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [TestMethod]
    public void PipeNamesAreRestrictedToASafeAlphabet()
    {
        foreach (string bad in new[] { @"a\b", "a/b", "a b", "", new string('x', 129) })
            Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--app", @"C:\a.exe", "--pipe", bad }), bad);
    }

    [TestMethod]
    public void ApplicationArgumentsRequireALaunchableApplication()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "p", "--token-env", "PATH", "--", "x" }));
        ServerOptions options = ServerOptions.Parse(new[] { "--app", @"C:\a.exe", "--", "--flag", "value" });
        CollectionAssert.AreEqual(new[] { "--flag", "value" }, options.ApplicationArguments);
    }
}
