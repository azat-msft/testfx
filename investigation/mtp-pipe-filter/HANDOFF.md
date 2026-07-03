# Handoff: Test Explorer silently drops MTP tests whose name contains `|` (VSTestBridge)

**Branch:** `investigate/mtp-pipe-filter-drop` (fork `azat-msft/testfx`)
**Date:** 2026-07-03
**Repo area:** `src/Platform/Microsoft.Testing.Extensions.VSTestBridge`

> **STATUS: RESOLVED — see the "Resolution (2026-07-03, second pass)" section below.**
> The earlier "the `|` value does not round-trip through the filter grammar" hypothesis was
> **disproven** by a runnable repro against the package version testfx actually references. The
> filter string is built and matched correctly for `|`. A *different*, real bug was found and fixed:
> a hand-rolled escaper in `BuildFilter` mixed the node index with the character index, throwing
> `IndexOutOfRangeException` (and under-escaping) for multi-node selections.

---

## Resolution (2026-07-03, second pass)

**What was verified (runnable, not hypothetical):**

1. Built `test/UnitTests/Microsoft.Testing.Extensions.VSTestBridge.UnitTests` and ran
   `RunContextAdapterFilterTests` + the repro. With the **exact 7 UIDs from the capture** and the
   Filter.Source package testfx references (`18.8.0-preview-26276-01`), the built filter is:

   ```
   (FullyQualifiedName=...Test1|FullyQualifiedName=...PrintArg\("as\!"\)|...|FullyQualifiedName=...PrintArg\("as\|"\)|...)
   ```

   and `MatchTestCase` returns **`true` for all seven nodes, including `PrintArg("as|")`**. So the
   `|` string round-trips correctly through `FilterExpressionWrapper`/`TestCaseFilterExpression` in
   the current package. The real-world drop observed in the capture came from an **older Filter.Source
   bundled with a *released* VSTestBridge** (e.g. the one NUnit's MTP runner ships), not from testfx
   `main`.

2. The actual defect in testfx `main` is in `ContextAdapterBase.BuildFilter`. The hand-rolled escaping
   loop guarded "is this char already escaped?" with `i - 1 < 0 || currentTestNodeUid.Value[k - 1] != '\\'`,
   mixing the **node index `i`** with the **character index `k`**. For any node after the first
   (`i > 0`) whose **first** character (`k == 0`) is a filter operator, this evaluates `Value[-1]` and
   throws `IndexOutOfRangeException`, aborting the whole run. It also under-escapes an operator that
   follows a literal backslash in a later node.

**The fix (committed):** replace the hand-rolled loop with VSTest's canonical
`FilterHelper.Escape(currentTestNodeUid.Value)` (already accessible via the imported
`Microsoft.VisualStudio.TestPlatform.Common.Filtering` namespace). It escapes every operator
unconditionally — correct for a raw UID — and removes the index bug entirely.

**Regression tests (all green, 59/59 on net8.0):**

- `RunContextAdapterFilterTests.GetTestCaseFilter_WithSecondNodeContainingPipe_KeepsBothNodesAndEscapesPipe`
- `RunContextAdapterFilterTests.GetTestCaseFilter_WithSecondNodeStartingWithSpecialCharacter_DoesNotThrowAndEscapes`
  (this one reproduced the `IndexOutOfRangeException` before the fix)
- `RunContextAdapterFilterTests.GetTestCaseFilter_WithBackslashFollowedBySpecialCharacter_EscapesBoth`
- `ReproFilterTests.GetTestCaseFilter_WithNamesContainingOperatorCharacters_MatchesEveryNode`
  (asserts every captured UID matches its own test case)

**Still open / optional follow-up:** an acceptance test driving the real Test Explorer server-protocol
path end-to-end (NUnit/MSTest-on-MTP asset selected by UID) would guard against future regressions in
the *shipped* bridge, but the unit-level regressions above cover the testfx code under change.

---

## Original TL;DR (first pass — superseded, kept for history)

When Visual Studio Test Explorer runs selected tests for an MTP-based project, it sends the selection
**by test-node UID over the JSON-RPC server protocol** (`testing/runTests`) — there is **no CLI
`--filter`**. The VSTestBridge layer reconstructs that selection into a **single classic VSTest
`TestCaseFilter` string** in `ContextAdapterBase.BuildFilter`, joining the UIDs with the `|` (OR)
operator and escaping in-name operator characters.

Escaping **is present** (contrary to the first handoff's guess) and works for `! & = ( ) ~`. The bug
is **specific to `|`**: a test whose name contains `|` is silently excluded from the run. All other
operator characters execute fine.

The most likely defect: reconstructing a **name-based** `FullyQualifiedName=...` filter from an
explicit UID selection is **lossy for `|`** — the escaped `\|` inside a `FullyQualifiedName` value does
not round-trip through VSTest's `FilterExpressionWrapper` / `TestCaseFilterExpression` grammar, so the
pipe node fails to match and is dropped before execution. UID-equality selection would avoid this
entirely.

---

## Decisive evidence: the captured proxy log

File (committed): `investigation/mtp-pipe-filter/captures/capture-20260703-193541-82532.log`

This is a man-in-the-middle capture of the MTP server-protocol traffic between VS Test Explorer and
the test host for the repro project `ticket-11115502` (NUnit on MTP, net10.0). Key frames:

1. **Host launched with `--server ... --client-port` and NO `--filter`** (line 2 / line 7). Selection
   travels over the port, not the command line.

2. **VS -> HOST `testing/runTests`** (line 23) selects **7 nodes by UID**:
   ```
   ticket_11115502.Tests.Test1
   ticket_11115502.Tests.PrintArg("as!")
   ticket_11115502.Tests.PrintArg("as")
   ticket_11115502.Tests.PrintArg("as&")   (\u0026 in JSON)
   ticket_11115502.Tests.PrintArg("as=")
   ticket_11115502.Tests.PrintArg("as|")    <-- the pipe one
   ticket_11115502.Tests.PrintArg("as~")
   ```
   (`\u0022` = `"`, `\u0026` = `&`. The `|` is transmitted literally as `|`.)

3. **HOST log** (line 48): `NUnit3TestExecutor discovered **6 of 6** ... Non-Explicit run` — only
   **6**, though **7** were requested. The filter dropped one before the executor saw it.

4. **Result nodes** (lines 53-55) report execution-state for exactly these six:
   `("as")`, `("as=")`, `("as!")`, `("as&")`, `("as~")`, and `Test1`.
   **`PrintArg("as|")` never appears** — no `in-progress`, no `passed`, no `failed`.

5. **Telemetry** (line 70): `"total passed":1,"total failed":5` (= 6 executed), `"filter enabled":"true"`.

Conclusion: `& = ! ~` all execute; **only `|` is silently excluded**, and the exclusion happens on the
filter/selection path inside the bridge, before the framework executor runs.

---

## Code under suspicion

`src/Platform/Microsoft.Testing.Extensions.VSTestBridge/ObjectModel/ContextAdapterBase.cs`

- `HandleFilter(...)` — detects `filter is TestNodeUidListFilter` and calls `BuildFilter`.
- `BuildFilter(TestNodeUid[] testNodesUid, StringBuilder filter)` — the conversion:
  - Joins selected nodes with `filter.Append('|')` as the **OR separator**.
  - For non-GUID nodes emits `FullyQualifiedName=<escaped-uid>`.
  - Escapes `\ ( ) & | = ! ~` in the name by prefixing `\`.
- `GetTestCaseFilter(...)` — wraps the built string in a `FilterExpressionWrapper` /
  `TestCaseFilterExpression`; throws `TestPlatformFormatException` on parse error. Called by the
  adapters (MSTest/NUnit) via reflection.

Consumed via `RunContextAdapter` -> `VSTestBridgedTestFrameworkBase.ExecuteRequestAsync`.

### Two concrete suspects (confirm which)

1. **Grammar asymmetry (primary):** VSTest's filter parser treats `|` as OR at the top level. Even
   when the in-value `|` is emitted as `\|`, the wrapper's value-unescaping/matching may not treat
   `\|` as a literal `|` inside a `FullyQualifiedName` value, so the constructed clause never matches
   the actual test case. (The first handoff's manual `dotnet run -- --filter 'PrintArg(as\|)'` ->
   *"Missing Operator '|' or '&'"* supports this.) Because `|` is ALSO the join operator between
   clauses, this is uniquely fragile for `|` versus `& = ! ~`.

2. **Index bug in the "already-escaped" guard** (`BuildFilter`, the `if (i - 1 < 0 || currentTestNodeUid.Value[k - 1] != '\\')`
   line): it mixes the **node index `i`** with the **character index `k`** (indexes `Value[k - 1]`
   while guarding on `i - 1`). This is at least a latent correctness/`IndexOutOfRangeException` risk
   for multi-node selections; verify whether it also contributes to the `|` drop.

**Preferred fix direction:** for an explicit UID selection, match by **test-node UID / `Id` equality**
instead of rebuilding a name-based `TestCaseFilter`. UID matching is already proven safe with `|`
(`--filter-uid '...PrintArg("as|")'` runs it). If a name filter must be kept, fix the `|`
escaping/round-trip and the `i`/`k` index guard, and add regression coverage.

---

## Tests added on this branch

`test/UnitTests/Microsoft.Testing.Extensions.VSTestBridge.UnitTests/ObjectModel/`

- **`RunContextAdapterFilterTests.cs`** — 11 MSTest tests exercising `BuildFilter` through the public
  `RunContextAdapter.GetTestCaseFilter(...).TestCaseFilterValue`: single/multiple `FullyQualifiedName`
  nodes, GUID -> `Id=`, mixed, special-character escaping (incl. the `|` case), runsettings
  `TestCaseFilter`, command-line `--filter`, and `&`-combination. **Verified passing on net8.0** in a
  previous run (11/11). NOTE: these currently assert the *current* (buggy) output string; once the fix
  lands, the `|` expectations must be updated to reflect correct behavior.

- **`ReproFilterTests.cs`** — end-to-end repro using the exact 7 UIDs from the capture. Builds the
  filter and calls `expr.MatchTestCase(...)` for each UID, printing `MATCH[true/false]`. **Intent:**
  prove the `|` node yields `MATCH[false]` (dropped) while the others yield `true`. **This assertion
  was NOT yet run to completion** (see below). It currently only `Console.WriteLine`s; once you
  confirm the observed behavior, convert the prints into hard `Assert` statements as the regression
  test.

---

## Where I left off / next steps

Blockers on the current (heavily loaded) machine: repeated `dotnet build` runs of the VSTestBridge
unit-test project were slow (10+ min) and hit `MSB3021`/`MSB3027` file-lock errors from leftover
test-host processes (e.g. PID 36376) and a running VS instance locking output DLLs.

**To continue on a clean machine:**

1. Build the test project:
   ```powershell
   .\build.cmd -c Debug -projects test\UnitTests\Microsoft.Testing.Extensions.VSTestBridge.UnitTests\Microsoft.Testing.Extensions.VSTestBridge.UnitTests.csproj
   ```
   (Close VS / kill stale `Microsoft.Testing.Extensions.VSTestBridge.UnitTests` and `dotnet` test-host
   processes first if you hit file-lock errors. net9.0 may fail to *run* if the .NET 9 runtime isn't
   installed — that's environment-only; use net8.0.)

2. Run the repro + filter tests on net8.0 (name-substring filter):
   ```powershell
   & "artifacts\bin\Microsoft.Testing.Extensions.VSTestBridge.UnitTests\Debug\net8.0\Microsoft.Testing.Extensions.VSTestBridge.UnitTests.exe" --filter "ReproFilter"
   & "artifacts\bin\Microsoft.Testing.Extensions.VSTestBridge.UnitTests\Debug\net8.0\Microsoft.Testing.Extensions.VSTestBridge.UnitTests.exe" --filter "RunContextAdapterFilter"
   ```
   Read the `MATCH[...]` lines to confirm `PrintArg("as|")` is the only `false`.

3. Decide fix location (UID-equality vs. fix `|` escaping + `i`/`k` guard). Implement, update the
   `RunContextAdapterFilterTests` expectations, and harden `ReproFilterTests` into asserts.

4. Consider an acceptance test (NUnit-on-MTP asset with a `[TestCase("as|")]`) selected via the server
   protocol, to guard the real Test Explorer path end-to-end.

---

## Repro recipe (self-contained)

- NUnit-on-MTP project (net10.0), `EnableNUnitRunner=true`, `<OutputType>Exe</OutputType>`,
  `global.json` -> `"test": { "runner": "Microsoft.Testing.Platform" }`.
- Test:
  ```csharp
  [TestCase("as")] [TestCase("as!")] [TestCase("as&")]
  [TestCase("as=")] [TestCase("as|")] [TestCase("as~")]
  public void PrintArg(string arg) => Assert.That(arg, Is.EqualTo("a"));
  ```
- `dotnet run -- --list-tests` shows all (incl. `PrintArg("as|")`).
- In VS Test Explorer, "Run All" -> the `|` case never runs; the host log shows "discovered 6 of 6"
  while 7 were selected (see the committed capture for the exact JSON-RPC frames).
- `dotnet run -- --filter-uid '...PrintArg("as|")'` -> runs fine (proves UID path is safe).

## User-side workaround (not a platform fix)

Give the pipe case an explicit name so its display name has no filter operators:
```csharp
[TestCase("as|", TestName = "PrintArg_pipe")]
```
