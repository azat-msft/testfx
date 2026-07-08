// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Testing.Platform.Acceptance.IntegrationTests;
using Microsoft.Testing.Platform.ServerMode.IntegrationTests.Messages.V100;

using MSTest.Acceptance.IntegrationTests.Messages.V100;

using Newtonsoft.Json.Linq;

namespace MSTest.Acceptance.IntegrationTests;

/// <summary>
/// End-to-end regression for the report that Visual Studio Test Explorer silently drops an
/// MTP test whose name contains the filter-operator character '|' (see
/// <c>investigation/mtp-pipe-filter/HANDOFF.md</c> and the captured server-protocol traffic in
/// <c>investigation/mtp-pipe-filter/captures</c>).
///
/// <para>
/// The test drives a real NUnit-on-Microsoft.Testing.Platform host over the server protocol exactly
/// like Test Explorer: it discovers the tests, then sends a single <c>testing/runTests</c> selecting
/// every discovered test <b>by node UID</b> (no CLI <c>--filter</c>). It then asserts that every
/// selected test — including <c>PrintArg("as|")</c>, whose UID contains a literal '|' — actually runs
/// and reports a terminal result, instead of being dropped by the VSTestBridge filter reconstruction.
/// </para>
///
/// <para>
/// NUnit is used (rather than MSTest) because it is the framework from the original report and because
/// NUnit's bridge uses the fully-qualified name — which encodes the <c>[TestCase]</c> argument, hence
/// the '|' — as the test-node UID, whereas MSTest uses opaque GUID UIDs that never contain filter
/// operators. The asset pins this repository's locally-built Microsoft.Testing.Platform packages so
/// the test exercises the in-repo VSTestBridge code, not whatever version NUnit happens to ship.
/// </para>
/// </summary>
[TestClass]
public sealed class NUnitPipeFilterServerModeTests : ServerModeTestsBase<NUnitPipeFilterServerModeTests.TestAssetFixture>
{
    // Matches the capture (investigation/mtp-pipe-filter/captures): the asset namespace is
    // 'ticket_11115502' so the discovered UIDs are byte-for-byte identical to the ones VS Test
    // Explorer sent, letting the raw-JSON-RPC test replay the captured payload verbatim.
    private const string PipeTestUid = "ticket_11115502.Tests.PrintArg(\"as|\")";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunSelectedTestsByUid_WithNameContainingPipe_RunsThePipeTest()
    {
        using TestingPlatformClient jsonClient = await StartAsServerAndConnectToTheClientAsync(
            TestHost.LocateFrom(AssetFixture.ProjectPath, TestAssetFixture.ProjectName, "net8.0", buildConfiguration: BuildConfiguration.Release));

        await jsonClient.Initialize();

        // 1) Discover every test, capturing the leaf ("action") node UIDs, exactly like Test Explorer
        //    populates its list before the user selects and runs them.
        TestNodeUpdateCollector discoveryCollector = new();
        ResponseListener discoveryListener = await jsonClient.DiscoverTests(Guid.NewGuid(), discoveryCollector.CollectNodeUpdates);
        await discoveryListener.WaitCompletion();

        RunRequestTestNode[] selectedTests = discoveryCollector.TestNodeUpdates
            .Where(u => u.Node.NodeType == "action")
            .Select(u => new RunRequestTestNode(u.Node.Uid, u.Node.DisplayName))
            // A UID can appear more than once in discovery updates; select each once.
            .GroupBy(n => n.Uid)
            .Select(g => g.First())
            .ToArray();

        Assert.Contains(n => n.Uid == PipeTestUid, selectedTests, $"The pipe test was not discovered. Discovered: {string.Join(", ", selectedTests.Select(t => t.Uid))}");

        // 2) Run the exact selection by UID (the Test Explorer "run selected/all" path).
        TestNodeUpdateCollector runCollector = new();
        ResponseListener runListener = await jsonClient.RunTestsByUid(Guid.NewGuid(), selectedTests, runCollector.CollectNodeUpdates);
        await runListener.WaitCompletion();

        // 3) Every selected test must actually run. A dropped test produces no result node at all.
        string[] executedUids = runCollector.TestNodeUpdates
            .Where(u => u.Node.NodeType == "action" && IsTerminalState(u.Node.ExecutionState))
            .Select(u => u.Node.Uid)
            .Distinct()
            .ToArray();

        Assert.Contains(
            PipeTestUid,
            executedUids,
            $"The test whose name contains '|' was silently dropped and never ran. Selected {selectedTests.Length} tests, executed: {string.Join(", ", executedUids)}");

        // Sanity: none of the other operator-character tests were dropped either.
        foreach (RunRequestTestNode selected in selectedTests)
        {
            Assert.Contains(
                selected.Uid,
                executedUids,
                $"Selected test was dropped and never ran: {selected.Uid}. Executed: {string.Join(", ", executedUids)}");
        }

        await jsonClient.Exit();
    }

    [TestMethod]
    public async Task RunTests_ReplayingRawCapturedJsonRpc_ParsesEscapedUidsAndRunsThePipeTest()
    {
        // This variant does NOT build the request object model; it sends the *exact* JSON-RPC bytes that
        // Visual Studio Test Explorer sent in the captured session
        // (investigation/mtp-pipe-filter/captures/capture-20260703-193541-82532.log), including its JSON
        // unicode escapes (\u0022 for '"', \u0026 for '&') and the literal '|'. This exercises the
        // server's own JSON parse/unescape path for the test-node UIDs, end to end over the socket.
        using RawTestingPlatformClient client = await StartAsServerAndConnectRawAsync(
            TestHost.LocateFrom(AssetFixture.ProjectPath, TestAssetFixture.ProjectName, "net8.0", buildConfiguration: BuildConfiguration.Release));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // 1) initialize handshake (id = 2, matching the capture). Use the real process id so the
        //    server's client-liveness monitoring stays valid for the duration of the run.
        string initJson = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"processId\":"
            + Environment.ProcessId
            + ",\"clientInfo\":{\"name\":\"raw-capture-client\",\"version\":\"1.0.0\"},\"capabilities\":{\"testing\":{\"debuggerProvider\":false}}}}";
        await client.SendRawAsync(initJson, cts.Token);
        await ReadUntilResponseAsync(client, id: 2, cts.Token);

        // 2) The captured testing/runTests frame, byte-for-byte (7 tests selected by UID). Note the
        //    \u0022 / \u0026 escapes and the literal '|' inside PrintArg("as|").
        const string CapturedRunTestsJson = """{"jsonrpc":"2.0","id":3,"method":"testing/runTests","params":{"runId":"86ff55d1-fcbe-4377-ad33-afbf1e8f8955","tests":[{"uid":"ticket_11115502.Tests.Test1","display-name":"Test1","node-type":"Action"},{"uid":"ticket_11115502.Tests.PrintArg(\u0022as!\u0022)","display-name":"PrintArg(\u0022as!\u0022)","node-type":"Action"},{"uid":"ticket_11115502.Tests.PrintArg(\u0022as\u0022)","display-name":"PrintArg(\u0022as\u0022)","node-type":"Action"},{"uid":"ticket_11115502.Tests.PrintArg(\u0022as\u0026\u0022)","display-name":"PrintArg(\u0022as\u0026\u0022)","node-type":"Action"},{"uid":"ticket_11115502.Tests.PrintArg(\u0022as=\u0022)","display-name":"PrintArg(\u0022as=\u0022)","node-type":"Action"},{"uid":"ticket_11115502.Tests.PrintArg(\u0022as|\u0022)","display-name":"PrintArg(\u0022as|\u0022)","node-type":"Action"},{"uid":"ticket_11115502.Tests.PrintArg(\u0022as~\u0022)","display-name":"PrintArg(\u0022as~\u0022)","node-type":"Action"}]}}""";
        await client.SendRawAsync(CapturedRunTestsJson, cts.Token);

        // 3) Collect terminal results until the run response (id = 3) arrives.
        var executedUids = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            JObject? message = await client.ReadMessageAsync(cts.Token);
            Assert.IsNotNull(message, "Server closed the connection before responding to testing/runTests.");

            if (message["id"]?.Type == JTokenType.Integer && message["id"]!.Value<int>() == 3 && message["result"] is not null)
            {
                break;
            }

            if ((string?)message["method"] == "testing/testUpdates/tests"
                && message["params"]?["changes"] is JArray changes)
            {
                foreach (JToken change in changes)
                {
                    JToken? node = change["node"];
                    if ((string?)node?["uid"] is string uid
                        && (string?)node["node-type"] == "action"
                        && IsTerminalState((string?)node["execution-state"]))
                    {
                        executedUids.Add(uid);
                    }
                }
            }
        }

        // 4) Every UID from the captured payload must have run — the escapes must round-trip through the
        //    server's deserializer and the VSTestBridge filter. The pipe one is the whole point.
        string[] expectedUids =
        [
            "ticket_11115502.Tests.Test1",
            "ticket_11115502.Tests.PrintArg(\"as!\")",
            "ticket_11115502.Tests.PrintArg(\"as\")",
            "ticket_11115502.Tests.PrintArg(\"as&\")",
            "ticket_11115502.Tests.PrintArg(\"as=\")",
            "ticket_11115502.Tests.PrintArg(\"as|\")",
            "ticket_11115502.Tests.PrintArg(\"as~\")",
        ];

        Assert.Contains(
            PipeTestUid,
            executedUids,
            $"The test whose name contains '|' was silently dropped. Executed: {string.Join(", ", executedUids)}");

        foreach (string expected in expectedUids)
        {
            Assert.Contains(
                expected,
                executedUids,
                $"Selected test was dropped and never ran: {expected}. Executed: {string.Join(", ", executedUids)}");
        }

        await client.ExitAsync(cts.Token);
    }

    private static async Task ReadUntilResponseAsync(RawTestingPlatformClient client, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            JObject? message = await client.ReadMessageAsync(cancellationToken);
            Assert.IsNotNull(message, $"Server closed the connection before responding to request id {id}.");

            if (message["id"]?.Type == JTokenType.Integer && message["id"]!.Value<int>() == id && message["result"] is not null)
            {
                return;
            }
        }
    }

    private static bool IsTerminalState(string? executionState)
        => executionState is "passed" or "failed" or "skipped" or "error" or "cancelled" or "timeout";

    public sealed class TestAssetFixture : TestAssetFixtureBase
    {
        public const string ProjectName = "NUnitPipeFilterRepro";

        public string ProjectPath => GetAssetPath(ProjectName);

        // The MSTest source generator is irrelevant to an NUnit asset (and its metadata hook is not
        // present here), so opt out of the default SourceGeneration build variant.
        protected override IReadOnlyList<MetadataMode> SourceGenMetadataModes => [];

        public override (string ID, string Name, string Code) GetAssetsToGenerate() => (ProjectName, ProjectName,
            Sources
                .PatchCodeWithReplace("$MicrosoftTestingPlatformVersion$", MicrosoftTestingPlatformVersion));

        private const string Sources = """
#file NUnitPipeFilterRepro.csproj
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>

    <!-- Run on Microsoft.Testing.Platform as a self-contained executable so the host can be
         launched in server mode, mirroring Visual Studio Test Explorer. -->
    <EnableNUnitRunner>true</EnableNUnitRunner>
    <OutputType>Exe</OutputType>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
    <GenerateProgramFile>false</GenerateProgramFile>
    <NoWarn>$(NoWarn);NETSDK1201;NU1507</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.6.1" />
    <PackageReference Include="NUnit3TestAdapter" Version="6.2.0" />

    <!-- Pin this repository's locally-built MTP packages so the test exercises the in-repo
         VSTestBridge, overriding NUnit3TestAdapter's transitive '[2.1.0, )' references. -->
    <PackageReference Include="Microsoft.Testing.Platform" Version="$MicrosoftTestingPlatformVersion$" />
    <PackageReference Include="Microsoft.Testing.Platform.MSBuild" Version="$MicrosoftTestingPlatformVersion$" />
    <PackageReference Include="Microsoft.Testing.Extensions.VSTestBridge" Version="$MicrosoftTestingPlatformVersion$" />
  </ItemGroup>

</Project>

#file UnitTest1.cs
using NUnit.Framework;

namespace ticket_11115502;

public class Tests
{
    [Test]
    public void Test1()
    {
    }

    // Each [TestCase] argument is encoded verbatim into the test's fully-qualified name (and thus its
    // MTP node UID), so 'PrintArg("as|")' has a literal '|' — a TestCaseFilter OR operator — in its UID.
    [TestCase("as!")]
    [TestCase("as")]
    [TestCase("as&")]
    [TestCase("as=")]
    [TestCase("as|")]
    [TestCase("as~")]
    public void PrintArg(string arg)
    {
    }
}
""";
    }
}
