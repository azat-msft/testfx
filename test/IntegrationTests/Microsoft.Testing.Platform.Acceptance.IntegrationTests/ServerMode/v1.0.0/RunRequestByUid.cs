// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Newtonsoft.Json;

namespace Microsoft.Testing.Platform.ServerMode.IntegrationTests.Messages.V100;

/// <summary>
/// A <c>testing/runTests</c> request that selects tests by node UID, exactly like Visual Studio Test
/// Explorer does (see the captured traffic in <c>investigation/mtp-pipe-filter/captures</c>). The
/// server turns <see cref="Tests"/> into a <c>TestNodeUidListFilter</c>.
/// </summary>
public sealed record RunRequestByUid(
    [property: JsonProperty("runId")]
    Guid RunId,

    [property: JsonProperty("tests")]
    RunRequestTestNode[] Tests);

public sealed record RunRequestTestNode(
    [property: JsonProperty("uid")]
    string Uid,

    [property: JsonProperty("display-name")]
    string DisplayName,

    [property: JsonProperty("node-type")]
    string NodeType = "action");
