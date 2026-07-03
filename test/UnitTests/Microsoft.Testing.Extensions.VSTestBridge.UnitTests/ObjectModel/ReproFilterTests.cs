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

[TestClass]
public sealed class ReproFilterTests
{
    [TestMethod]
    public void Repro_PipeTest_IsDropped()
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

        Console.WriteLine("FILTER=[" + expr.TestCaseFilterValue + "]");

        foreach (string uid in uids)
        {
            var testCase = new TestCase(uid, new Uri("executor://nunit"), "asm.dll");
            bool matched = expr.MatchTestCase(testCase, prop => prop == "FullyQualifiedName" ? uid : null);
            Console.WriteLine($"MATCH[{matched}] {uid}");
        }
    }
}
