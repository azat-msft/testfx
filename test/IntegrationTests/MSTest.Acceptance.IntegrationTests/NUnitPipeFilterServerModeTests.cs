// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Testing.Platform.Acceptance.IntegrationTests;
using Microsoft.Testing.Platform.ServerMode.IntegrationTests.Messages.V100;

using MSTest.Acceptance.IntegrationTests.Messages.V100;

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
    // Matches the capture: one plain test plus a [TestCase] per filter-operator character.
    private const string PipeTestUid = "PipeFilterRepro.Tests.PrintArg(\"as|\")";

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

namespace PipeFilterRepro;

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
