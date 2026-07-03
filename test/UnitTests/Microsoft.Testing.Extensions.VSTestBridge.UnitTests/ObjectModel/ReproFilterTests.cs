// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Testing.Extensions.VSTestBridge.ObjectModel;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;

using Moq;

namespace Microsoft.Testing.Extensions.VSTestBridge.UnitTests.ObjectModel;

/// <summary>
/// End-to-end regression for the "Test Explorer drops the test whose name contains '|'" report
/// (see investigation/mtp-pipe-filter/HANDOFF.md). Reconstructs the exact UID selection captured from
/// a real VS Test Explorer run and verifies that every selected node — including the ones whose names
/// contain filter operator characters (<c>! &amp; = | ~</c>) — round-trips through
/// <see cref="ContextAdapterBase"/> and matches its own test case.
/// </summary>
[TestClass]
public sealed class ReproFilterTests
{
    [TestMethod]
    public void GetTestCaseFilter_WithNamesContainingOperatorCharacters_MatchesEveryNode()
    {
        string[] uids =
        [
            "ticket_11115502.Tests.Test1",
            "ticket_11115502.Tests.PrintArg(\"as!\")",
            "ticket_11115502.Tests.PrintArg(\"as\")",
            "ticket_11115502.Tests.PrintArg(\"as&\")",
            "ticket_11115502.Tests.PrintArg(\"as=\")",
            "ticket_11115502.Tests.PrintArg(\"as|\")",
            "ticket_11115502.Tests.PrintArg(\"as~\")",
        ];

        var runSettings = new Mock<IRunSettings>();
        runSettings.Setup(x => x.SettingsXml).Returns("<RunSettings><RunConfiguration></RunConfiguration></RunSettings>");
        var cmd = new Mock<ICommandLineOptions>();

        var filter = new TestNodeUidListFilter([.. uids.Select(u => new TestNodeUid(u))]);
        var adapter = new RunContextAdapter(cmd.Object, runSettings.Object, filter);

        ITestCaseFilterExpression? expr = adapter.GetTestCaseFilter(null, _ => null);
        Assert.IsNotNull(expr);

        foreach (string uid in uids)
        {
            var testCase = new TestCase(uid, new Uri("executor://nunit"), "asm.dll");
            bool matched = expr.MatchTestCase(testCase, prop => prop == "FullyQualifiedName" ? uid : null);
            Assert.IsTrue(matched, $"Selected node was dropped by the reconstructed filter: {uid}");
        }
    }
}
