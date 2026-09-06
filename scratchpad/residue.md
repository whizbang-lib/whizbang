# Coverage residue — lines that will not be covered by unit tests

> **Read this first.** Of the categories recorded here, **five were wrong or overstated**:
> B (a harness that already existed), C (infrastructure the suites self-provision via
> Testcontainers), E (dissolved by `-Mode Ai`), B2 (events unraisable externally but drivable
> through the worker), and I (factory lambdas the integration suites do execute). A sixth, A,
> was right about 8 of its 9 entries — the ninth was a live hang, not defensive code.
>
> The failure mode of a residue list is that it converts "I have not checked" into "this
> cannot be done", and then stops being questioned. Before trusting any entry below, check
> whether a harness, fixture, or suite already exists for it.

## Index — what each entry actually is

Not every heading below is residue. Three different things are recorded here and conflating
them is how a list like this stops being useful.

**Live residue — will not be covered by a unit test, with a reason that survived re-checking**

| | what | why not |
|---|---|---|
| A | 8 defensive branches inside covered members | Case 3; the attribute is member-level |
| ~~K, N~~ | ILRepack copies | **SUPERSEDED** — most covered in round 21; the rest is a shared-assembly self-test, not residue |
| L | PerspectiveWorker's ~40 guard and shutdown branches | value-per-test, none strand a caller |
| M | Generator internal-fault diagnostics | needs an injected fault inside the generator |
| P | ASB processor-lifecycle handlers | needs a live processor; the policy they wire is 100% covered |
| Q | The hardened `usingWhizbang` fallback | unreachable by construction, kept correct on purpose |
| R | `root is not CompilationUnitSyntax` guards | ParseText always yields a compilation unit |
| C | Broker receive/settle paths only | two of its four bullets were wrong; see the entry |
| X | SharedSelfTest's failure arm, 1 line per host | only runs when a merged copy has diverged; cut from 12 lines/host to 1 |

**Tractable — available work, not residue. Do not read these as done.**

| | what | the obstacle, precisely |
|---|---|---|
| S | ClaimWorker rows-per-stream *effect* | needs the outstanding budget driven above outstanding; the update and its floor ARE now covered |

**Corrected — recorded because being wrong here is the expensive failure mode**

B, B2, C, E, I were wrong or overstated and are marked so in place. L's *reasoning* was wrong
while its conclusion held. N claimed most merged copies were unreachable; most were reached.

**Measurement context — not residue at all**

B3, F, G, H, J: defects in how the number was produced, and their fixes. F is the running list.
J is the most consequential: a stall-killed project still writes a *partial* cobertura, so its
un-run tests' lines enter the worklist as phantom gaps. A cobertura file is not proof a project
completed.

Branch: test/coverage-round-22 (PR #670, base develop).
Rule applied: ai-docs/coverage-exclusions.md. Case 3 (a defensive branch *inside* an
otherwise-covered member) gets NO `[ExcludeFromCodeCoverage]` — the attribute is
member-level, so applying it there would suppress the member's real covered lines and
inflate the measurement. Those stay red on purpose.

## A. Case-3 defensive branches — RE-EXAMINED, 8 of 9 confirmed safe

Verified present in hand-written source (9 markers, generated/obj excluded):

| File | Line | Why unreachable |
|---|---|---|
| `Whizbang.Core/Execution/SerialExecutor.cs` | 182 | channel completes before worker cancellation |
| `Whizbang.Core/Execution/SerialExecutor.cs` | 197 | `WriteAsync` throws before queueing canceled work |
| `Whizbang.Core/Execution/SerialExecutor.cs` | 210 | exceptions are captured in `PooledValueTaskSource` |
| `Whizbang.Core/Messaging/EnvelopeSerializer.cs` | 25, 38 | payload/`TMessage` cannot be `JsonElement` after serializer checks |
| `Whizbang.Core/Workers/ServiceBusConsumerWorker.cs` | 623 | transport envelopes are strongly typed by construction |
| `Whizbang.Core/Transports/InProcessTransport.cs` | 169 | cleanup arm reached only if the subscription leaks |
| `Whizbang.Core/Dispatcher.cs` | 5194 | diagnostic arm for a state the serializer already rejects |

### Re-examination (2026-09-04) — the rule that separates safe from harmful

After `SerialExecutor:197` turned out to be a live hang rather than defensive code, every
marker in this list was re-read against one question: **what does the branch DO when it
fires?**

| Behavior when the branch fires | Verdict |
|---|---|
| `throw` with diagnostics | **Safe.** Fails loudly; caller learns immediately. |
| log / record telemetry | **Safe.** Observable, nothing is stranded. |
| `continue` / `return` / swallow, while a caller waits on a promise | **Dangerous.** Silent hang or data loss. |

Results across the nine markers:

- `EnvelopeSerializer.cs:25, 38` — **throw**, with double-serialization diagnostics. Safe.
- `ServiceBusConsumerWorker.cs:623` — **throws** naming the offending envelope type. Safe.
- `Dispatcher.cs:5194` — **throws** on a JsonElement MessageType. Safe (the "DIAGNOSTIC: Log"
  comment is stale; it throws rather than logs).
- `SerialExecutor.cs:182` — catch in `DrainAsync` after the worker has already finished; no
  caller is waiting. Safe.
- `SerialExecutor.cs:210` — catch around execution, but `_executeWithPooledStateAsync` has
  already completed the source in its own try/catch. Safe.
- `InProcessTransport.cs:169` — not a branch at all: a `finally` that disposes the response
  subscription on every path. Mislabeled "DEFENSIVE"; it always runs.
- `RoslynGuards.cs:11` — a doc comment, not code.
- **`SerialExecutor.cs:197` — WAS NOT SAFE.** `continue` past a canceled work item without
  completing the caller's `PooledValueTaskSource`. Fixed in 5b2efafa6.

**Conclusion:** 8 of 9 are genuine Case-3 residue and stay red without an attribute, per
`ai-docs/coverage-exclusions.md` §3. One was a live defect. The distinguishing question is not
whether a branch is labelled defensive — it is whether firing it strands someone.

**Latent defect found while surveying, not a coverage matter:** both defensive arms in
`_processWorkItemsAsync` abandon the caller's `PooledValueTaskSource` without completing
it — line 197 `continue`s past a canceled work item, and line 210 swallows an escaped
exception. Either one firing leaves the caller's `await` hanging forever rather than
failing. (Line 182, by contrast, is harmless: it catches in `DrainAsync` after the worker
has already finished, so nothing is left waiting.) Both branches are unreachable today,
so this is a latent hazard worth an issue, not a live bug.

## B. ~~Needs a harness that does not exist yet~~ — WRONG, the harness exists

**CORRECTED (2026-09-04).** `tests/Whizbang.Generators.Tests/GeneratorTestHelper.cs` provides
`RunGenerator<TGenerator>(source)` — a generic Roslyn driver that already loads the
FastEndpoints and HotChocolate references, and `Whizbang.Generators.Tests` already
project-references **both** transport generator projects. No scaffolding was required; the
two lowest-covered assemblies in the repo (HotChocolate.Generators 44.2%,
FastEndpoints.Generators 46.7%) were reachable the whole time.

First proof: `RestLensEndpointGeneratorTests` (7 tests, commit 93d40a377).

Remaining generator work — ordinary test-writing, not residue:
- `RestMutationEndpointGenerator` (FastEndpoints.Generators)
- both HotChocolate generators

**Pattern to note across this file:** categories B, C and E each described the *measurement
setup* rather than the code. Before trusting any remaining entry here, check whether a harness,
fixture or suite already exists for it.

## B2. Events on concrete classes — PARTLY WRONG: unraisable externally, but drivable

`IdleActivityTouchHookBinder` subscribes to three sources. `IWorkNotificationListener.OnSignal`
is on an interface, so a fake raises it directly and that arm is covered.

The C# constraint is real: an event can only be raised from inside its declaring type, so no
test can fire `ClaimWorker.OnBatchClaimed` or `HeartbeatWorker.OnHeartbeatRecorded` from
outside. **But that does not make the arms untestable, which is what this entry implied.**

`ClaimWorker.cs:443` raises `OnBatchClaimed?.Invoke(batch)` inside its own execute loop
whenever the coordinator returns a non-empty batch. A stub `IWorkCoordinator` whose
`ClaimWorkAsync` returns work will drive it — the same shape as the `RecordingCoordinator`
already written for `FailureFlushWorkerTests`. Observation must wait on the touch arriving
(a TaskCompletionSource with a deadline), not on a delay.

So this is **scaffolding work, not residue**: moderate effort, and it would cover both the
binder's remaining arms and ClaimWorker's claim path. Reclassified rather than left as an
excuse.

## B3. Measurement hazard: fail-fast corrupts the coverage number

`-Mode AiUnit` enables `--fail-fast` by default. When any test fails, the runner aborts that
project mid-flight, so its cobertura file is **partial** and every line the aborted tests would
have covered is reported uncovered. The percentage then moves for reasons that have nothing to
do with the code.

Observed directly: a run in which `Whizbang.Core.Tests` aborted on one flaky test reported
87,793 covered — 8 *fewer* than the previous green run — in a cycle that had only *added*
tests. Use `-NoFailFast` for any run whose number is going to be compared.

**Pattern across -Mode Ai runs 4 and 5.** Failures are accumulating in timing-sensitive tests
as the suite grows, each passing in isolation:

| Run | Failed | Test |
|---|---|---|
| 4 | 1 | `ThreadPoolFloor_AbsorbsAFanOutBurst_LivenessKeepsGettingAThreadAsync` (Soak) |
| 5 | 2 | the same, plus `Contract_MultipleStreamsConcurrent_IndependentProcessing_Async` (Core.Integration) |

Both verified green when run alone. `[NotInParallel]` cannot help here: it serializes within an
assembly, and these are separate test *projects* which `Run-Tests.ps1` runs concurrently
(unit projects at max 10 parallel). The `ClaimWorkerDoorbellLivenessTests` failure earlier this
session was the same shape *inside* one assembly, and was fixed by joining the serialized group;
the cross-project version has no equivalent lever from a test attribute.

**This is a real risk to the loop's stopping condition:** every test added raises total host load,
so a "green run" becomes progressively harder to obtain for reasons unrelated to correctness. If a
clean full run is required to declare done, the runner needs either lower cross-project
parallelism for the timing-sensitive suites, or those tests need to stop depending on wall-clock
budgets under load.

**Third occurrence (run 8, uncontended, 91.1%):** `Whizbang.Core.Tests` failed 1 of 10,859
again. The failing test's name is not recoverable from the log -- the runner keeps only the
last 30 lines per project and they were blank progress lines. Running the project alone gives
10,859/10,859. To identify it, a future full run needs `-LogFile` (the script supports it)
rather than relying on the truncated tail.

**Known flaky under full-suite load:** `Whizbang.Core.Tests.Workers.UngatedWorkerAdoptionTests`.
Two separate full runs failed two *different* methods of this class, each at 0ms with
`OperationCanceledException` thrown from the scheduler
(`TestScheduler.ProcessDynamicTestQueueAsync`), while its 30-second safety-net waits had not
elapsed. It passes in isolation (5/5) and inside its full namespace (1,826/1,826), and it failed
this way before any test added in this session existed. Engine-level cancellation under load,
not a product defect — but it aborts the run and corrupts the measurement, so it is worth
chasing separately.

## G. RESOLVED: the migrate CLI always exited 0 — fixed in 05860b04c

`tools/Whizbang.Migrate/Program.cs` never sets an exit code on any path. `analyze` on a
missing directory writes "Directory not found" to stderr and returns; the process still
exits **0**. Same shape for every other command -- there is no `ExitCode`, no
`Environment.Exit`, and no `return 1` anywhere in the file.

For a migration tool this matters more than usual: it is run from pipelines, and a step
that prints an error and reports success lets the next step run against a project that was
never migrated.

**Fixed.** Four error paths now set a non-zero code: `analyze` (directory not found), `apply`
(decision file not found; command failure) and `status` (command failure). `analyze` and
`status` moved to the `InvocationContext` handler overload, which `apply` already used. This
was a deliberate, user-visible behavior change, approved before implementing: a pipeline that
passed because of the bug will now fail, correctly, on a step that was already broken.
`--help` still exits 0, pinned by test so the fix cannot break pipelines from the other side.

## J. RESOLVED (mechanism): a killed project still flushes a PARTIAL cobertura

Was: "~15 tests discovered but not executed in a full run, and I could not explain why."
The mechanism is in the runner, not the tests, and it is worse than a miscount.

`scripts/Run-Tests.ps1` terminates a test project two ways -- stall detection when the test
count stops changing for `HangTimeout` (180 s default, ~line 1883) and silence detection at
`HangTimeout * 2` (~line 1904) -- both via `$process.Kill($true)`. That is the SIGTERM behind
`Exit code: 143`.

**A killed project still writes a cobertura file.** Coverage flushes for whatever executed
before the kill, so the surviving report is not empty, it is *truncated*. Every test that had
not yet run contributes nothing, and the lines only those tests reach are reported as
uncovered -- indistinguishable from real gaps. The presence of a cobertura file is therefore
NOT evidence that a project completed; I misread exactly that in run 14.

Confirmed instance, `Whizbang.Core.Tests` in runs 13 and 14:
`RecentlyProcessedEventCacheSweepWorker` showed its constructor at 8/8 (the DI registration
tests ran) and `<ExecuteAsync>d__4` at 0/20. Running the same project alone gives **19/20** on
that state machine. Nothing was wrong with the code or the tests -- the tests that drive
`ExecuteAsync` never got to run before the kill. Ranking that report sent a whole cycle after a
class that was already covered.

The PARTIAL banner's premise is too narrow: it says "a failed project contributes no cobertura,
so its lines leave the denominator". A *killed* project contributes a partial one instead, which
keeps its lines in the denominator and invents uncovered ones.

Fixed in the runner: cobertura files belonging to any failed or killed project are now dropped
before the merge, and the excluded projects are named, so the banner's stated semantics hold.

Operational rule regardless: **never rank a worklist from a run whose banner says PARTIAL**, and
never treat "every project produced a cobertura" as proof the run completed whole.

Still open from the original entry: whether `Whizbang.Generators.Tests`' ~15-test gap is this
same kill. Same signature, not yet confirmed against a run that completes whole.

## H. Operational: one build at a time on this machine

Measured, not inferred: building `Whizbang.LanguageServer.Tests` alone reported **16m21s
wall time using 89s of CPU at 9% utilization**. It was blocked on MSBuild/NuGet locks held
by a concurrent full-suite build, not compiling.

Two coverage runs (~100 minutes total) produced no number because scoped builds and the
full run contended. Both were misdiagnosed first as a wedged compiler, then as full
rebuilds forced by `dotnet format`. Neither was the driver. `obj/`-write counts cannot
distinguish a stalled compile from a long one, because Roslyn buffers output until a
compilation completes -- do not use that signal.

Rule: check `pgrep -f "Run-Tests.ps1"` before any scoped build, and check for scoped builds
before launching a coverage run. Prefer `-ProjectFilter` for per-project measurement and
`dotnet format --include <file>` over formatting whole projects.

## I. DI factory lambda bodies — LARGELY RESOLVED by -Mode Ai

A registration file's *statements* are covered as soon as a test calls the Add* extension, but
the **body of each factory lambda** runs only when the service is resolved. Resolving these
opens the very thing the unit suite has no access to — an AMQP connection, a Service Bus
client, a Postgres LISTEN connection.

Verified on `Whizbang.Transports.RabbitMQ/ServiceCollectionExtensions.cs`: all 53 uncovered
lines fall inside `AddSingleton<IConnection>(sp => {...})`,
`AddSingleton<IBacklogPeek>(sp => ...)` and
`AddSingleton<ITransportDeadLetterDrainer>(sp => {...})`. Three unit test files already cover
the registration surface itself, so the remaining lines are not a testing gap.

Roughly **254 lines** repo-wide sit in this shape:

| Uncovered / total | File |
|---|---|
| 78 / 248 | `Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs` |
| 53 / 268 | `Whizbang.Transports.RabbitMQ/ServiceCollectionExtensions.cs` |
| 43 / 281 | `Whizbang.Transports.AzureServiceBus/ServiceCollectionExtensions.cs` |
| 35 / 1753 | `Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs` |

**RESOLVED (2026-09-04).** The mechanism described above is correct — the bodies run only on
resolution — but classifying them as residue was not. The integration suites resolve these
services against real infrastructure, so under `-Mode Ai` they execute:

| File | AiUnit | Ai |
|---|---|---|
| `PostgresNotificationsServiceCollectionExtensions` | 68.5% | **97.1%** |
| `RabbitMQ/ServiceCollectionExtensions` | 80.2% | **91.0%** |
| `AzureServiceBus/ServiceCollectionExtensions` | 84.7% | **88.2%** |

Asserting `ServiceDescriptor` lifetimes remains the right *unit-level* contract, and the
existing tests already do that. The lambda bodies are covered by the integration suites.

## C. Needs live infrastructure

- **Broker transports** (Azure Service Bus, RabbitMQ receive/settle paths) — need a real
  broker; the emulator has no admin plane, so DI-wired tests need
  `AutoProvisionInfrastructure=false` and still cannot exercise settlement.
- ~~**`Whizbang.Offloads.AzureBlob.AzureBlobMessageBodyStore`** (65 of 104 lines)~~ —
  **RECLASSIFIED, not residue.** I logged this as needing infrastructure that was not worth
  mocking. It turns out `Whizbang.Offloads.AzureBlob.Integration.Tests` already covers it via
  `AzureBlobStoreRoundTripTests` with an `AzuriteFixture` — a Testcontainers-provisioned
  Azurite emulator. The suite simply never ran under `-Mode AiUnit`. Same for the RabbitMQ and
  Azure Service Bus integration suites, which self-provision containers under `Containers/`.

  **Lesson for this file: "needs live infrastructure" was frequently a statement about the
  measurement mode, not about the code.** Several entries below were written while only
  AiUnit was in play and should be re-checked against the `-Mode Ai` baseline before being
  trusted.
- ~~**`Whizbang.Migrate` tool** (`tools/`) — a CLI whose paths run against a real database
  and a real project tree on disk.~~ — **WRONG, and it was wrong when written.** The tool
  neither needs nor touches a database. Its project tree is a directory of `.cs` files, which
  a test creates under `Path.GetTempPath()` in three lines. `Whizbang.Migrate.Tests` now
  stands at 507 passing tests covering the transformers, the analyzers, `ApplyCommand` and the
  CLI surface itself — including four production defects this branch found and fixed there
  (a duplicate `using` raising CS0105, and three commands exiting 0 for work they had not
  done).

  This is the fourth entry in this file to claim infrastructure it did not need. The pattern
  is now unmistakable: "needs live infrastructure" was, every single time, a guess made
  without opening the code.

## K. ILRepack-merged shared code is counted once per host assembly  ← NOT a blocker; see the self-test note in N

`Whizbang.Generators.Shared` is ILRepack-merged into four generator assemblies. Its 18 classes
therefore appear **five times** in the report — once in their home assembly and once inside each
host — and each host can only cover the handful of utilities it actually calls. 52 of those 72
merged copies sit under 50% in their host.

This is why the generator work in commits 93d40a377 / e8332572d / fa48d2b60 moved the assembly
totals by nothing while doing exactly what it should. The four generator *classes* went to
**98.6–99.3%**; the assemblies stayed at 44.2% / 46.5% because they are dominated by merged
copies of EFCore/Postgres-oriented shared types that a FastEndpoints or HotChocolate generator
will never execute — and should never execute.

**Measured impact on the remaining work:**

| Measure | Coverage | Uncovered |
|---|---|---|
| reportgenerator, per-assembly | 95.1% | 5,584 |
| **deduplicated by unique source line** | **96.08%** | **3,248** |

So **2,336 of the 5,584 "uncovered" lines are the same source counted more than once**, and are
uncoverable in the host that counts them. Literal 100% against the reportgenerator number is
therefore unreachable by writing tests, for the same structural reason `-Mode AiUnit` was.

**Options (needs a decision):**
1. Track progress on the deduplicated figure (96.08%) and treat the per-assembly number as
   indicative only.
2. Add reportgenerator `-classfilters` to drop `Whizbang.Generators.Shared.*` from non-home
   assemblies — cleanest, but the filter syntax cannot express "except its home", so it would
   also drop the home copy where the code IS covered.
3. Stop ILRepack-merging Shared into the transport generators, if the packaging permits it.

## L. PerspectiveWorker's remainder — scattered safe error arms

52 uncovered lines of 2,123 (97.5% covered), spread across roughly forty separate one-to-three
line branches. Sampled against the rule from category A — does firing the branch throw, log, or
strand someone?

- `427-428` — `catch (OperationCanceledException)` calling `TrySetCanceled` on the startup TCS.
  Completes the promise; nobody is left waiting. Safe.
- `3795-3797` — logs and rethrows. Safe.
- `3673-3677` — opens a fresh scope to log a detached-stage failure, the same shape covered by
  `LifecycleTrackingStateTests`. Safe.
- `2005-2007` — conditional on an optional sync tracker being registered.

RE-SAMPLED, and the first sample was unrepresentative. It happened to pick only catch arms,
which is why this entry says "error arms". Most of the 53 are not error arms at all:

    552  break     -- OperationCanceledException while awaiting work; shutdown
    844  return    -- no IWorkCoordinator registered; a schema-only host has nothing to sweep
   2720  continue  -- lease already held for this work id; re-acquiring would be wrong
   2934  continue  -- Guid.Empty work id filtered out
   3480  return    -- empty batch

These are guard clauses in normal control flow and one shutdown break.

That also corrects the rule this file states in category A. "throw/log = safe;
continue/return = dangerous" is too coarse and would flag every line above. The dangerous shape
is narrower: `continue`/`return`/swallow **inside an error handler, while a caller is waiting on
a promise the handler does not complete**. That is what the SerialExecutor bug was -- cancelled
work abandoned its caller mid-await. A guard clause that declines to do work nobody asked for
strands no one.

Conclusion unchanged, reasoning corrected: the judgement is value-per-test, roughly forty
fixtures for forty guard and shutdown branches in a file already at 98.9%, none of which strand
a caller. Revisit if the deduplicated count elsewhere runs out.

## M. Generator internal-fault diagnostics — report loudly, unreachable from a valid compilation

`EFCoreServiceRegistrationGenerator` sits at 98% (35 uncovered of 1,753). The two largest
blocks are catch-alls that report a diagnostic and continue:

- `176-184` — wraps `_generateRegistrationMetadata`, reporting **EFCORE996** on any exception.
- `1353-1364` — wraps embedded-snippet loading, reporting **EFCORE999** and returning false.

Both satisfy the category-A rule: firing them reports rather than strands. Reaching them needs
an injected fault inside the generator or a corrupted embedded resource — neither is producible
from a test compilation, and adding a seam purely to reach them would put test-only surface into
a shipped analyzer.

The remaining ~14 uncovered lines in that file are isolated singles of the same shape scattered
through a 1,753-line generator.

Same judgement as category L: not claimed impossible, but the value per test is poor and the
mechanism is a loud failure rather than a silent one. Revisit if the deduplicated count
elsewhere runs out.

## N. ILRepack copies: 1,383 uncovered lines of already-covered source

Measured from run 6's report, fresh pages only.

`Whizbang.Generators.Shared` is merged by ILRepack into four generator assemblies. Every copy
reports the *same source path* (`src/Whizbang.Generators.Shared/Utilities/...`), but
reportgenerator counts them as separate classes because they sit in different assemblies. So
each shared file is counted five times.

    uncovered in the Shared source assembly : 23
    uncovered across its four merged copies  : 1,383

The source assembly is effectively complete. The 1,383 are duplicates of lines already covered
there, and they are 36% of the deduplicated remainder.

Deduplicating by source line -- a line covered in any copy is covered in the file -- gives:

    as reported : 96.0%  (111,175 / 115,788), 4,613 uncovered
    deduplicated: 96.40% (78,185 / 81,107),  2,922 uncovered

Part of the merged surface is not merely untested but *unreachable in its host*.
`IdentifierValidation.ValidateTableName/ValidateColumnName/ValidateIndexName` and the three
`Is*Valid` companions take an `IDbProviderLimits`. The only implementations, `PostgresLimits` and
`OverriddenPostgresLimits`, live in `Whizbang.Data.EFCore.Postgres.Generators`. That host's copy
is the best covered of the four (13 uncovered vs 34); the other three carry the code with nothing
able to satisfy its parameter. ILRepack merges the whole shared assembly regardless of use, and
no `Internalize` flag is set.

Driving those through reflection is not possible the ordinary way either: each merged copy has
its own type identity for `IDbProviderLimits`, so one C# class cannot implement all four. It
would take runtime type emission per host.

UPDATE -- most of this turned out to be reachable after all, and has been covered.

MergedSharedCopyTests now drives, through all four copies: TypeNameUtilities (all seven
methods), TypeSymbolExtensions (the base-type walk and signature dedupe), TemplateUtilities
(both ReplaceRegion guards, GetEmbeddedTemplate, ExtractSnippet), ConfigurationUtilities (both
build properties, both selector entry points), AttributeUtilities (array arguments, named and
positional) and NamingConventionUtilities (GenerateTableName, StripConfigurableSuffixes).
102 cases where there were 42.

Two things made it work that were not obvious at first: the symbol-taking methods are reachable
because Roslyn types are NOT merged, so one test compilation's symbols satisfy every copy; and
`ExtractSnippet`/`GetEmbeddedTemplate` take the `Assembly` as a parameter, so any copy's method
can be handed the one assembly that actually carries the embedded templates.

What remains is narrower than this entry first claimed -- and it is not unreachable either.

`IdentifierValidation`'s methods take an `IDbProviderLimits`, and each merged copy has its own
type identity for that interface, so no single class in the *test* assembly can satisfy all
four. That is a limitation of driving the code by reflection from outside, not of the code.

The answer is a self-test **inside the shared assembly**: a public entry point that exercises
its own surface, carrying whatever helpers it needs -- including its own `IDbProviderLimits`
implementation. ILRepack merges those helpers into every host too, so each copy holds an
implementation with matching identity and the problem does not arise. Each host's test then
calls one entry point.

BUILT AND VERIFIED. `Whizbang.Generators.Shared.Diagnostics.SharedSelfTest` now carries the
checks and its own `SelfTestLimits`; `MergedSharedCopyTests` calls `Run()` on each host copy.
All four pass, and a deliberately broken check inside it made all four fail with the specific
message -- so the assertion can genuinely go red, which is the only thing that makes a green one
worth anything.

The category is now closed, and the enumeration is what closes it:

- Merged **records** (`TableNameConfig`, `PerspectiveTableSchema`, the model records) are
  reachable from outside: `Activator.CreateInstance` builds one per copy, which the existing
  reflection tests already do.
- Merged **interfaces** cannot be: you cannot instantiate one, and no class declared in a test
  assembly can implement four distinct identities at once.
- The shared assembly declares **exactly one interface** (`IDbProviderLimits`) and **no abstract
  classes**. So that is the entire hard category, and the self-test covers it.

There is no remaining ILRepack surface that tests structurally cannot reach. What is left is
ordinary uncovered code, to be handled like any other.

The rest is a build-shape question, not a test-coverage one: either ILRepack trims what a host
does not use, or the measurement stops counting the same file five times. Both are decisions
outside this loop.

## O. DI factory lambdas: mostly tractable, one genuinely broker-bound

A recurring shape across three files. `services.AddSingleton(sp => new Thing(...))` puts the
construction inside a lambda that runs only when something *resolves* the service. Registration
tests that count descriptors -- which is what the suite does today -- never execute them.

    Whizbang.Core_WorkerPipelineExtensions              90 uncovered
    Whizbang.Transports.AzureServiceBus_ServiceCollectionExtensions  33
    Whizbang.Data.EFCore.Postgres_PostgresDriverExtensions           31

The invariant these hide is worth asserting: a factory whose dependency is not registered throws
on first resolution, which in production is host startup, and in the worker case surfaces as a
background-service failure with the real cause buried. Being covered by a resolution test is the
same thing as being checked for constructibility.

Tractable, and being added: WorkerPipelineExtensions' ~22 hosted-service factories.

Residue within this group: `ServiceCollectionExtensions` lines 145-152 build a real
`ServiceBusClient` through `AzureServiceBusConnectionRetry.CreateClientWithRetryAsync(...)
.GetAwaiter().GetResult()`. Resolving that registration dials the broker synchronously, so it
needs a live namespace -- and the emulator has no admin plane. The neighbouring registrations in
the same file (`AsbBacklogPeek`, `AsbTrafficClassOpsRateSource`) only need an `ITransport` and
are not blocked by this.

## P. AzureServiceBusTransport: processor-lifecycle handlers (35 uncovered of 2,504)

Assessed line by line rather than dismissed as "needs a broker", because the file is 98.6%
covered and the remainder is not homogeneous.

Tractable, and being added: `812-818`. `_createServiceBusMessage(BulkPublishItem, ...)` is the
batch path's own message builder, separate from the single publish path. The publish path has a
correlation/causation test; the batch path's equivalent branches had none. That asymmetry is
worth closing precisely because it is invisible: a trace stays connected or breaks depending on
whether the message happened to be grouped into a batch.

Residue, for a specific reason rather than a general one:

- `1876-1900` -- the namespace-throttle pause/resume. It runs detached inside
  `_handleProcessorErrorAsync`, needs a `ServiceBusException` whose Reason is ServiceBusy, and
  needs a live processor to stop and restart. The decision logic it wires up is a separate
  class, `AsbThrottleBackoffPolicy`, and that class is **fully covered (0 uncovered of 67)**.
  What is untested here is the wiring, not the policy.
- `810-820`-adjacent catch arms and `1039-1042` -- handlers for a processor that is already
  closed or disposed. Reaching them means closing a live processor mid-operation.

The general shape: what remains in this file is processor lifecycle, and the pure logic it
delegates to has its own tests. That is a better place to be than the line count suggests.

## Q. HandlerToReceptorTransformer 268-280: the hardened-but-unreachable using fallback

The `if (!addedWhizbang)` block that builds a `using Whizbang.Core;` directive from scratch. The
guard above it requires an exact `using Wolverine;`, which the loop always replaces, so the flag
is never false at that point -- and after the CS0105 fix the flag is additionally seeded true
when the file already imports Whizbang.Core, which closes the other way in.

It is kept, and kept correct, deliberately: `SyntaxFactory` emits `using` and the name as
adjacent tokens, so a directive built without an explicit leading space renders as
`usingWhizbang.Core;` and the migrated file does not compile. That exact bug shipped once and
recurred in a second transformer, so the block exists to stop a future loosening of the guard
quietly emitting broken source. The in-code comment says so.

Genuinely unreachable, deliberately retained, and not a candidate for
[ExcludeFromCodeCoverage] -- the member around it has covered lines.

Worth recording separately: the *reachable* half of that same fix -- the new gate at 235-240
that drops the redundant using -- was NOT covered when this was written. The regression
assertion added with the fix lived in ApplyCommandTests and was satisfied through a different
transformer's path, so the three gates actually added by the fix went unexercised. Unit tests
for each of the three transformers have been added. A fix asserted only end-to-end can pass
without touching the code it was written for.

## R. Transformer guards against a root that is not a compilation unit

Every transformer opens with:

    if (root is not CompilationUnitSyntax compilationUnit) { return root; }

`TransformAsync` obtains the root from `CSharpSyntaxTree.ParseText(...).GetRootAsync()`, which
returns a `CompilationUnitSyntax` for any input -- including empty text and text that does not
parse, where the node simply carries diagnostics. So the false arm cannot be reached through the
public entry point.

It stays as a type guard rather than a cast, which is right: the helpers are `SyntaxNode`-typed
and a future caller could hand one a different node. Category A -- returns the input untouched,
strands nobody.

Same shape appears in ProjectionToPerspectiveTransformer (line 78) and the other transformers
that share the pattern.

## S. ClaimWorker adaptive sizing -- TRACTABLE, not residue, not yet built

Recorded so the next reader does not mistake it for a dead end.

`ClaimWorker` line 710 keeps an exponential moving average of rows-per-stream:

    _rowsPerStream = (0.2 * observed) + (0.8 * _rowsPerStream);

updated only when a claimed batch carried both inbox work and inbox stream ids. It is consumed
at line 677:

    streamsAffordable = ceil(headroomRows / max(1.0, _rowsPerStream));

The store claims by stream while the budget is in rows, so this ratio is what converts one into
the other. Never updating it leaves the worker claiming against the initial assumption of 1.0
row per stream -- on a workload of thousand-row streams that over-claims by three orders of
magnitude, and the symptom is a lease budget exhausted by one claim rather than an error.

The field is private, but it is observable through behaviour: the stream count requested on the
next claim. A fixture that runs two claim cycles -- first returning a batch with a known
rows-to-streams ratio, then asserting the second claim requests proportionally fewer streams --
tests it without touching internals.

ATTEMPTED. The EMA update itself is now covered -- a batch carrying stream ids as well as rows
reaches it -- and the deadlock guard beneath it is asserted. Its *effect* is not, and the reason
is worth recording rather than re-attempting blind.

`streamsAffordable = ceil(headroom / rowsPerStream)` only distinguishes workload shapes while
`headroom > 0`, and `Headroom(outstanding) = max(0, _current - outstanding)`. In any fixture
where the fake reports the batch as outstanding, `_current` sits at or below that count and the
headroom is zero, so every shape floors at one stream and the division measures nothing. Two
attempts confirmed this: first with no completions, then with the fake recording the whole batch
as completed on every claim to drive a drain rate. Both floored.

Making the shape observable means driving `AdaptiveOutstandingBudget` into a regime where
`_current` exceeds outstanding -- a third interacting variable, adapting on its own cadence.
That is a timing-sensitive multi-variable fixture, and this suite already has cross-project
saturation problems; a flaky test here would cost more than the three lines are worth.

The tractable version, if someone wants it: test `AdaptiveOutstandingBudget` directly for the
headroom regime, then unit-test the rows-per-stream arithmetic in isolation, rather than trying
to observe both through a live ClaimWorker.

## T. The suite flake, diagnosed: doorbell coalescing vs. a test that counts claims

Round 21 lost three of its last four measurement runs to a test failure somewhere in the suite,
and the victims kept changing, which made it look like ambient load sensitivity. Sampling
`Whizbang.Core.Tests` three times in a row separated signal from noise:

    run 1  clean
    run 2  clean
    run 3  FreshWorkOnEmptyEdge_DoorbellPreceded_NoMissRecordedAsync   30s 005ms

That test had already failed once earlier in the session, also at exactly its 30s timeout. It is
a repeat offender, not a random victim -- roughly one run in three.

**Mechanism.** `ClaimWorker._wake` is a `SemaphoreSlim(0, 1)` and `RequestImmediatePoll` only
releases when `CurrentCount == 0`:

    public void RequestImmediatePoll() {
      if (_wake.CurrentCount == 0) {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
      }
    }

Two doorbells rung close together therefore collapse into one pending permit, and the worker
performs one claim rather than two. The test rings `SignalNewWork()` after each claim signal and
waits for three distinct claims; when the second and third collapse, the third never arrives.
Polling is deliberately parked at 60s by that test so claims must be doorbell-driven, so nothing
else wakes the worker inside the 30s window and it times out.

**This is most likely the test's assumption, not a product defect.** Coalescing wakes is the
right behaviour -- a single claim picks up all available work, and the worker makes no promise of
one claim per signal. The test encodes a promise the worker does not make, and load decides
whether it holds.

**Fix direction for round 22:** assert the invariant the test names -- that a doorbell-preceded
discovery records no miss -- without requiring an exact claim count. Wait on
`ConsecutiveMissedDoorbells` staying 0 once the work has drained, rather than on three claims
arriving. The neighbouring test at line 121 counts claims the same way and is presumably exposed
to the same collapse.

FIXED in round 22. Both tests now wait on `SignalBusLivenessState.DoorbellEvaluated`, a signal
added for the purpose because the moment they were proxying for had none. Three consecutive
full-suite runs after the fix: the doorbell test did not fail once, against roughly one in three
before.

**But the suite is not clean, and the tidy story was wrong twice.** When failures landed on
different tests each run I read it as ambient load sensitivity. Sampling then found the doorbell
test recurring and I read it as a single repeat offender. Verification found a third thing:

    run 1  clean
    run 2  DrainMode_OceDuringShutdown_StopsTheStreamInsteadOfLoggingPerPerspectiveAsync  132ms
    run 3  clean

That is a different shape -- an assertion failure at 132ms, not a 30s timeout -- so it is not the
coalescing mechanism, and the doorbell fix does not touch it. There is at least one recurring
flake AND other independently unstable tests.

**Next to diagnose.** `DrainMode_OceDuringShutdown_...` asserts that an OperationCanceledException
during shutdown stops the stream rather than logging once per perspective. Failing fast on an
assertion means it observed the wrong thing, not that it waited for something absent. Sample it
in isolation first to establish a rate before theorising.

The practical consequence stands either way: a coverage run still cannot be assumed whole. The
banner now says when one was partial, which makes the failure visible rather than silent -- but
visible is not gone.

## U. DrainMode_OceDuringShutdown: an interaction, characterised but not yet fixed

Sampled in isolation first this time, before forming any theory -- the discipline skipped on the
doorbell flake, where theorising off three data points produced two confident wrong answers.

    isolation, 6 runs : 6 clean
    full suite        : fails roughly 1 in 3-6

So it is an interaction, not a defect in the test's own logic. That rules out reading the test
harder, which is where the previous investigation wasted its time.

**What is known.** `PerspectiveWorkerDeepPathDrainTests` carries no `[NotInParallel]`, so it runs
fully parallel with the rest of the suite. The failure is an assertion at ~132ms, not a timeout,
so the test observed the wrong thing rather than waiting for something absent -- a different
shape from the doorbell coalescing, and unaffected by that fix.

The test cancels from INSIDE the runner (`BeforeThrow = cts.Cancel`) and then throws an OCE, to
distinguish "shutdown reached the perspective" from "one perspective misbehaving". The two arms
differ only by whether cancellation is observed, and the assertions are an exact call count and
the absence of a "skipping to next perspective" warning. Either could flip if contention moves
where cancellation is observed relative to the throw.

**Do not add [NotInParallel].** It would mask the symptom, serialise a whole class, and
establish nothing about the mechanism.

### Hypotheses tested and DISPROVED — do not re-run these

1. **The DrainRunner signals too early.** `RunWithEventsAsync` calls
   `_firstRunWithEvents.TrySetResult()` at line 1060, then `BeforeThrow?.Invoke()` (which cancels
   the test's CTS) and throws at 1068-69. So the test resumes one step before the state it
   depends on. This is a REAL ordering defect in a shared test double and worth fixing on its own
   merits -- but it is NOT the cause. Widening that window with a deliberate 150ms delay between
   the signal and the throw: **3/3 passed.**

2. **Thread-pool starvation under full-suite load.** Running the test alone with
   `DOTNET_ThreadPool_ForceMinWorkerThreads=1` and `ForceMaxWorkerThreads=2`: **3/3 passed.**

3. **Shared static state.** `PerspectiveWorker` references none of the four static perspective
   registries. `_createWorker` builds every collaborator per test -- coordinator, registry,
   logger, harness, instance provider. Nothing is shared.

### What is established

    isolation            6/6 clean
    own class (19 tests) 5/5 clean
    widened race window  3/3 clean
    starved thread pool  3/3 clean
    full suite           ~3 failures in ~15 runs (~20%)

Cross-class interaction. Assertion-shaped (~132ms), not a timeout, so the test observes the wrong
thing rather than waiting for something absent. The two arms it distinguishes differ only by
`when (ct.IsCancellationRequested)` at the moment the exception filter runs.

### Next step, and why it is expensive

Bisect the suite: run half plus the target, repeatedly. At a ~20% rate each half needs several
runs before "clean" means anything -- roughly 8-10 runs per bisection step to be reasonably
confident, at ~2.5 min each. That is the honest cost, and it is why this is recorded rather than
finished.

Cheaper alternative worth trying first: capture the failure WITH its assertion text. Two attempts
failed to -- one grepped for the wrong test name, one hit four clean runs. The failing assertion
(exact call count vs. absence of the "skipping to next perspective" warning) would split the
remaining space in half immediately.

## V. ASB ServiceCollectionExtensions: the namespace-mirror path needs a subscription

Traced rather than assumed, because this file is 94% covered and the remainder is not uniform.

- `404-410` -- logs after `peer.InitializeAsync()`. Needs a live namespace.
- `428-438` -- `_activeConsumeNamespaceKeys`, pure logic and tempting. It is reached ONLY through
  a deferred delegate handed to `NamespaceRoutingTransport`, which invokes it from
  `_activeMirrorTransports()`, which is called from `_mirrorSubscribeAsync` -- i.e. only when a
  subscription is actually opened. The registration itself composes fine offline (the existing
  tests do exactly that with `AutoProvisionInfrastructure = false`), but nothing invokes the
  delegate without a broker.
- `524-532` -- merging non-default namespaces from configuration over the code map, inside the
  `IInfrastructureProvisioner` factory. Probably reachable offline: the factory needs an
  `IServiceBusAdminClient`, which the offline harness already supplies. Observing the merge means
  reaching into the provisioner, so it is awkward rather than blocked. Left as available work.

The lesson repeated from category O: "needs a broker" is worth checking per block. Two of these
three are genuinely gated; the third is not, it is just inconvenient.

## W. Timing and scheduling assertions cannot hold under the parallel coverage run

Run 12 came back PARTIAL (41 of 46 projects) because
`CommitToPerspectiveVisible_FencedByOpenSameDbTransaction_StillLandsUnder1500msAsync` took
10.5 s against a 1500 ms budget. Everything after it was the usual --fail-fast cancellation
cascade.

    isolation, 3 runs : 3 clean
    46-project run    : 7x over budget

The test measures a real end-to-end pipeline -- wh_committed wake, stamp, instance-routed
doorbell, claim, drain window, apply -- and asserts it lands under 1500 ms. That is a production
latency characteristic being measured on a machine running 46 test projects at once, where the
workers in that pipeline are competing for the same cores.

**Not a defect, and not something to quietly weaken.** The assertion guards something real: the
comment says anything near 5 s means visibility has quantized to the backstop cadence, which is
exactly the regression it exists to catch. Raising the budget until it stops failing would
remove the signal.

Options, none of which is obviously right and all of which are a call for the maintainer:

- `[NotInParallel]` reduces in-assembly contention but not the other 45 projects, so it would
  likely still fail.
- Move latency assertions to a category excluded from the parallel coverage run and run them
  on their own. Keeps the signal, costs a separate run.
- Measure the fenced operation rather than total elapsed wall time, if the pipeline exposes a
  point to measure between. That narrows what the budget covers, which may or may not still
  catch the quantization it is aimed at.

**A second instance confirms this is a category, not a tuning problem.** Run 13 came back
PARTIAL on a different test:

    run 12   CommitToPerspectiveVisible_..._StillLandsUnder1500msAsync   latency budget
    run 13   ThreadPoolFloor_AbsorbsAFanOutBurst_LivenessKeepsGettingAThreadAsync   thread availability

Both assert a property of the machine as much as of the code -- one an end-to-end latency, the
other that a thread-pool floor absorbs a burst and liveness still gets a thread. Neither can
hold by construction when 46 test projects contend for the same cores and the same pool. One
instance looked like a badly-tuned threshold; two make it a design constraint.

The practical consequence for anyone running this loop: measurements come back PARTIAL at some
rate unrelated to coverage work. Run 11 was whole; 12 and 13 were not, for two different timing
tests. The banner makes that visible instead of silent, so the number is discarded rather than
misread -- but expect roughly every other run to be unusable until these are separated from the
parallel run.

Recorded rather than changed. It is a test-strategy decision about what these assertions are
for, and raising thresholds until they stop failing would remove the regressions they exist to
catch.

### Update (run 19): now the only failing test in the suite, and not reproducible on demand

`CommitToPerspectiveVisible_FencedByOpenSameDbTransaction_StillLandsUnder1500msAsync` is the sole
failure in an otherwise green 46-project run, at **10.6 s against a 1500 ms budget**. That is not
jitter: the budget exists because after the fence clears there is no further external wake, so
only `CommitOrderStamperOptions.FencedRetryInterval` (250 ms) can stamp the row -- otherwise it
waits for the 5 s backstop. 10.6 s is roughly TWO backstop ticks, which would mean the fenced
retry never fired AND the first backstop tick missed it.

Attempts to reproduce: 3 runs in isolation, 4 more with a second Postgres suite hammering the same
container. All 7 passed. It appears only under the full 46-project run, where a single reproduction
costs ~35 minutes, so bisecting the mechanism this way is not affordable.

What this changes about the entry: option 1 (`[NotInParallel]`) is now clearly the wrong shape --
the contention is cross-assembly and this test already runs serialized within its own. Option 3
(measure the fenced operation rather than total elapsed) is the only one that both keeps the
assertion meaningful and survives an arbitrarily loaded machine, because what the test cares about
is that the fenced retry stamps the row promptly after the fence clears, not that the whole
machine was fast that minute.

If it is worth the run time, the cheap next step is to make the failure explain itself rather than
to chase it: have the test report, on failure, whether the row was stamped by the fenced retry or
by a backstop tick. The elapsed value already hints at the answer; that would confirm it from a
single unattended full run instead of a reproduction loop.

## D. Excluded from the measurement by construction

- `*.g.cs`, `obj/`, `.whizbang/` and `.whizbang-generated/` — source-generator output.
  Already filtered by `Run-Tests.ps1 -Coverage` via `-filefilters`.


## E. Covered by integration suites  — RESOLVED by switching to -Mode Ai

`-Mode AiUnit` runs 33 test projects. It does **not** run `*.Integration.Tests`,
`Whizbang.Data.EFCore.Postgres.Tests`, or `Whizbang.Data.Dapper.Postgres.Tests`. Production
code whose only tests live in those suites therefore reads as **0%** in this measurement
while being genuinely well tested.

Verified examples (each has a dedicated test file, each reads 0% in the AiUnit report):

| Class | Its tests live in |
|---|---|
| `Whizbang.Core.Fingerprint.TypeDefinitionReconciler` | `Whizbang.Data.EFCore.Postgres.Tests/TypeDefinitionReconcilerTests.cs` (real Postgres) |
| `Whizbang.Core.Startup.StandbyHandshake` | `Whizbang.Data.EFCore.Postgres.Tests/StandbyHandshakeE2ETests.cs` |
| `Whizbang.Core.Perspectives.PerspectiveRowCapRegistry` | `.../RowRetentionDeclarationToEnforcementTests.cs` |

`Whizbang.Data.EFCore.Postgres` does not appear in the report's assembly list **at all** —
no AiUnit cobertura covers it.

**RESOLVED (2026-09-04):** the decision was to measure with `-Mode Ai`. That run builds 46
test projects (33 unit + 13 integration, the latter sequential) against a live
`whizbang-test-postgres` container, so every class listed above is now actually executed and
this category ceases to be residue. Expect the headline percentage to move *down* at first:
integration suites pull production code into the denominator that AiUnit never loaded.

Category **I** (DI factory lambda bodies) should shrink for the same reason -- integration
tests resolve those services against real infrastructure, which is exactly what executes the
lambda bodies.

Assembly-level lows in the AiUnit report, for sizing:
`Whizbang.Data.Postgres` 35.4%, `Whizbang.Transports.HotChocolate.Generators` 44.2%,
`Whizbang.Transports.FastEndpoints.Generators` 46.7%, `Whizbang.Migrate` 58.3%,
`Whizbang.Offloads.AzureBlob` 59.8%, `Whizbang.Transports.AzureServiceBus` 66.6%.

## F. Measurement defects found and fixed (not residue -- context for the numbers)

Two bugs made the script's own output untrustworthy; both are fixed on this branch.

1. **The worklist never printed.** Under `Set-StrictMode -Version Latest` the below-100%
   block dereferenced `.coverage` on an int (JsonSummary reports `classes` as a count, not
   an array), throwing a terminating error the script-level trap re-raised as a bare
   `ScriptHalted` against its own line. Green runs silently produced no worklist.
2. **Coverage merged six months of stale results.** `TestResults` is append-only and was
   globbed without an age filter: 1,173 cobertura files, only 26 from the current run.
   Corrected figure on identical test data: **89.7% (87,801 / 97,821)**, not 76%
   (162,404 / 213,560). Everything measured before this fix was steering by that union.

---
**Status: provisional.** Sections B and C are sized from a *contaminated* snapshot (several
per-project cobertura files on disk date from March/April, and others came from a run that
was killed mid-flight). Nothing here should be quoted as a final figure until a clean
`Run-Tests.ps1 -Mode AiUnit -Coverage` lands.

## X. SharedSelfTest's failure arm -- one line per host, unreachable in any build that ships

`SharedSelfTest.Run()` verifies that each ILRepack-merged copy of the shared assembly behaves
like its source. Every check reports a divergence by appending to a failure list, so the
reporting arm runs *only when a copy has diverged* -- the condition the self-test exists to
detect, and one that is false in every build that passes CI. The arm is uncovered by
construction, and it is duplicated into all five hosts.

Reduced, not eliminated. The original wrote `failures.Add(...)` at each of twelve checks, so
each host carried twelve permanently-uncovered lines (23 uncovered of 107 on the HotChocolate
copy). Every check now reports through a single `_expect` helper, leaving one such line per
host. Covering even that one would mean feeding the self-test a deliberately-broken
implementation, which defeats its purpose: it is meaningful precisely because it exercises the
real merged code.

Not a candidate for `[ExcludeFromCodeCoverage]` -- the attribute is member-level, and `_expect`'s
covered guard sits on the same member as the uncovered arm.

## Y. OPEN, unproven: a narrow lost-wake window in ClaimWorker.RequestImmediatePoll

Observed once in six consecutive full-project runs of `Whizbang.Core.Tests` (2026-09-05):
`GateFlipsToAvailable_TriggersImmediatePollAsync` timed out on its 30 s safety net. It passes
scoped and passed the other five runs.

That test is built so a timeout cannot be a latency flake -- the base interval is an hour, so
within the safety net a claim can arrive ONLY because a gate transition woke the loop. A timeout
therefore means a wake was genuinely lost, not merely late.

Candidate mechanism, from reading the code, NOT yet demonstrated:

```csharp
public void RequestImmediatePoll() {
  if (_wake.CurrentCount == 0) {
    try { _wake.Release(); } catch (SemaphoreFullException) { }
  }
}
```

The check-then-act is not atomic, and the coalescing it implements is only sound if every
pending permit guarantees a poll that *begins after* the wake request. There is a window where
it does not:

1. Worker's `_wake.WaitAsync` begins consuming the permit; the decrement is not yet visible.
2. The poll it is about to run reads the gate state -- still the old value.
3. `gate.Set(true)` then `_wakeNow()` -> `RequestImmediatePoll` reads `CurrentCount` as 1
   (pre-decrement) and skips the `Release`.
4. The poll from step 2 completes without observing the new gate state, and the loop parks on
   the hour-long wait. The transition's wake is gone.

In production this reads as a worker sleeping through a gate recovery until its max interval --
exactly the symptom the immediate-poll path exists to prevent.

The standard fix is to stop using the permit count as the state: an `Interlocked.Exchange`-style
pending-wake flag that the loop clears *after* a poll has begun, re-polling when it was set
again in the meantime. That is a production change to a hot path, so it is recorded rather than
made on the strength of one observation.

Next step: reproduce deliberately -- drive the transition against a worker held at the moment of
permit consumption -- before changing anything. Do not "fix" this from the reasoning alone; the
same reasoning looked airtight for three earlier hypotheses in this session that measurement
disproved.

## Z. BacklogAgeWorker line 103 -- the disposed-timer exit, unreachable by construction

```csharp
using var timer = new PeriodicTimer(_options.Interval);
while (!stoppingToken.IsCancellationRequested) {
  try {
    if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
      return;                                  // <- line 103
    }
```

`WaitForNextTickAsync` returns `false` only when the `PeriodicTimer` has been disposed. This one
is a `using var` local, so the only thing that disposes it is the method returning -- which cannot
happen while the method is parked inside it. No caller holds a reference to dispose it early.

The remaining 13 lines of `ExecuteAsync` are covered: the disabled/nothing-wired guard, the
started log, the timer loop, the peek, and the cancellation exit. This is Case 3 from
ai-docs/coverage-exclusions.md -- a defensive branch inside an otherwise-covered member -- so it
gets no `[ExcludeFromCodeCoverage]`: the attribute is member-level and would suppress the 13
covered lines beside it.

Worth keeping rather than deleting. `WaitForNextTickAsync` genuinely has a false return, and a
loop that ignored it would spin once the timer was disposed. It is correct code guarding a state
this construction cannot currently reach.

## AA. Shutdown landing inside a database round-trip -- two workers, one line each

```csharp
try {
  await _tickOnceAsync(stoppingToken);
} catch (OperationCanceledException) {
  break;                                   // <- line 69
} catch (Exception ex) {
  LogTickFailed(_logger, ex);
}
```

The other 20 lines of `ExecuteAsync` are now covered, including the gate-cancel return and the
error arm (driven by a connection to a refused port, which also proves the loop survives a
database outage and comes back round).

Line 69 needs cancellation to arrive *while a scan is in flight* and to surface as an
`OperationCanceledException`. A refused port fails instantly, so there is no window to cancel
inside. Producing one means pointing the monitor at a black-hole address so the connect hangs,
then cancelling once the tick has demonstrably begun -- and with `DirectConnectionString` there is
no configuration read to signal that beginning, so the test would be timing-dependent on how
Npgsql surfaces a cancelled connect.

`PgDurableSignalRetentionWorker` line 74 is the same line in the same shape -- `break` when the
sweep is cancelled rather than failing -- and is uncovered for exactly the same reason. Its other
21 lines are covered, including the gate-cancel exit and the sweep-failure arm.

Tractable, not impossible -- but a test whose green depends on out-racing a network connect is
worth less than the line it covers, and this session has spent more time on flaky waits than on
the gaps they were meant to close. Recorded rather than built. If it is ever wanted, the honest
route is a seam on each worker that reports when a pass starts, not a cleverer sleep.

## AB. SlidingWindowOutboxBatchStrategy: six arms that only run while something is going wrong

The idle-eviction sweep is now covered in both directions -- an idle stream loses its buffer, an
active one keeps it -- which was the part that mattered: stream ids are unbounded, so that sweep
is the only ceiling on the buffer map. What remains is six lines, each reachable only from a
state the test would have to manufacture by breaking something:

- **128** `continue` when the batcher yields an empty batch. `SlidingWindowBatcher` does not
  publish empty batches; this is a guard against a future one that might.
- **140** `return` when the flush is cancelled *and* the strategy is stopping. Needs a flush
  suspended precisely across a `FlushAndStopAsync`.
- **150** the shutdown `catch (OperationCanceledException)` closing the drain loop.
- **155** `return` when the sweep timer fires after disposal. The timer is disposed during stop,
  so hitting this means winning a race against the disposal that is meant to prevent it.
- **163** `continue` when `TryRemove` loses to a concurrent removal of the same buffer.
- **170** the empty `catch` around awaiting an evicted stream's worker. `_drainBufferAsync`
  already catches cancellation and per-batch failures, so the worker faulting means an
  unanticipated escape from code written specifically not to.

All six sit inside members whose other lines are covered, so per ai-docs/coverage-exclusions.md
this is Case 3 and none of them gets `[ExcludeFromCodeCoverage]` -- the attribute is member-level
and would suppress the covered lines beside them.

One line from this class did leave the denominator honestly: `StreamBuffer.Reader` was dead. The
batcher is handed `channel.Reader` directly at construction, nothing ever read the property, and
the inbox sibling of this class does not declare it. Deleted rather than left as an uncoverable
line, which is the difference between removing code and hiding it.

## AC. EFCoreDeadLetterRecoveryService line 122 -- a no-rows fallback the function never produces

```csharp
await using var reader = await cmd.ExecuteReaderAsync(ct);
if (!await reader.ReadAsync(ct)) {
  return new CanaryVerdict(CanaryVerdictKind.Pending, 0, 0, 0);   // <- line 122
}
```

`evaluate_canary_campaign` is a set-returning function that yields a row for any fingerprint and
generation, including ones no campaign has ever used -- the cold-connection test calls it with
`fp-none / gen-none` and gets `Pending` back, with this branch unexecuted. So the guard fires only
if that function is one day rewritten to return an empty set.

Worth keeping. A reader that returns nothing would otherwise throw on the field access below it,
turning a schema change into an exception on a maintenance path instead of the conservative
"look again next scan" answer this returns. Case 3 from ai-docs/coverage-exclusions.md: the rest
of the member is covered, so no member-level attribute.

The other 23 lines that were uncovered on this class are now covered. All but one were the same
guard repeated across twelve entry points -- `if (conn.State != Open) await conn.OpenAsync(ct)` --
dead in the suite because every other test reaches the service through a context EF Core has
already opened, while in production a scoped DbContext resolved for a maintenance pass arrives
closed and that guard is the first thing that runs.

## AD. OPEN: 21 test databases per full run still leak, mechanism not established

Fixed and verified: `EFCoreTestBase` was dropping its per-test database with a terminate followed
by a separate DROP, which race each other, and swallowing the failure on the grounds the container
would be torn down anyway. Returning the connection pool first and using `DROP ... WITH (FORCE)`
fixed the bulk of it, and every other test-database drop in both Postgres suites now uses FORCE
too (25 files). The effect is not subtle: the EFCore suite went from **16m37s back to ~7m25s**,
matching its old baseline, because the databases were no longer piling up under it.

What is NOT explained: a full 2,675-test run still leaves exactly **21** `test_%` databases, and
the count was identical across three runs with different fixes in between. Ruled out:

- Not the base class alone -- running two base-derived classes leaves zero.
- Not the standalone classes that create their own database -- running one leaves zero.
- Not the racy DROP -- the leftovers drop instantly with FORCE afterwards, and adding a bounded
  retry to the base changed nothing.
- Not teardown hiding via inheritance -- the classes with their own CREATE DATABASE are standalone
  (`: IAsyncDisposable`), not derived, so no `[After(Test)]` is being shadowed.

Stable at 21 across runs suggests something systematic under parallelism rather than a race, but
that is a guess and it is written here as one.

Why it still matters even at 21: every leaked database brings backends whose open transactions
hold the cluster's cleanup horizon back, which is the exact condition behind issue #671. The
volume is now far below what was measured before (71 across a two-hour container), so it degrades
slowly rather than quickly.

Next step if picked up: instrument the swallowed catch to record which database failed to drop and
from which class, then read it off one unattended full run. Bisecting by running classes in
isolation does not reproduce it and has already been tried.

### AD update: the 21 correlate with suite time, but this is not a controlled measurement

Two observations, one each: the EFCore suite ran **7m25s** starting from a cleared server, and
**16m14s** starting with the 21 already present on a container that had been up three hours. The
21 did not grow during the second run -- it ended where it started -- so whatever costs the time
is the standing population plus whatever else three hours of create/drop churn leaves behind
(catalog bloat, WAL, autovacuum work spread across more databases).

Recorded as a correlation, not a cause. Confirming it means clearing the server and re-running,
which is 8-16 minutes for a data point, and nothing downstream currently depends on knowing.

It does not affect CI, where every run gets a fresh container. It affects local runs, and it is
the reason a local timing drift is worth a look rather than a shrug -- that is how the leak was
found in the first place.

## AE. The machine, not the code: memory pressure explains most of today's "flakiness"

Measured on this workstation while the suites were running: **0 GB free, 35.3 of 36.8 GB swap in
use**, a Roslyn compiler server holding 3.3 GB, and 59 dotnet processes totalling another 3.3 GB.
A full-suite run was killed outright by the OS for low memory after 1,971 tests (zero failures),
and an earlier one died at 2,079.

This retroactively explains a set of things that were being attributed to the code:

| symptom | earlier reading | what it is |
|---|---|---|
| suite 7m25s -> 16m37s -> 19m38s | leaked databases | swap thrash |
| Postgres container recreated mid-run | churn/strain | container OOM-killed |
| `EFCoreTestBase.SetupAsync` transient Npgsql failure | infrastructure noise | initializing against a server that just restarted |
| **AD**: ~21 databases leak per full run | racing DROP, fixed with FORCE | **a process killed mid-run never reaches teardown** |
| **W**: 1500 ms budget blown to 10.6 s | cross-assembly contention | a machine deep in swap |

The AD reframing is the one that matters, because it fits evidence the DROP theory never did: a
single class in isolation never leaks, the count stays roughly stable rather than scaling with
concurrency, and neither `WITH (FORCE)` nor a bounded retry moved it -- because the drop code was
never reached at all. The FORCE change is still correct and the pool-return still fixed a real
race (the suite did come back from 16m37s to 7m25s once), but it was not the whole story and the
remainder was never a Postgres problem.

W is now much less interesting as a product question. A 1500 ms latency budget on a host 35 GB
into swap says nothing about the fenced-retry path it was written to protect. Before spending any
more on it, re-run it on a machine that is not swapping; the seven failed reproduction attempts
recorded above were all made on this one.

None of this affects CI, which runs each suite on a fresh runner. It affects every local
measurement taken today, including the timing correlation recorded under AD, which should be read
as an artifact rather than a finding.


## AF. AzureServiceBusConnectionRetry: the success path needs an admin plane nothing local has

`CreateClientWithRetryAsync` went from 17 uncovered lines to 7. The retry contract is now covered
without a broker, by pointing at a refused local port: it gives up and surfaces the failure when
`RetryIndefinitely` is off, and goes past the configured budget when it is on. Both matter --
swallowing the final failure lets a host start reporting healthy with no connection behind it,
and giving up under RetryIndefinitely needs a restart before the worker can ever connect.

**76-78, 80, 87 -- the success return.** Connectivity is verified with
`ServiceBusAdministrationClient.GetNamespacePropertiesAsync`, an ADMIN-plane call. The Azure
Service Bus emulator does not implement the admin plane at all (the same reason emulator-backed
tests must run with `AutoProvisionInfrastructure=false`), so no local or CI-hosted emulator can
return success here. Covering these needs a real Azure namespace and credentials, which is a
different category of test than this suite.

**104-105 -- the every-tenth "still retrying" log.** Reachable only at attempt 10 under
`RetryIndefinitely`. Each attempt costs several seconds of wall clock because
`ServiceBusAdministrationClient` runs its OWN internal retry before surfacing a failure, and the
production code constructs that client with no options seam to shorten it. Ten attempts is roughly
fifty seconds of a unit suite that otherwise finishes in eighteen, for one log line that reports
progress rather than changes behaviour. Not worth the run time; recorded instead.

Both are Case 3 -- the members around them are covered -- so neither gets an attribute.

## AG. MessageBusToDispatcherTransformer: a real bug, and what is left after fixing it

Writing a coverage test for the type-argument branch found a defect rather than a gap.
`List<IMessageBus>` was never rewritten: the identifier's PARENT is the `TypeArgumentListSyntax`,
but the check looked at `parent?.Parent`, which is the `GenericNameSyntax`. Off by one level, so
the branch never fired. The migrated file kept a Wolverine interface while losing the using that
imported it -- it did not compile, and the migration reported success. Fixed, and the branch is now
covered by a test that asserts `List<IDispatcher>` comes out.

Remaining uncovered, all defensive:

- **71** `return root` when the root is not a `CompilationUnitSyntax`. `ParseText` always yields
  one; this is residue R in a second transformer.
- **127-139** the "add a Whizbang using from scratch" fallback, which the code's own comment
  marks unreachable: the guard above requires an exact `using Wolverine;`, which the loop always
  replaces. Kept correct so loosening that guard later cannot start emitting `usingWhizbang.Core;`
  -- the same shape, and the same reasoning, as residue Q.
- **223, 317** closing branches reached only when the identifier is literally `IMessageBus` but is
  not a type usage -- a member access or a name in a position the rewriter deliberately ignores.
- **368** the default arm of a member-name switch, taken when the member is neither a plain nor a
  generic name.

One test in this batch had to be rewritten before it meant anything. `TransformAsync` returns
early when a file contains no `IMessageBus` at all, so a "file that never used Wolverine" test
built from an unrelated class asserts an absence that the early return already guarantees -- it
passed without reaching the using logic it named. The version kept here includes `IMessageBus`
without a file-level `using Wolverine;`, which is how the type arrives via a global using, and it
does reach the branch.

## AH. ProjectionToPerspectiveTransformer: 22 uncovered down to 15, and what the 15 are

Covered this round, each a behaviour a migrated file depends on: non-Marten usings survive the
import swap (a dropped `using System.Collections.Generic;` breaks the build for a reason unrelated
to the migration), a non-projection base type is kept (a class that silently stops implementing a
marker interface fails wherever the codebase resolved it by that interface), a fully-qualified
`Marten.Events.IEvent<T>` metadata parameter is not mistaken for the handled event type, and
classes in the file that are not projections are left alone.

What remains, and why each is a poor target rather than an untested behaviour:

- **78, 85** `return root` guards -- a root that is not a `CompilationUnitSyntax`, and a file with
  no Marten import. `ParseText` always yields a compilation unit; this is residue R appearing in a
  third transformer.
- **301, 447, 483** "could not determine" returns -- `"unknown"`, and two `null`s from helpers that
  walk a base list looking for a shape the caller has already established is there. Reaching them
  means constructing a projection whose own declaration contradicts itself.
- **322, 338** `continue` arms in parameter loops, skipping shapes the surrounding code has
  already filtered for.
- **387-388** a warning emitted when neither event nor model type can be derived for a
  `ShouldDelete` transform -- same shape as above: the enclosing method only runs once those types
  resolved.
- **474-475, 479-481** the fallback in generic-argument parsing for `IPerspectiveFor<T>` written
  with ONE argument. The transformer always emits two (`IPerspectiveFor<Model, Event>`), so this
  is a guard against hand-edited or future output, not against anything the tool produces.
- **513** the default `("Delete", true)` arm of the ShouldDelete classifier, below two returns that
  already cover the shapes its callers construct.

All sit inside members whose other lines are covered, so Case 3 applies and none takes an
attribute. Worth keeping: every one of them is the conservative answer, and the alternative to a
guard here is a NullReferenceException inside a migration tool halfway through rewriting a file.

## AI. WolverineAnalyzer: 21 uncovered down to 4

Covered this round, chosen because each one changes what the migration report says about a
handler rather than whether a line ran:

- A handler in a **block-scoped namespace** is still fully qualified. The report keys on that
  name, so an unqualified one collides with every same-named handler in the solution.
- **ValueTask<T>** reports T, and a **synchronous** handler reports its declared type. Getting
  either wrong generates a receptor whose signature does not match what the handler produced.
- A **custom base class** is flagged, and non-custom bases -- an interface, `object`, the
  Wolverine interface itself -- are NOT. The second half matters as much: a warning that fires
  for every handler implementing an interface trains the reader to skip the one that matters.
- A **nested handler** is flagged however it was discovered. Wolverine finds handlers three ways
  and each is a separate branch here; a warning wired to only one leaves the other two migrating
  a nested class silently. (The first version of this test used the interface path and passed
  while the attribute and convention branches stayed dark -- the coverage check is what showed
  the assertion was answering for a different branch than the one it named.)
- A **generic message type** keeps its own type argument. A depth-blind comma split would report
  `Envelope<OrderCreated` and name a type nothing resolves.

The four left:

- **275** the `IHandle<>` branch falling through with no type argument -- a malformed interface
  the compiler would already have rejected.
- **342** `return null` from the Handle-method finder, reached only when the enclosing scan has
  already established a Handle method is present.
- **422, 437** the skips for known Marten types and for ignored base-class patterns
  (FastEndpoints). Testable, but each needs a base type named in a private allow-list, and the
  behaviour they implement -- "do not warn about this one" -- is already asserted by the
  interface/object case that covers the sibling arms.

Case 3 for all four: the members around them are covered, so none takes an attribute.

## AJ. PackageManager: 19 uncovered down to ~9

Covered this round, both cases where getting it wrong breaks a build the author did not touch:

- **Generator projects are skipped whole.** Source generators target netstandard2.0 and reference
  Roslyn, not the runtime packages; adding Whizbang references to one does not migrate it, it
  stops it compiling — and the failure lands in a project nobody edited. Asserted by leaving even
  the stale Wolverine reference in place, and by reporting no change for that project, so the
  author is not sent looking for an edit that was deliberately not made.
- **A package with no Whizbang equivalent is removed from central versions**, and reported as
  removed. Central package management splits a reference across two files; leaving the version
  entry after the reference is gone is dead configuration that outlives the migration, and
  nothing in the migrated solution mentions the package again to explain it.

What is left is guards and loop skips: a project path that does not exist (unreachable from the
discovery path, which only returns files it globbed), early `return changes` arms, and `continue`
arms for entries with no Include attribute or already present in the target set. Case 3
throughout — the surrounding members are covered.

## AK. CollectiveSettersRewriter: 13 uncovered down to 7

Covered this round, both cases where the caller is doing something legal and the failure would be
confusing:

- **An explicitly object-typed selector.** `SetProperty` infers TProp, so a selector normally
  arrives unwrapped — but written as `SetProperty<object>(j => j.ViewCount, 42)`, which is what a
  shared helper or a loop over heterogeneous setters produces, the compiler boxes the access into
  `Convert(j.ViewCount, object)`. Unstripped, the body is a UnaryExpression rather than a
  MemberExpression and the lookup reports it cannot find a property that is plainly there.
- **A value the rewriter cannot read** now has a test asserting the error names RawSql. This runs
  while building an UPDATE, and an operator told only that "an expression node kind is
  unsupported" has no way to discover which spec kind accepts richer value sources.

The seven left:

- **134-135** the arity guard on `SetProperty`. The interface declares exactly one overload, and
  it takes two arguments, so this is a guard against an overload that does not exist yet.
- **180-181** the loop body of `_stripConvert`, reachable only through the computed-comparison
  path with an operand the compiler wrapped — an enum or nullable comparison. The test model has
  neither, and adding one to reach two lines buys less than it costs in a shared fixture.
- **188** the bare-`LambdaExpression` arm of `_unwrapLambda`. A lambda passed as an argument
  inside an expression tree arrives Quoted, so the unquoted arm is for a tree built by hand.
- **189-190** its throw, for a selector that is not a lambda at all — which the strongly typed
  `Expression<Func<TModel, TProp>>` parameter makes unconstructible from C# source.

Case 3 throughout; the surrounding members are covered.

## AL. AsbTrafficClassOpsRateSource: 11 lines behind a projection only a live subscription sets

`Project()` walks the transport's namespaces and asks each for its idle ops-rate projection. Every
uncovered line — the rate contribution itself and the whole of `_trafficClassFor` — sits past this
guard:

```csharp
if (transport is not AzureServiceBusTransport asb
    || asb.IdleOpsRateProjection is not { } projection) {
  return;
}
```

Two things make that unreachable from the unit suite. The check is against the CONCRETE
`AzureServiceBusTransport`, not an interface, so no fake satisfies it. And
`IdleOpsRateProjection` is get-only over a field assigned in exactly one place — the private
`_reevaluateIdleOpsProjection`, which runs when a session subscription is established. A transport
constructed in a test has never subscribed, so the projection is null and `_add` returns before
doing anything.

The existing tests cover what they can: a non-ASB transport contributes nothing, which is the
behaviour that matters most (reporting a zero would read as "idle and free" on a namespace that is
simply unmeasured).

Reachable in principle from the integration suite, where a real subscription would populate the
projection. Not reachable by adding a test here, and the alternative — widening the guard to an
interface or exposing a setter purely so a test can reach it — changes production shape to serve
coverage, which is the trade this loop has declined elsewhere.

## AM. Three `root is not CompilationUnitSyntax` guards, and one switch arm C# cannot produce

Four lines across three classes, all the same shape: a defensive arm guarding a state the
type system upstream has already ruled out.

**`GuidToTrackedGuidTransformer` lines 87 and 125**, **`NewtonsoftToSystemTextJsonTransformer`
line 38** — `if (root is not CompilationUnitSyntax) { return root; }`. Every caller obtains
`root` from `CSharpSyntaxTree.ParseText(...).GetRoot()`, which returns a `CompilationUnitSyntax`
for any input, including empty text and text that fails to parse. Nothing in these transformers
constructs a root any other way. Case 3: the guard sits inside otherwise-covered members, so no
member-level `[ExcludeFromCodeCoverage]` applies.

**`DapperCollectiveSpecCompiler._setterVisitor` line 189** — the `LambdaExpression direct => direct`
arm of `_unwrapLambda`. The rest of that switch is now covered: the `Quote` arm by ordinary specs,
and the `default` throw arm by a hand-built tree passing a `ConstantExpression` as the selector.
The middle arm is the one that cannot be reached. `SetProperty`'s first parameter is typed
`Expression<Func<TModel, TProp>>`, so a lambda written in source is always wrapped in
`UnaryExpression{Quote}` by the compiler, and `Expression.Call` will not accept a bare
`LambdaExpression` for that parameter either — it quotes it or it throws at tree-construction
time, so `Arguments[0]` is never an unquoted lambda. Reaching it would take reflection into
private framework state, which would demonstrate nothing about the compiler's behaviour.

Everything else in that class is now covered: 541 tests in the Dapper suite, one line left.

## AN. Whizbang.LanguageServer/Program.cs: 22 lines, the whole file, resolved by exclusion

Every line of the language server's entry point was uncovered, and all 22 are the same
thing: top-level statements that bind the LSP server to the process's own standard input
and output and then block on `WaitForExit` until the editor closes the connection. A test
cannot run that. Doing so would take over the test host's console streams and never return.

This is case 2 in ai-docs/coverage-exclusions.md — the whole member is unreachable, not one
branch inside a covered one — so the attribute fits rather than a residue note alone. Applied
via a `partial class Program` declaration carrying
`[ExcludeFromCodeCoverage(Justification = ...)]`; the compiler emits the synthesized
entry-point class as partial, so the attribute reaches `<Main>$` and the two logging closures
nested in it. Verified by running the suite with coverage and confirming no `Program*` class
appears in the cobertura output at all, with all 89 tests still passing.

Worth stating why this is not hiding a gap: the file makes exactly one decision,
`LanguageServerServices.ResolveDocsBaseUrl()`, and registers services through
`AddLanguageServerServices`. Both are exercised directly by
`tests/Whizbang.LanguageServer.Tests/LanguageServerServicesTests.cs`. What the attribute
suppresses is the OmniSharp wiring and two log lines.

Note this does NOT generalise to `tools/Whizbang.Migrate/Program.cs`, which also shows
uncovered lines. That one has real command parsing with covered lines in the same members, so
a member-level attribute there would suppress genuinely tested code — the exact thing the
policy's one hard constraint forbids. It stays on the worklist as ordinary untested code.

## AO. DeadLetterRecoveryWorker and PerStreamSerializer: what is left, and one measurement trap

DeadLetterRecoveryWorker went 23 uncovered -> 6; PerStreamSerializer 14 -> 6. What remains
splits three ways, and the first is a warning about the worklist itself.

### Line 154 is covered. The report is wrong about it.

`DeadLetterRecoveryWorker.cs:154` is the `return;` inside
`catch (OperationCanceledException)` around `_schemaReadyGate.WaitForReadyAsync`. Run
`ShutdownBeforeTheSchemaIsReady_ExitsQuietlyAsync` **alone** under coverage and line 154 records
a hit. Run its whole test class and line 153 -- the catch clause itself -- records a hit while
154 records zero. The handler demonstrably executes either way; only the attribution of the
`return` changes.

That is an async state machine artifact: a `return` inside a `catch` compiles to a jump to the
method's shared exit, and which sequence point that jump is attributed to depends on which other
paths ran. Do not spend another cycle writing a test for this line. More usefully: **a line in
the worklist that sits on an early `return` inside a `catch` in an `async` method may already be
covered**, and the way to tell is to run its one test in isolation and compare.

### Genuinely unreachable

`DeadLetterRecoveryWorker.cs:227` -- `catch (OperationCanceledException) { break; }` around
`await Task.WhenAny(pollDelay, wakeTask)`. `Task.WhenAny` completes successfully as soon as any
constituent reaches a terminal state, whatever that state is, and the result is never unwrapped
here. `Task.Delay` and `SemaphoreSlim.WaitAsync` both return an already-cancelled task rather
than throwing synchronously on a pre-cancelled token. So nothing on that line can throw
`OperationCanceledException`; cancellation is observed on the next iteration of the enclosing
`while (!stoppingToken.IsCancellationRequested)`. Defensive code, not a gap.

`PerStreamSerializer.cs:166` -- `if (!stream.Reader.TryRead(out var first)) { continue; }`. The
channel is created `SingleReader = true` and this worker is its only reader, calling
`WaitToReadAsync` and then `TryRead` on the same task with nothing in between. The single-reader
contract already excludes the state.

### Needs a decision, not a test

`DeadLetterRecoveryWorker.cs:244-247` -- the loop breaker's close path. `_isBreakerOpen` reads
`DateTimeOffset.UtcNow` directly and the class takes no `TimeProvider`. `LoopBreakerCooldownMinutes`
is an `int` whose smallest useful value is 1, so closing the breaker needs a real sixty-second
wait. Covering it means adding a clock seam to production, which is the owner's call, not this
loop's. Everything else about the breaker -- opening it, and not opening it on a genuine backlog
-- is covered.

`PerStreamSerializer.cs:197-198, 200, 225` -- these need a pending channel read to observe
cancellation by *throwing*, rather than being resolved gracefully by the `TryComplete()` that
`FlushAndStopAsync` always performs first. Whether it throws is a race inside `Channel<T>`.
Measured evidence that it really is a race: two consecutive full runs of the same suite in this
session reported different subsets of these lines as covered (one run hit 197 and 200, the next
did not). Any test written for them would be exactly that flaky.

`PerStreamSerializer.cs:239` -- `TryRemove(KeyValuePair)` losing its race, reachable only by two
concurrent sweeps hitting one entry. Scheduler-dependent; not worth a flaky test for one line.

## AP. AzureServiceBus ServiceCollectionExtensions: 11 lines behind a real admin round-trip

`ServiceCollectionExtensions.cs:146-156` is the `ServiceBusClient` factory lambda inside
`_addTransport`, invoked only when no `ServiceBusClient` has been pre-registered. It calls
`AzureServiceBusConnectionRetry.CreateClientWithRetryAsync`, which constructs a
`ServiceBusAdministrationClient` and calls `GetNamespacePropertiesAsync` — a management-plane
round trip with no seam to substitute, against a plane the local emulator does not implement
(see AF).

Worse than merely unreachable: on a failure it classifies as transient it retries indefinitely
by default (`RetryIndefinitely = true`), so a test pointed at a bogus host would hang rather
than fail cleanly. This is why the file's own test class documents pre-registering a
`ServiceBusClient` as the standing convention.

Everything else in the file is now covered, including the pieces that needed a resolve rather
than a registration assertion: the namespace client factory singleton, the backlog peek and
traffic-class ops-rate sources composed over the resolved transport, the multi-namespace peer
initialization logging, both arms of the active-consume-namespace projection, and the
configuration-only namespace being merged into a composite provisioner.

## AQ. RabbitMQ ServiceCollectionExtensions: the connection factory needs a real broker

`ServiceCollectionExtensions.cs:133-154` is the `AddSingleton<IConnection>` factory body, entered
only when no `IConnection` has been pre-registered. It calls
`RabbitMQConnectionRetry.CreateConnectionWithRetryAsync`, which calls the concrete
`RabbitMQ.Client.ConnectionFactory.CreateConnectionAsync()` — a real socket connect with no seam
to substitute.

Worth noting precisely because the sibling path does have one: per-namespace connections go
through `IRabbitMQNamespaceConnectionFactory`, which is why every multi-namespace test in this
suite runs offline. The default connection has no equivalent interface. If that were ever
extracted, these lines become ordinary unit-testable wiring; until then they belong to the
integration suite.

Everything else in both files is covered. `RabbitMQTransport` went from 14 uncovered to none of
the targeted set, including the passive-declare path for a destination that requires a
pre-provisioned entity, correlation and causation IDs reaching the wire headers, the batch-flush
debug log, the nack path when reading a message's own properties throws, the comma-split routing
fallback when a RoutingPattern override is present but empty, and idempotent double dispose.

One line needed a real subscribe rather than a direct call: the closure passed to
`NamespaceRoutingTransport` at line 288. Its two services are resolved once at container build,
so testing the static helper it calls proves the logic and not the capture — a wiring mistake
there would leave the helper correct and the mirror permanently blind.

## AR. Two dispose races, and three lines that are dead rather than untested

### The `ObjectDisposedException` race, now seen twice

`ClaimWorker.cs:294` and `PerStreamSerializer.cs:294` are the same shape, and it is worth
naming as a class rather than rediscovering each time. Both are an empty
`catch (ObjectDisposedException)` guarding a `Cancel()` on a `CancellationTokenSource` that the
owning loop's teardown may have disposed in between another thread's `Volatile.Read` of the
field and its `.Cancel()` call. Reaching it requires hitting that exact interleaving from
outside, and neither class exposes a seam that would let a test place itself there.

Both are correct code — the race is real and the catch is why it is harmless — and both are
untestable without adding a production hook that exists only for the test. Recorded rather than
faked with a sleep, which is what a test for this would amount to.

### IntegrityManifestReceptors: dead, not untested

`IntegrityManifestReceptors.cs:835` is a guard inside `_sendBulkBackfillRequestAsync` checking
transport, serializer, requester and topic. Its only caller, `_handleTypeLevelAsync`, performs
the byte-identical check on the same service provider and options at line 690-692 and returns
before it can ever invoke this method. Singleton resolution is deterministic, so the condition
cannot be true when reached. Note the neighbouring lines 839-840, which guard the *tracker's*
origin topic, are genuinely reachable and are now covered — the two look alike and are not.

`IntegrityManifestReceptors.cs:814` is the `streamLevel: true` arm of a ternary in
`_tableDigestsWithFallbackAsync`. That private helper has exactly one call site, which hardcodes
`streamLevel: false`. Reaching the other arm means reflection-invoking a private method no
caller reaches, which would assert an implementation detail rather than a behaviour.

`IntegrityManifestReceptors.cs:589-590` clears `_pagesFollowed` when it exceeds 256 entries.
That dictionary is `private static readonly` — process-global, and NOT scoped by this class's
`[NotInParallel]` key. Driving it past the cap, whether by real round trips or by seeding it
through reflection, would clear it out from under any other test in the 46-project suite
accumulating entries in the same dictionary at the same moment. Declined deliberately: covering
one safety-valve line is not worth manufacturing cross-test flakiness, and there is no
externally observable invariant here that does not amount to poking the field.

## AS. PerspectiveWorker: dead wiring, two tests that pass for the wrong reason, and one more instrumentation artifact

Entry L already settled 18 of this file's uncovered lines. This round closed 10 more with real
assertions. Three findings from the attempt are worth keeping, because none of them is a
coverage question.

### Lines 460-461 are not untested — the wiring they perform does nothing

```csharp
if (workChannelWriter is not null) {
  workChannelWriter.OnNewPerspectiveWorkAvailable += RequestImmediatePoll;
}
```

`RequestImmediatePoll` releases `_pollWakeSignal`. That semaphore is declared at line 300,
released at line 354, and **awaited nowhere in the file**. Verified by grep: two references
total, the declaration and the release. `ClaimWorker` has the working version of this same
pattern — `RequestImmediatePoll` → `_wake.Release` → a `WaitAsync` that actually returns — so
this looks like the copy left behind when, as the comment three lines below says, "the work-pump
decomposition migrated perspective traffic to the channel architecture."

So these lines are reachable — a test need only supply a non-null writer — but there is no
invariant to assert, because subscribing changes nothing observable. A test here would assert
that a line ran, which is the thing this loop exists to avoid.

**This is a question for the owner, not a coverage item.** Either the perspective poll loop was
meant to wake on new work and silently no longer does, or the wiring is vestigial and should
go. Deliberately not removed here: deleting production wiring is not a change to make on a
coverage branch, and a reader today is reasonably misled into believing new-work signals wake
this loop.

### Two existing tests were passing for the wrong reason

`Worker_WithoutEventTypeProvider_SkipsEventLoadingAsync` and its `...Empty...` sibling never
register an `IReceptorInvoker`, so `shouldLoadEvents` is false and `_loadProcessedEventsAsync` —
the method they are named for — is never entered. They pass without reaching the code under
test. Replacements registering both `IReceptorInvoker` and `IEventStore` now cover lines
3760-3761 and 3768-3769, keyed on the unique `LogWarningNoEventTypes` EventId as the signal.

Same shape as `Worker_RegistryNotRegistered_SkipsPerspectiveAndContinuesAsync`, whose assertion
is `ConsecutiveEmptyPolls >= 0` — a tautology. Its replacement proves the branch is taken for
each of two concurrent stream items rather than merely that nothing crashed.

### Line 1162 is another instrumentation artifact, not a gap

It is the closing brace of a `catch` that ends in an unconditional `throw;`, and
`Worker_PerspectiveRunThrows_ReportsFailureViaStrategyAsync` demonstrably executes that catch
(proven by `ReportFailureCallCount`) while the line stays red. Same family as AO's line 154:
a sequence point after an unconditional transfer, attributed unpredictably. Do not write a test
for it.

### Unreachable by construction, traced to their call sites

`3735` — `_startLockKeepaliveAsync`'s `if (_streamLocker is null) return;`. Its single call site
runs only when `lockAcquired` is true, which itself requires a non-null locker.
`3628` and `3660` — null-invoker guards in the detached-stage fire paths, reached only after the
caller resolved a non-null invoker from the same root provider.

### Left for a future round, honestly rather than gold-plated

`1228, 1236, 1301, 1310, 1320` (stream-affinity eviction guards), `1563, 1648, 1694, 1739, 1803,
1852` and `2016, 2017` (drain-mode refetch guards), `3538`, `3637-3638`. All reachable in
principle; all need a full drain-mode or lifecycle fixture for one guard clause each. Of these,
**3637-3638 is the best next candidate** — the catch-and-log in the detached PrePerspective
fallback is a real gap rather than unreachable code.

## AT. Three things the round-24 integration turned up that are not coverage items

### PerspectiveWorker's missing-registry branch is not reachable from its test harness

`PerspectiveWorker.cs:2660-2661` logs and returns when `IPerspectiveRunnerRegistry` is absent.
A test for it was written and then removed, because work enqueued through
`PerspectiveWorkerTestHarness` never reaches `_resolveDependenciesAndLoadEventsAsync` at all:
`LogPerspectiveRunnerRegistryNotRegistered` (EventId 11) is never emitted. Verified by waiting on
it for ten seconds, for one emission and for two, three runs each — zero every time.

The reason this went unnoticed is worth more than the line. The existing
`Worker_RegistryNotRegistered_SkipsPerspectiveAndContinuesAsync` is named for this branch and
asserts `ConsecutiveEmptyPolls >= 0` — true whether or not the branch is ever reached. It has
been passing without executing the code it is named for. Left in place rather than deleted, but
it should not be read as evidence this path works.

### ClaimWorker treats a perspective-only batch as an empty poll

`_distributeAsync` is called only `if (hadWork)`, and `hadWork` is:

```csharp
batch.OutboxWork.Count > 0 || batch.InboxWork.Count > 0
  || batch.PerspectiveStreamIds.Count > 0
  || batch.OutboxStreamIds.Count > 0 || batch.InboxStreamIds.Count > 0
```

`batch.PerspectiveWork.Count` is not among them, while `batch.OutboxWork` and `batch.InboxWork`
are. A batch carrying perspective rows but no `PerspectiveStreamIds` therefore reads as an empty
poll and is never distributed — the rows stay leased to this instance and nothing consumes them
until the lease expires. Found because a test constructed exactly that batch shape and timed out.

Today's stores populate both lists, so this is latent rather than live, and the asymmetry may
well be deliberate. Flagged for the owner rather than changed: the fix would be a one-word edit
to a claim-loop predicate, which is not a change to make on a coverage branch.

### A closing brace after `throw;` is unreachable for instrumentation, again

`Dispatcher.cs:3235` and `3337` are the closing braces of `catch` blocks whose last statement is
an unconditional `throw;`. Tests drive both catches and assert the rethrown exception, and both
lines stay red — the same shape as AO's `DeadLetterRecoveryWorker.cs:154` and AS's
`PerspectiveWorker.cs:1162`. That is now four instances. **Treat `}` after an unconditional
`throw` or `return` inside an async method as instrumentation noise, not a gap**, and do not
spend a cycle on it.

## AU. AzureServiceBusTransport: three lines defending against states the type system forbids

28 of this class's 31 uncovered lines are now covered, offline, against fakes. What is left:

`AzureServiceBusTransport.cs:1682` and `1761` — `default: throw new InvalidOperationException(
$"Unknown AsbReceiveAction: {decision.Action}")`. `AsbReceiveAction` has exactly four members and
all four are handled above. The `_decisionMaker` that produces the value is a private field with
no injection point, so no test can hand the switch an out-of-range enum value.

`AzureServiceBusTransport.cs:2041` — `if (_adminClient == null) throw ...` inside
`_applyCorrelationFilterAsync`. Its single call site in the repo,
`_applyCorrelationFilterFromMetadataAsync`, already guards with `if (_adminClient != null)` before
calling it.

Worth recording what this round proved *is* reachable, since the emulator's missing admin plane
(AF) makes it tempting to assume otherwise: the throttle pause and its detached resume, the
resume-failure path including that `EndPause()` still runs in the `finally`, the adaptive-acceptor
resize failure and the sweep surviving it, and the sender-cache double-checked lock under two
genuinely interleaved first callers. All driven by fakes with `AutoProvisionInfrastructure=false`.

One test needed a fix at integration and the reason generalizes: with a `FakeTimeProvider`,
`Advance()` fires the periodic tick synchronously. Anything the assertion depends on — the
injected failure, the log subscription — has to be armed *before* the clock moves, or the single
sweep the test gets happens before the test is watching, and the wait then hangs to its timeout
rather than failing with a useful message. Bound every such wait with `WaitAsync`.

## AV. PostgresDeadlockRetry: 11 uncovered down to 1, and why the sibling is harder

`PostgresDeadlockRetry.cs:94` — `throw new InvalidOperationException("Unreachable")` after the
retry `for` loop in the generic overload. The loop returns on success, retries while
`attempt < maxAttempts`, and rethrows when `attempt == maxAttempts`, so control cannot leave it
normally. The compiler needs the statement; nothing can execute it.

Everything else in that class is covered. The gap was the same shape found in `ReceptorInvoker`:
every log call sits behind `if (logger is not null)`, and the existing `PostgresDeadlockRetryTests`
never pass a logger. The retry behaviour was well covered; what it *reports* was not covered at
all, and the generic overload's entire exhaustion path — log and rethrow — had never run.

That matters more than a line count here. A deadlock retry that succeeds is invisible to the
caller by construction, so the warning is the only evidence a database is thrashing; and it has to
carry the SQL state, because 40P01 and 40001 are both retried by this code and point at different
remedies. Exhaustion has to be Error rather than Warning, since that is the line an alert fires
on, and it has to carry the exception.

### Not attempted: PostgresConnectionRetry (11 uncovered)

Same "log only when a logger was supplied" shape, but not reachable the same way.
`PostgresConnectionRetry` constructs a real `NpgsqlConnection` and calls `OpenAsync`, and its
schema path calls `_isSchemaReadyAsync`, which opens one too. Lines 77-78 and 114-115 log only
when a *later* attempt succeeds (`attempt > 1`), which needs a connection that fails once and then
works — not producible against a bogus host, which fails every time.

Tractable in the live-Postgres suite: point the first attempt at a closed port, then at the real
fixture connection string. Left for a round that is working in that project, rather than standing
up a database fixture in the Dapper unit suite for it.

## AW. SerialExecutor: two defensive catches the source itself labels "should never happen" — verified

Both remaining blocks in `src/Whizbang.Core/Execution/SerialExecutor.cs` carry a `DEFENSIVE:
Should never happen` comment. The rule here is to prove that rather than believe it, so both were
traced to their call graphs.

**`217-223`** — `catch (Exception ex)` around `workItem.ExecuteAsync(workItem.State)` in
`_processWorkItemsAsync`. That delegate is never supplied by a caller: `WorkItem` is a
`private readonly struct` with no public constructor or enqueue path, and the only entry point,
`ExecuteAsync<TResult>`, always installs `_executeWithPooledStateAsync<TResult>`. That method
wraps the handler in `try { ... } catch (Exception ex) { state.Source.SetException(ex); }
finally { state.Reset(); ExecutionStatePool<TResult>.Return(state); }` — a throwing handler is
captured and handed to the caller's value task, never propagated to the worker. The only way to
reach the outer catch is for `SetException`, `Reset` or the pool return to throw, which are
internal-state failures with no route from the public surface.

**`185-190`** — `catch (OperationCanceledException)` around `await _workerTask` in `DrainAsync`.
Reaching it needs the worker's `ReadAllAsync(ct)` to observe cancellation *after*
`Writer.Complete()` has already run, and after completion the reader drains what remains and
finishes normally. Producing it means racing `DrainAsync` against whatever cancels the internal
token — a scheduler-dependent interleaving, which is precisely the flaky-test shape declined
elsewhere in this file (see AO on `PerStreamSerializer`).

Both are correct code, and both already do the right thing when they do fire: they record to
`WhizbangActivitySource` rather than swallowing silently, so the condition is observable in
production even though it is unreachable from a test. Category A — reports rather than strands.

The rest of this class is covered, including the neighbouring branch that looks identical and is
not defensive at all: a work item whose token is canceled after queueing but before execution.
Its comment says so explicitly, and the reason it matters is worth keeping — only the execute
path completes the value-task source, so skipping such an item without finishing it would hang
the caller's `await` with no exception and nothing logged.

## AX. Migrate CLI: two of rollback's three messages cannot be reached, and why that will matter later

`tools/Whizbang.Migrate/Program.cs:281` and `286-288` are the `--list` branch and the
neither-argument-nor-list branch of the `rollback` handler. Only the middle branch,
`else if (checkpoint != null)`, is reachable.

The cause is an arity subtlety worth writing down. The argument is declared
`new Argument<string?>("checkpoint", ...)`, and the `?` reads as optional — but nullable
reference annotations are erased at runtime, and System.CommandLine's `ArgumentArity.Default`
decides optionality via `Nullable.GetUnderlyingType(type) != null`, which is false for
`string`. With no default value supplied either, the argument's arity is `ExactlyOne`: the
checkpoint is **required**. `rollback --list` and bare `rollback` therefore fail in
System.CommandLine's own parse-error middleware, which sets a `ParseErrorResult` without calling
the next middleware — so `SetHandler`'s delegate never runs.

**Today this is cosmetic.** Every branch of this handler writes "not yet implemented" and exits
1, so `rollback --list` fails either way; the user just gets "Required argument missing" instead
of the message the author wrote. The existing `Rollback_ListingCheckpoints_...` and
`Rollback_WithoutCheckpointOrList_...` tests pass for exactly this reason — they assert a
non-zero exit, and System.CommandLine's parse error supplies one. They do not reach the lines
they appear to be about.

**It stops being cosmetic the day rollback is implemented.** `--list` is documented in the
option's own description and will still never reach the handler. Whoever implements it needs to
give `checkpointArgument` an explicit `ArgumentArity.ZeroOrOne` (or a default value) first, or
the listing feature will be unreachable from the command line while looking correct in code.

Not changed here: altering a command's argument arity is a behavioural change to a shipped CLI,
not a coverage edit.

## AY. ReceptorDiscoveryGenerator: one block is dead code, the rest defend against inputs the generator itself cannot produce

Five lines closed with real assertions; the remaining sixteen split into three kinds, each
traced rather than assumed.

### Dead code with zero live callers — worth deleting, not testing

`ReceptorDiscoveryGenerator.cs:1817-1820` is the `else` branch of `_buildReceptorInvocationsCore`,
guarded by its `useStageFiltering` parameter. **Both** call sites — lines 1766 and 1781 — pass
`useStageFiltering: true`. Verified by reading every call site: the parameter is effectively a
constant and the `else` can never run. This is not residue in the usual sense; it is a parameter
and a branch that could be removed outright. Left alone here because deleting production code is
not a coverage edit, but it should not sit on a worklist as though a test could fix it.

### Guards against inputs the generator constructs itself

`849-850` — a bare `"Whizbang.Core.Dispatch.Routed<"` prefix check in `_unwrapRoutedTypeString`.
Every `ResponseType` string it inspects is produced by `ToDisplayString` with a fully-qualified
format, which always emits `global::` for a namespaced type. The branch is coded to detect a
shape its own input format cannot produce.

`1070` — an `IsNullOrEmpty` guard in `_addTupleElement`. A valid C# tuple has at least two
elements and Roslyn never renders an empty element substring.

`1224` — `parts.Length != 2` in `_reportLikelyNotInjectableReceptors`, where the string being
split is always built as `name + "|" + type.ToDisplayString()`. Neither a C# identifier nor a
type display string can contain `|`.

`1972`, `2001`, `2016` — null fallbacks in `_generateReceptorInfoEntry` /
`_extractReceptorInfoFromSnippet`. All four embedded snippet templates in
`Templates/Snippets/DispatcherSnippets.cs` carry a well-formed `ReceptorInfo(` marker with
balanced parentheses; only editing that shipped template could trip these. This is the same
argument already recorded for the sibling `_generateReceptorInfoEntryManually`.

### Roslyn-contract guards (residue M shape)

`170` — `context.Attributes.FirstOrDefault()` null guard inside a `ForAttributeWithMetadataName`
transform, where the API guarantees a non-empty collection for any node that reaches it.
`RawReceptorDiscoveryGenerator.cs:50` — `GetDeclaredSymbol(...) is not INamedTypeSymbol`,
structurally identical to `RoslynGuards.GetClassSymbolOrThrow`, which this codebase already
documents as indicating a compiler bug and not worth a test.

### Reachable in principle, declined for a harness reason worth recording

`428`, `653`, `672` need an attribute whose bound constructor argument is int-valued while the
constructor parameter symbol being inspected is not an `INamedTypeSymbol`. The real
`FireAtAttribute` and `DefaultRoutingAttribute` each declare exactly one constructor taking a
proper enum, so this cannot arise from legitimate use. Producing it means shadow-declaring a
second, differently-shaped constructor on a type with the same fully-qualified name — which
collides (CS0433) because the test harness references the real `Whizbang.Core` assembly. A
fully self-hosted compilation could do it, but the result would also depend on Roslyn's
`GetMembers()` declaration order, which is not a contract worth building a test on.

## AZ. EventEnvelopeJsonbAdapter: 11 down to 1, and one defence that stops halfway

`EventEnvelopeJsonbAdapter.cs:182` — the `perspectiveScopeTypeInfo == null` early return in
`_tryParsePerspectiveScope`. Reaching it needs `_jsonOptions.GetTypeInfo(typeof(PerspectiveScope))`
to return null, but `JsonSerializerOptions.GetTypeInfo(Type)` throws rather than returning null when
no resolver can supply the type, so the only way to produce a null there is a custom resolver that
deliberately answers null for this one type — a shape no real composition builds. Note the sibling
lookups in the same class do not even have this guard: they use `?? throw`.

### The finding: the scope-column defence is asymmetric

`_parseScopeValues` tries `_tryParsePerspectiveScope` first and falls back to `_tryParseLegacyScope`.
The first wraps its deserialize in `try { ... } catch (JsonException) { }`. **The second does not**,
and neither does any caller up to `FromJsonb`.

So a scope column whose contents are *wrong-shaped but valid* JSON degrades gracefully — the new
parser throws, is caught, and the legacy parser returns no values (now covered). But a column whose
contents are *malformed* JSON propagates a raw `JsonException` out of `FromJsonb`, killing the read
of an otherwise intact event. The event's own data and metadata are untouched; only an auxiliary
column is unreadable, and the row never changes, so every retry fails identically.

Deliberately not asserted as a test. Writing one would cement the behaviour, and the catch is
plainly meant to cover both parsers — the fix is a `catch (JsonException)` on the legacy path too,
which is a production change and the owner's call.

Everything else in the class is covered, including the paths that matter for reading old rows: a
metadata document written before hops existed (no `hops` key at all, not an empty array), a scope
column holding the literal `null`, and the non-generic `FromJsonb` refusing with a message that
names the generic overload to call instead.

## BA. MessageJsonContextGenerator: 28 down to 11, and one adjacent gap worth an owner's look

Eleven lines remain, none of them the internal-fault shape residue M describes. They fall into
three groups, each traced to a call graph or an API contract rather than assumed.

**Guaranteed by an API contract:**
`206` — `attribute is null` inside a `ForAttributeWithMetadataName` transform, which the framework
only invokes when the attribute is present. `3003` — `containingNamespace == null`; Roslyn returns
the global namespace, never null.

**Guaranteed by the shape of a string this generator itself produced:**
`2195`, `2220-2221` — a matched collection prefix with no closing `>`, impossible for any name
Roslyn's fully-qualified format emits for a closed generic. `2248` — a `Dictionary<K,V>`-shaped
string with no top-level comma. `3131` — fewer than one type argument after a predicate that
already matched a `"TModel, TEvent"` prefix, where every matching arity has at least two.

**Provably dead:**
`2292`, `2297` — collection and array checks inside `_extractDirectPropertyType`, which only runs
after `_extractElementType` returned null on the *same* string, having applied the identical prefix
and `EndsWith("[]")` tests. The condition cannot be true by the time control arrives.

**Blocked by "never guess":**
`3212` — `ConstructorArguments.Length == 0` for a `[JsonDerivedType]`. The attribute has no
parameterless constructor, so zero arguments is CS7036 in valid source; reaching it needs a
deliberately broken compilation whose `AttributeData` shape under Roslyn's error recovery was not
verifiable without running one.

### Adjacent gap found while working (not a coverage item)

`3614` — `_buildPolymorphicRegistry` handling zero concrete derived types after filtering — is
reachable, via an abstract type that arrives through perspective `TModel`/`TEvent` discovery. That
path is the one message-discovery route that does **not** filter `IsAbstract`, unlike every other.
The same omission means the generator will also emit a factory for that abstract type, which is
CS0144 in the generated code. Worth an owner's look: the fix is an `IsAbstract` filter on the
perspective discovery path, which would make 3614 unreachable rather than merely untested.

## BB. Three lines left after the worker/registry batch, and two lessons that cost real time

### ServiceBusConsumerWorker 226-227 — the idle wait cannot be made to fault

The `catch (Exception ex)` around `ExecuteAsync`'s idle wait, reached only when the wait faults
with something other than `OperationCanceledException`. A test was written on the premise that
`Task.Delay(Timeout.Infinite, token)` throws `ObjectDisposedException` when the token's source was
already disposed. **It does not** — the delay simply never completes, so the test hung to its
timeout rather than failing. Removed. No seam in the current API makes that wait fault any other
way.

Worth generalizing: a test whose premise is unverified BCL behaviour fails by *hanging*, not by
asserting. Bound every wait, and treat "no output at the timeout" as a wrong premise rather than
a slow machine.

### JsonContextRegistry 143-145 and 830/855

`143-145` — the `_resolvers.IsEmpty` throw in `CreateCombinedOptions`. `_resolvers` is a
process-global `ConcurrentQueue` filled by `[ModuleInitializer]`s before any test runs, with no
unregister or reset API. Emptying it means reflecting into the private static field, which would
permanently break every other test in the assembly that depends on Core's registered contexts.
Declined for the same reason as AR's `_pagesFollowed`: covering one line is not worth corrupting
shared state the rest of the run depends on.

`830`, `855` — the true arm of `Setter = _setter != null ? ... : null` in `_createProperty` and
`_createPropertyWithTypeInfo`. All three production call sites hardcode `null` for that argument.
Reaching the other arm means reflecting into a private method with a synthetic delegate no real
path produces.

### A coverage subtlety that nearly sent a cycle the wrong way

`WorkerPipelineExtensions.cs:1067` reported as **hit** while the log it contains never happened.
The statement is `lifecycleLogger?.LogError(...)`: the null check executes and the line counts as
covered whether or not the call runs. A `?.` on a line makes "covered" mean "the receiver was
evaluated", not "the call happened" — which is exactly the gap a log-assertion test exists to
close, and exactly why the count-based assertion around it had to be replaced with one keyed on
message content.

Related, and the reason the first assertion failed: the pre-distribute stage reports its own
failure and does not rethrow, so the callback's pre-store catch never observes it. Only the
post-store catch does. The invariant still holds and is now asserted — a failing lifecycle stage
never blocks the outbox store, and the post-store failure says the store already happened so a
reader does not retry a batch that is safely persisted.

## BC. Round-24 measurement, and a correction to how AE gets used

Full `-Mode Ai -Coverage`, **completed whole**: no PARTIAL, **zero truncated projects**, 46/46
projects, 21,133 tests, **0 failures**. So the number is comparable.

**97.8% (115,058 / 117,602)** — up from 97.4% (114,631) at round 23's measurement and 97.2% at
run 20. Raw uncovered 2,971 -> 2,544. Deduped worklist 2,186 -> **1,942**; classes carrying eight
or more uncovered, 88 -> **76**.

### Correcting myself on the timing, because the wrong version of this is the dangerous one

This run took **79m36s** against the previous **44m05s**. Mid-run I concluded that this refuted my
earlier explanation of the round-23 flake — I had blamed six concurrently running agents, and here
was a quieter run taking almost twice as long. That inference was wrong, and stating it plainly
matters more than quietly dropping it.

What the two runs actually show:

| | agents running | duration | failures |
|---|---|---|---|
| round 23 | six, heavy file I/O | 44m05s | 1 (doorbell liveness, 30s timeout) |
| round 24 | none | 79m36s | **0** |

Slower *and* clean. So swap pressure makes the machine uniformly slow without tripping the timing
races, while the concurrent-agent run was fast in wall-clock and still produced a flake. That is
consistent with the original attribution — bursty CPU contention from agents perturbs a scheduling
race that a uniformly slow machine does not — and it is the opposite of what I said mid-run.

**The practical rule, which is what AE should be used for:** memory pressure explains *duration*,
not *failures*. Do not reach for it to wave off a failing test, which is what I was starting to do.
A slow run is not evidence that anything regressed in the code, and a fast run is not evidence that
the machine was healthy. Duration and correctness need separate explanations.

Standing condition during this run: 2.1 GB free against 21.1 GB of 22.5 GB swap consumed, and the
OS killed a background shell outright to reclaim memory. Shutting down idle `dotnet build-server`
processes after the build phase returned about 0.9 GB and is worth doing before any long run.

## BD. EFCoreWorkCoordinator: 19 down to 6, and a dead property worth removing

### Guards after a query that structurally cannot return zero rows

`275`, `404`, `486`, `920` are all "the reader returned no row" branches placed immediately after
a call that always returns exactly one. Traced individually rather than as a group:

- `CountServiceBacklogAsync`'s SQL is a bare `SELECT` of scalar subqueries with no top-level
  `FROM` — one row, always.
- `reclassify_events_ephemeral` and `register_type_definition` are `RETURNS TABLE` functions whose
  every code path ends in a single `RETURN QUERY SELECT` of one scalar row.
- `wh_integrity_ledger_summary` is a `COUNT(*)` aggregate with no `GROUP BY`, which returns a row
  even against an empty table.

Reaching any of them means changing the SQL, not the test.

### Two genuine cross-writer races

`2939` — `return null` when the compare-and-set update loses, requiring the `wh_settings` row to
change between this method's own SELECT and its own UPDATE. `2924` — the sibling case where
another instance baselines the checkpoint first. Both need a real concurrent writer interleaved
inside one method call. No deterministic seam exists, and manufacturing one with timing would be
exactly the flaky test this loop keeps declining.

### `OrphanedEventRow.Metadata` is dead

`5088` is the getter of a property nothing reads. `_deserializeEventEnvelope` consumes
`EventData`, `EventId` and `Scope` and never touches `Metadata`; a grep of the class finds no
other reader. It is covered now only by a property round-trip test, which the test's own doc
comment says plainly rather than dressing up as a behavioural lock.

Worth an owner's look: a write-only field on a row type is either a column being carried for no
reason or a deserialization path that was meant to use it and does not. Not removed here —
deleting a public-ish member is not a coverage edit.

### What did get covered, and why it matters more than the count

Three of these tests exercise **backward compatibility with older SQL**: `get_stream_events`,
`fetch_outbox_batch` and `fetch_inbox_batch` each have column-count fallbacks for databases whose
functions predate a migration. Those paths run on every consumer who has not yet migrated, and
nothing had ever executed them. Each test installs a period-accurate stub of the older function in
its own per-test database, so the fallback is driven by a genuinely narrower result set rather
than by a mocked reader.

## BE. PerspectiveRunnerGenerator and MessageTagDiscoveryGenerator

`PerspectiveRunnerGenerator` 14 -> 5, `MessageTagDiscoveryGenerator` 14 -> 12. What remains in
each was traced to a call graph, not assumed.

### PerspectiveRunnerGenerator — unreachable past an earlier guard

`104` and `804` — `_extractModelType` returning null. Its caller already returns at line 94 when
all three interface lists are empty, so by the time `_extractModelType` runs at least one is
non-empty, and its two branches cover exactly those cases.

`113` — `eventTypes.Count == 0`. The interface extractors only match arities of two or three, so
`Skip(1)` / `Skip(2)` always leaves at least one event whenever the line-94 guard passed.

`1063`, `1083` — `modelType is not INamedTypeSymbol`. Reaching those calls requires first passing
`_findModelStreamIdProperty(modelType) is not null`, and a type with no named-type members cannot
carry a `[StreamId]`-attributed property.

### MessageTagDiscoveryGenerator — mostly guards against its own inputs

`161`, `224`, `267` share one root cause: `_typedConstantToCSharpLiteral`'s `default` arm fires
only for `TypedConstantKind.Error`, which requires a compile error in the attribute argument.
`248` is dead by pre-emption — `value.IsNull` is checked ten lines earlier and is already true
whenever `Value` is null for a non-array kind. `219` needs an empty constructor-parameter name.
`71` and `273` are Roslyn-contract guards (`GetDeclaredSymbol` non-null; a present attribute's
`AttributeClass` non-null). `579` is `_escapeString(null)`, and every call site passes either a
non-nullable field defaulting to `""`, a pre-guarded value, or a pattern-matched non-null string.

`188` deserves its own note because it took real effort to rule out: `_resolveNamingConvention`'s
fallthrough after `rawValue is int intValue` fails. Both the Core enum and the generator's
netstandard2.0 mirror are int-backed, so a normally-applied attribute always satisfies the
pattern. No valid-C# scenario reaches it short of the enum ceasing to be int-backed.

`604`, `605`, `607` are getters on an internal record — `TypeName`, `Namespace`, `AttributeName` —
that **nothing reads**. The file consumes `TypeFullName`, `AttributeFullName`, `Tag`, `Properties`,
`ExtraJson`, `TypeProperties` and `ExtraInitializers` and never these three. Reading them from a
test purely to turn the lines green would assert nothing about the generator. Same category as
`OrphanedEventRow.Metadata` in BD: dead members, worth deleting rather than covering.

### A real gap the tests now document rather than fix

A `StreamGroup` key containing `|` desyncs the pipe-delimited membership encoding, and the
membership is **silently dropped with no diagnostic**. The test pins current behaviour and says so.
The generator validates neither the key nor the encoding, so a perspective whose group key happens
to contain a pipe simply never joins its group — at runtime, with nothing to explain it. Worth an
owner's decision: reject the key with a diagnostic, or escape the delimiter.

## BF. A load-sensitive test shape I introduced twice, and what is left in the transport strategy

### The bug I shipped and then repeated

`await worker.ExecuteTask!.WaitAsync(...)` on a `BackgroundService` whose `ExecuteAsync` exits via
a cancellation catch is **load-sensitive**. The task can end in either terminal state:

- **RanToCompletion** — the thread pool ran `ExecuteAsync`, it awaited the gate, the token fired,
  the `catch (OperationCanceledException) { return; }` swallowed it.
- **Canceled** — the token was already canceled by the time the thread pool first ran the method,
  so cancellation surfaces as the task's own state rather than through the catch.

Awaiting the task rethrows in the second case. Isolated, the first always happens; under a loaded
suite the second does, so the test passes alone and fails in a full run — the worst failure shape
to debug, and I wrote it twice (`ClaimWorkerCoverageTests` in an earlier cycle, then
`TransportConsumerWorkerCoverageTests` in this one) before noticing.

**The fix, for any future test of this shape:**

```csharp
await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
  .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
```

That asserts the real invariant — the worker exited promptly and did not fault — without caring
which of two equally graceful terminal states it reached.

### TransportPublishStrategy 13 -> 5

`519-522` — `_resolveEntityDestination`'s null/empty-destination branch. Both public entry points
filter `OutboxWork` with no `Destination` into the event-store-only success path *before* calling
the resolver, and `Destination` is an `init`-only property on a record, so it cannot change in
between. Unreachable without a production seam.

`261` — the closing brace of the retry `while` loop. Every path inside the body returns or
continues, so there is no fall-off-the-end case for the brace to represent. Same synthetic-sequence
-point family as AO/AT: a `}` after an unconditional transfer, now the fifth instance recorded.

### A finding the transport work turned up

`TransportConsumerWorker.ExecuteAsync`'s schema-gate cancellation path does a bare `return;`
without settling `_subscriptionsReadyTcs` — unlike `ServiceBusConsumerWorker`'s equivalent, which
calls `TrySetCanceled`, and unlike this same method's other early return, which calls
`TrySetResult`. Anything awaiting `SubscriptionsReady` (a startup health probe, an
`IStartupReadinessContributor`) is left parked forever even though the worker has already stopped.
During a shutdown that races migrations, the host then never reports ready and the waiter never
exits. The test pins today's behaviour and says so, so a fix has to consciously update it.

## BG. PgSharedNotifyConnection: 23 down to 11, and why the rest are races

Covered: the per-channel LISTEN failure (318-319), both alive-lock outcomes — lost to another
session (445-446) and the claim function itself failing (450-452) — the idle keepalive (505-507),
and the backoff stretching to `PeriodicReprobeInterval` after the configured failure count
(627-628, no database needed, using the unresolvable-host technique from the sibling diagnostics
suite).

The keepalive test deserves a note because it asserts positively rather than by absence: it queries
`pg_stat_activity` filtered by the connection's own `application_name` and checks the last
statement was `SELECT 1`. Asserting merely that the connection stayed open would pass with the
keepalive removed entirely.

### Left uncovered — all the same reason

`162-164` — `ProbeNowAsync`'s `catch (OperationCanceledException) when (!cancellationToken
.IsCancellationRequested)`. Npgsql converts an internally-timed-out `OperationCanceledException`
into `TimeoutException`/`NpgsqlException` before it escapes, and a genuine caller cancellation
leaves `IsCancellationRequested` true, which fails the filter. Neither side of the guard can be
satisfied.

`230` and `461-463` — the self-test probe timing out. Both need the `SelfTestTimeout` to fire
strictly after LISTEN and NOTIFY have succeeded but before the already-sent notification is read
back. There is no signal for "we are now inside the wait", so any attempt races real Postgres
delivery latency against a timer — including with a fake `TimeProvider`, whose callback either
fires synchronously (zero loop iterations) or asynchronously (same race).

`241` and `335-336` — UNLISTEN failing. Both need the connection to break after a successful
LISTEN but before a specific UNLISTEN, without a competing handler observing the break first. The
only lever is `pg_terminate_backend`, and there is no synchronization point that lands it in that
window.

`294` — the `ObjectDisposedException` dispose race, already recorded in AR and now confirmed at
the same line number in a third file.

Every one of these is a race, not a missing fixture. Writing them would produce tests that pass
locally and fail in a full suite, which is the failure mode this session has already paid for
twice (see BF).

## BH. RESOLVED: MarkProcessed could throw ArgumentException under concurrent load at the cap

Found by a coverage test, and worth recording because the shape generalizes.

`RecentlyProcessedEventCache._enforceCapIfNeeded` ordered the live `ConcurrentDictionary`
directly:

```csharp
var toEvict = _entries.OrderBy(static p => p.Value).Take(batch)...
```

LINQ buffers a source for `OrderBy` via `Enumerable.ToArray`, which sees
`ICollection<KeyValuePair<Guid, DateTimeOffset>>` and takes the `CopyTo` fast path. `CopyTo`
sizes its destination from `Count` and then copies — so a concurrent `MarkProcessed` adding an
entry in between throws `ArgumentException` **out of `MarkProcessed`**, on the inbox dedup path.

The `_evictionLock` does not prevent it. It serializes evictions against each other, not against
inserts, and inserts never take it.

Reproduced deterministically: priming the cache to its cap and firing 100 concurrent inserts
failed on every one of three runs before the fix, and passes on every one of three runs after.

**Fixed** by snapshotting through `ConcurrentDictionary`'s own `ToArray()`, which takes all bucket
locks and returns an atomic copy, before ordering.

**The general rule:** `SomeConcurrentDictionary.OrderBy(...)`, `.ToArray()`, `.ToList()` and
anything else that buffers are unsafe while other threads write. The collection's own `ToArray()`
is the safe snapshot; LINQ's identically-named extension is not. Worth grepping for elsewhere —
this instance was in a hot dedup path and had no test until now.

## BI. InboxDrainWorker's last two lines

`535` — the `_logPerfIfInteresting(...)` call *after* the inner drain loop, reached only when the
loop exits via its `while` condition (cancellation observed at an iteration boundary) rather than
through the early `return` at 532 that every other exit takes. The cancellation test covers the
invariant that matters — once canceled, no further fetch is issued, `CallCount` stops at two —
but the loop still leaves through the inner return, so this trailing call stays dark. Covering it
needs cancellation to land in the narrow window after a page is written and before the next
iteration's condition is evaluated, without the page-smaller-than-cap early exit firing first.
That is a timing window, not a fixture.

`654` — `_admitRow`'s fallback `return true;` when a row's `MessageId` is not found in the fetch
list it is checked against. Both call sites derive `row` from that same list through `GroupBy` /
`OrderBy` projections, which do not copy elements, so the identity comparison always matches
before the loop can fall through. A third caller with a mismatched pair would be needed, and none
exists.

Note the drain cancellation test was rewritten during integration. As written it waited on a
fixed count of four written rows, which never arrives — how much of the second page lands before
cancellation is observed is a scheduling detail. It burned its own fifteen-second ceiling and then
failed, which is the hang-shaped failure BF warns about. It now waits on the worker's own
completion and asserts the fetch count, which is the actual invariant.

## BJ. Write-only members, now the third instance — worth deleting rather than covering

`WizardRunner.cs:166, 171, 176` are the getters of `WizardState.StartedAt`, `.GitCommitBefore`
and `.DecisionFilePath`. `WizardState` is constructed in exactly one place (`WizardRunner` line 52)
and used nowhere else in the repository; grepping every reader of those three names finds only the
identically-named members of `DetectedMigrationState` and `DecisionFile.State`, which are
different types. Nothing reads these.

That makes three recorded instances of the same shape:

- **BD** — `OrphanedEventRow.Metadata`: `_deserializeEventEnvelope` reads `EventData`, `EventId`
  and `Scope`, never `Metadata`.
- **BE** — `MessageTagDiscoveryGenerator`'s `TypeName`, `Namespace`, `AttributeName`: the file
  consumes seven other members of that record and never these.
- **BJ** — the three above.

In every case a test can trivially turn the line green by reading the property back after setting
it, and in every case that test asserts nothing about behaviour: it exercises a compiler-generated
getter, not a decision the code makes. **The right fix is deletion, not coverage.** A write-only
member is either a value being carried for no reason or a consumer that was meant to read it and
does not — and the second possibility is a bug the property hides.

Left in place here because removing public-ish members is not a coverage edit. Flagged together so
the owner can decide the three at once.

## BK. A MeterListener test I wrote that broke when its own siblings ran

`LedgerGauges_...` passed alone and in its class before commit, then began failing once more tests
existed in the same class. Worth recording because the mechanism is not obvious.

Every test in `StreamIntegrityMetricsCoverageTests` constructs its own `StreamIntegrityMetrics`,
and each instance registers its observable gauges on the **shared meter**, where they stay for the
life of the process. `listener.RecordObservableInstruments()` therefore fires every instance's
callback, not just this test's — the siblings all reporting their default zeros. The callback
assigned `unhealed = value`, so last-write-wins left the assertion comparing against whichever
instance happened to be polled last.

Fixed by collecting every observation into a list and asserting the expected value is **among**
them. That is sound rather than weaker: only this test's instance is set to 7/3/125.5, so
`Contains(7)` still proves this instance reported correctly, and it is immune to how many other
instances exist or what order they are polled in.

**The general rule:** an observable instrument's callback is registered per *instance* but polled
per *meter*. Any test that asserts on a single observed value is asserting on whichever instance
was polled last — which changes as soon as another test in the assembly constructs the same
metrics type. Collect and match, never overwrite.

Also note `TransportSubscriptionBuilder.cs:108` — `return [];` guarding a null `inboxStrategy`.
`RoutingOptions.InboxStrategy` is non-nullable with exactly two assignment sites, the constructor
(which always assigns) and a setter that throws on null, and `RoutingOptions` is sealed. Reaching
the branch needs an object graph no composition root can produce.

## BL. Five generators: 53 uncovered down to 8, and one Roslyn fact worth keeping

`ServiceRequirementsGenerator` goes to zero. The other four leave eight lines, every one traced
to a call graph or an API contract.

**Roslyn-contract guards** (residue M's category): `PerspectiveRunnerRegistryGenerator:70`,
`CollectiveApplyDiscoveryGenerator:62`, `AutoPopulateDiscoveryGenerator:110`,
`PinnedTypeLedgerGenerator:51` — all `GetDeclaredSymbol(...) is not I…Symbol` or
`context.Node is not TypeDeclarationSyntax` checks on a node the syntax provider already matched.

**Dead by construction:** `CollectiveApplyDiscoveryGenerator:167` — a null check inside `_emit`,
where `Initialize` already applies `.Where(static info => info is not null)` before `.Collect()`.
`AutoPopulateDiscoveryGenerator:395` and `:599` — default arms of switches over closed sets the
generator itself produces, with every member handled explicitly above.

**Could not be constructed, reported rather than guessed:** `AutoPopulateDiscoveryGenerator:129`
— a `continue` when `attribute.AttributeClass?.ToDisplayString()` is null. Every malformed or
unresolvable attribute shape reasoned through binds to an **error-type symbol** whose
`ToDisplayString()` is still non-null, rather than to a null `AttributeClass`.

### The Roslyn fact worth keeping

`PinnedTypeLedgerGenerator:55` checks `TypeKind` is neither `Class` nor `Struct`. Its **true**
outcome is unreachable, and the reason is not obvious: the only other `TypeKind` a
`TypeDeclarationSyntax` can produce is `Interface`, and **Roslyn reports `IsAbstract == true` for
every interface**, mirroring CLR reflection. The preceding line's abstract check therefore always
short-circuits first. Anyone writing a "is this a concrete type" guard in a generator should know
that an interface is already excluded by an `IsAbstract` test, so a following `TypeKind` test adds
nothing.

### Worth noting about the isolated-compilation technique

Covering `AutoPopulateDiscoveryGenerator:77` required a compilation that does **not** reference
the real `Whizbang.Core`, so `Whizbang.Core.Lenses.PerspectiveScope` fails to resolve at all — the
shared `GeneratorTestHelper.RunGenerator` always adds that reference and hardcodes the assembly
name. A local isolated-compilation helper was added in the test file. The same helper made
`697-698` reachable by controlling the compiling assembly's name, which is what the identifier
sanitizer operates on.

## BM. Four more generators: 35 uncovered down to 13

`ServiceRegistrationGenerator` 9 -> 1, `TopicFilterGenerator` 9 -> 3,
`GuidInterceptorGenerator` 9 -> 5, `WhizbangIdGenerator` 8 -> 4.

**Roslyn-contract guards** (the by-now-familiar category): `TopicFilterGenerator:80`,
`GuidInterceptorGenerator:105`, `WhizbangIdGenerator:112, 175, 236`.

**Dead by construction, traced to the producer:**
- `ServiceRegistrationGenerator:206` — the default-to-Lens arm of `_getServiceCategory`, only ever
  called after `_isUserInterfaceExtendingWhizbang` confirmed a match using the same two prefix
  checks over the same `AllInterfaces` set.
- `GuidInterceptorGenerator:280, 318, 323` — guards for `trivia.GetStructure() is not
  PragmaWarningDirectiveTriviaSyntax`, where both call sites pre-filter with
  `IsKind(SyntaxKind.PragmaWarningDirectiveTrivia)`, which Roslyn guarantees structures to exactly
  that type.
- `GuidInterceptorGenerator:417` and `WhizbangIdGenerator:354` — a switch default over a closed set
  the generator produced, and a null check on an array already filtered by
  `.Where(static info => info is not null)` upstream.

**Two lines a test was written for and did not move:** `TopicFilterGenerator:98` and `:155`. The
agent reported tests targeting both; the scoped coverage run shows neither hit. Not investigated
further this round — recorded so a later cycle knows the fixtures exist but miss, rather than
assuming the lines are untouched and writing them again.

That last point is the reason step 3 of the loop exists. Three separate agents this session have
reported "all target lines covered" while the scoped run showed otherwise, and in every case the
report was written in good faith from careful reading. Reading cannot substitute for measuring.

## BN. Five suites closed, and the EventId collision that keeps costing cycles

`SearchService` 9 -> 0, `ServiceBusReadinessCheck` 8 -> 0, `RevertCommand` 9 -> 0,
`MartenAnalyzer` 8 -> 0, `IRabbitMQNamespaceConnectionFactory` 10 -> 1,
`DeadLetterOperatorEndpoints` 9 -> 7.

### A standing note that belongs in every future prompt

**`EventId` is ambiguous in this repo.** `Microsoft.Extensions.Logging.EventId` and
`Whizbang.Core.ValueObjects.EventId` are both in scope in most test files, so any hand-rolled
`ILogger` fake whose `Log<TState>` signature writes a bare `EventId eventId` fails to compile with
CS0104 **and** CS0535 together (the ambiguity makes the override not match, so the interface also
reads as unimplemented). It has now cost a fix in seven separate files this session.

The fix is always the same: fully qualify the parameter as
`Microsoft.Extensions.Logging.EventId eventId`. Worth stating in the brief for any task that
involves a capturing logger, which is most of them.

### Confirmed unreachable, matching an earlier finding exactly

`DeadLetterOperatorEndpoints` lines `126, 127, 136, 137, 146, 147, 181` — the id-parse guard and
its three call sites. All three routes are mapped `"/{id:guid}"`, so routing rejects a malformed id
with 404 before the handler runs; `Guid.TryParse` inside `_tryGetIdFromRoute` can therefore never
fail. This is the same conclusion an earlier round reached by sending a malformed id and asserting
404, now confirmed a second time from the call graph. Two lines in the same file **were** covered:
the whitespace-fingerprint guard, reachable because `%20` decodes to a non-empty segment that
routing accepts and only the handler's own check rejects.

`IRabbitMQNamespaceConnectionFactory:56` — the closing brace of `CreateConnection`, which under
normal PDB semantics corresponds to the normal-exit `ret`. The method hardcodes its own
`ConnectionFactory` with no injectable seam, so a normal return needs a real AMQP handshake; an
offline test reaches every line above it and then leaves by exception. Needs a live broker, which
this suite deliberately does not use.

## BO. The Core batch: four classes closed, and PolicyContext's compatibility blocks are dead

`OutboxDrainWorker` 12 -> 0, `BodyOffloadPostSerializeHook` 9 -> 0, `OutboxPublishWorker` 13 -> 1,
`TransportConsumerBuilderExtensions` 13 -> 4, `DebuggerAwareClock` 9 -> 3,
`MessageTagProcessor` 13 -> 5, `SlidingWindowInboxBatchStrategy` 8 -> 5.

### PolicyContext 186-192 and 221-223 — dead, and no test file was kept for them

`PolicyContext.HasTag` / `HasFlag` each contain a "backwards compatibility" block handling
`string[]`, `IEnumerable<string>` and numeric metadata values. They cannot run.
`IMessageEnvelope.GetMetadata(string)` is declared to return `JsonElement?`, so `PolicyContext`
can only ever receive `null` or a boxed `JsonElement` — no implementer can put anything else
through that signature. The `is JsonElement` checks above are therefore exhaustive.

An agent produced a test file for this containing **no tests at all**, only prose explaining the
above. That file was deleted rather than committed: a test class with zero tests adds nothing to
the suite and hides its own reasoning where nobody looks for it. The reasoning belongs here.

### The rest, by category

**Logger-null by construction**: `MessageTagProcessor:86, 87, 113, 114`. Both blocks require
`_scopeFactory is null`, and the `Logger` property returns `NullLogger.Instance` in exactly that
case — so `Logger.IsEnabled(Debug)` is pinned false wherever these live. The existing debug-logging
test reaches real logging only through the scope-factory constructor, which structurally excludes
this path.

**Mode-gated**: `DebuggerAwareClock:132, 137` sit in `_sampleCpuTime`, whose only caller is a timer
created solely when `Mode` is `CpuTimeSampling` or `Auto`; `Mode` has no setter, so the
`DebuggerAttached` arm and the switch default cannot run. `:326` is a `catch (ChannelClosedException)`
around a channel `Dispose()` only ever completes gracefully.

**Contract-guaranteed**: `MessageTagProcessor:137` guards a `continue` after `_enforcePayloadSize`,
which has two returns, both `true` — the error path throws rather than returning false.

**Races declined**: `SlidingWindowInboxBatchStrategy:141, 165, 170, 178, 185` — an empty batch the
batcher's own contract never yields, an outer catch every inner handler already absorbs, and three
paths requiring two idle sweeps or a disposal to interleave at a specific instruction. The agent
declined all five rather than reach into private state or write a timing-dependent test, which is
the right call.

## BP. Round-25 measurement: 98.2%, and a firm operational rule about agent load

Full `-Mode Ai -Coverage`, **completed whole**: zero truncated projects, 46 projects, 21,282
tests. **98.2% (115,554 / 117,602)**, up from 97.8% and 97.4% in the two prior measurements.
Deduped worklist **1,942 -> 1,518**; classes carrying eight or more uncovered, **76 -> 31**. The
report predates the last five commits, so the true figure is better again.

### Two failures, both load-induced — and this is now a rule, not a hunch

`OutboxBulkFlushCallback_...` and `WorkOutboxAvailableSignal_WakesClaimWorker` both failed in the
run and both pass in isolation; the full Core suite had run 11,165 tests clean shortly before.
Ten agents were writing files throughout this measurement.

This is the **second consecutive measurement** where concurrent agent activity produced flaky
failures and no other anomaly — round 23's run had six agents and one flake. Combined with BC's
finding that memory pressure explains *duration* but not failures, the picture is now specific:

- **Agent CPU contention causes timing-sensitive tests to fail.** It does not truncate projects and
  does not change coverage numbers.
- **Memory pressure and swap cause runs to take longer.** They do not by themselves cause failures.

So a measurement taken with agents running is still **valid for coverage** — truncation is the only
gate that matters, and it stayed zero — but its **failure list cannot be trusted** and must be
re-checked in isolation before any of it is treated as a regression. Both were, and both passed.

Worth noting I got this wrong once mid-run in an earlier cycle, concluding from a slower quiet run
that agent load was *not* the explanation. That inference was backwards: the quiet run was slower
**and** clean, which supports agent load explaining failures and memory explaining duration.

### The shape of what is left

Every one of the top ten remaining classes is already recorded residue: `PerspectiveWorker` (L, AS),
`EFCoreServiceRegistrationGenerator` (M), `ReceptorDiscoveryGenerator` (AY), both Migrate
transformers, ASB `ServiceCollectionExtensions` (AP), `MessageTagDiscoveryGenerator` (BE),
`AsbTrafficClassOpsRateSource` (AL), `MessageJsonContextGenerator` (BA). That is the signal the
stopping condition is approaching: the head of the worklist is no longer tractable work, it is
documented residue, and what remains tractable has moved into the long tail.

## BQ. Postgres retry/locker/schema and five more generators

`PinnedTypeLedger` 9 -> 0, `DapperPerspectiveStreamLocker` 8 -> 0,
`PostgresConnectionRetry` 11 -> 1, four generators 8 -> 2 each,
`PostgresSchemaInitializer` 9 -> 6.

**`PostgresConnectionRetry:84`** — the closing brace of `if (_shouldRethrowAfterRetry(...)) { throw; }`.
The tests drive that condition's false branch many times over; the brace is the same
sequence-point-after-a-transfer shape recorded in AO, AT and BF. Fifth instance.

**Roslyn-contract guards** in the four generators: `SignalTypeRegistryGenerator:41`,
`EventNamespaceRegistryGenerator:76, 122`, `PerspectiveSchemaGenerator:132`.

**Dead by construction**: `ReceptorRegistryQueryGenerator:299, 328` — null checks on collections
both pipelines already filter with `.Where(static info => info is not null)` before `.Collect()`.
`PerspectiveSchemaGenerator:190` — the outer `if`'s closing brace where the inner
`modeArg.Value is int` is always true, because `PerspectiveStorageAttribute` takes an int-backed
enum, so the line above always returns first.

**`SignalTypeRegistryGenerator:76` and `EventNamespaceRegistryGenerator:94, 105`** were targeted by
tests that did not reach them. Both agents flagged their own fixtures as depending on Roslyn's
error-recovery for an undeclared type substituting into a constrained generic. The measurement says
it does not. Recorded so a later round knows the technique fails rather than retrying it.

### PostgresSchemaInitializer: six lines left, all needing a specific broken database

`136, 172, 315, 416, 493, 746` — the covered three are the migration-failure paths. The rest need a
database in a specific partially-broken state (a missing migrations table mid-rollback, a
particular DDL parse failure). Two were already argued unreachable by the agent from the SQL: both
`RollbackAsync:136` and `CleanupBackupsAsync:315` do `LastIndexOf("_bak_")` on strings that the
query producing them already filtered with `LIKE '%\_bak\_%'`, so the index can never be -1.

## BR. CustomParams: seven LSP notification properties nothing ever reads or writes

`tools/Whizbang.LanguageServer/Protocol/CustomParams.cs` lines 120, 123, 133, 138, 141, 146, 149
are auto-property declarations on protocol DTOs. They split two ways, and neither wants a test:

- **120 `StatusInfo.CacheAgeMinutes`, 123 `StatusInfo.ServerUptime`** — the record is live
  (`StatusHandler.Handle()` constructs one) but its object initializer never sets these two, and
  nothing else in the repo touches them. Every real status response carries `0` and `null`.
- **133 `RegistryChangedNotification.MessageCount`, 138/141 `DataLoadedNotification.Key`/`Count`,
  146/149 `LogNotification.Level`/`Message`** — all three record types are referenced nowhere
  outside their own declaration: no constructor call, no property read, no serializer
  registration. The matching `CustomMethods` constants exist, but nothing ever sends these
  notifications.

This is the fourth instance of the write-only-member pattern (see BD, BE, BJ). A round-trip test
would set a property and read it back, turning the line green while asserting nothing about any
decision the code makes — the compiler-generated accessor is the only thing under test. The
honest fixes are for the owner: delete the three dead notification records, and either wire
`CacheAgeMinutes`/`ServerUptime` into `StatusHandler.Handle()` or drop them too.

No test file was kept. An agent produced one containing only this prose and no `[Test]` method;
a test file with no tests is worse than none, because it reads as coverage that exists.

## BS. AzureServiceBusConnectionRetry 76-80, 87: the success path needs the management plane

Confirms and extends AF/AP. `CreateClientWithRetryAsync` verifies connectivity by awaiting
`ServiceBusAdministrationClient.GetNamespacePropertiesAsync` — a management-plane round trip the
local emulator does not implement, and the class constructs the admin client inline with no seam
to substitute one. Lines 76-78 (`LogConnectionEstablished`), 80 (`return client;`) and 87 (the
async epilogue reached only through that return) are therefore unreachable without a live Azure
namespace.

Line 104 (`LogStillRetrying`) and 105 are NOT residue and are now covered: the heartbeat fires
only when `attempt % 10 == 0` under `RetryIndefinitely`, so it needed a test that lets attempt 10
complete before cancelling. The existing sibling test stops at attempt 4 and never reached it.

## BT. MultiPassMessageTypeBinder: a real bug in pass 1, and why the pass-3 guard stays uncovered

**The bug.** `_resolve` called `Type.GetType(assemblyQualifiedName, throwOnError: false)` directly.
`throwOnError: false` suppresses `TypeLoadException` — the type not being found — but NOT the
exceptions raised while the NAME is parsed, before any lookup happens. A wire header carrying a
malformed assembly segment threw `FileLoadException: The given assembly name was invalid` straight
out of `BindWithDiagnostics`, from `System.Reflection.Metadata.TypeNameParser.ParseNextTypeName`.

That is the opposite of what the class is for. Its three-pass cascade exists so an unresolvable
header comes back as `Miss` for the caller to dead-letter. Throwing at pass 1 skipped passes 2 and
3 — and pass 2, which strips exactly that malformed metadata, would very likely have RESOLVED the
type. It also skipped the cache write, so every redelivery paid the throw again.

Found by a test an agent flagged as resting on an unverified CLR assumption. The assumption was
wrong in the more interesting direction: not "the malformed segment is ignored" but "it throws".

Fixed with `_tryGetType`, which treats `FileLoadException`/`BadImageFormatException`/
`ArgumentException` as "did not resolve" and falls through to the next pass. The original test now
passes and asserts recovery via `AssemblySimpleName`.

**The residue.** The pass-3 counterpart `_tryGetTypeFrom` has two uncovered lines (its catch
filter and `return null`), and this is a measured result, not an assumption: a fixture whose outer
type is also unresolvable — so passes 1 and 2 both miss and pass 3 receives the raw name with the
malformed segment intact — produced a clean `Miss` with the guard never entered.
`Assembly.GetType(name, throwOnError: false, ignoreCase: false)` returns null where
`Type.GetType` throws, because the assembly is already in hand and only the nested argument's
assembly name remains to resolve.

The guard stays. Its documented triggers are not about the name: a nested argument naming an
assembly that exists but fails to load, or one built for another architecture. Both are
deployment properties a unit test cannot stage, and the contract this fix establishes is that no
header can make the binder throw. Two lines, deliberately.

## BU. IntegrityAuditWorker 230-231 and the four workers batch

`SlidingWindowApplyBatchStrategy` lines 163, 183, 188, 196, 203 are declined for the same five
reasons the sibling `SlidingWindowInboxBatchStrategy` was: an empty batch `SlidingWindowBatcher`
never yields, an outer catch whose inner handlers absorb everything reachable, a timer callback
that must fire in the statement gap between `Interlocked.Exchange(ref _disposed, 1)` and
`_idleSweepTimer.DisposeAsync()`, a sweep-vs-sweep `TryRemove` race with no seam to force it, and
a catch-all around `await buffer.Worker` reachable only via a cancel-before-start race concurrent
with a sweep on that same buffer. Lines 172 and 193 are covered.

## BV. RabbitMQChannelPool: Reset() poisons any rental that spans it

`Reset()` exists to be called on connection recovery. It restored the semaphore to full capacity:

```csharp
while (_semaphore.CurrentCount < maxChannels) { _semaphore.Release(); }
```

Any `PooledChannel` still outstanding at that moment then called `Return` on disposal, which
released one more permit — past the maximum — and threw `SemaphoreFullException` out of
`Dispose()`, and therefore out of the caller's `using` block.

The ordering is not hypothetical. Recovery happens precisely because something broke mid-operation,
so channels ARE in flight when `Reset()` runs, and the throw lands on top of the original failure
and hides it. The stale channel would also have gone back into the available bag, to be handed to
the next caller on a connection that no longer exists.

Fixed with a generation counter: `Reset()` bumps `_generation`, each `PooledChannel` carries the
value it was rented under, and a `Return` whose generation is stale disposes its channel and
returns WITHOUT releasing a permit. All 301 tests in the RabbitMQ suite pass with the change.

Found because a coverage test for the "Reset restores full capacity" line disposed the channel it
had rented across the reset — something no existing test did.

## BW. DapperSqliteEventStore: three dead guards, one of them load-bearing

`JsonSerializerOptions.GetTypeInfo(Type)` **throws** `NotSupportedException` for a type the resolver
chain does not know. It never returns null. Three guards in the polymorphic read path were written
against a null return and so could never fire:

- `_tryMatchEventType`: `if (typeInfo == null) continue;`
- `_tryDeserializeMessageId`: `if (messageIdTypeInfo == null) return null;`
- `_deserializeHops`: `if (hopsTypeInfo == null) return [];`

The first is load-bearing and its failure is a real bug. `ReadPolymorphicAsync` takes a
caller-supplied list of candidate event types — the whole point being that the store tries each and
picks the one that fits. A caller listing ONE type absent from the JSON context did not get that
candidate skipped; the read threw and the caller lost the entire stream. The third has the same
shape for hops, which the method's own doc comment calls optional trace metadata: an unregistered
hop shape took down delivery of the event carrying it.

Fixed by switching all three to `TryGetTypeInfo`, which is what the guards were always written for.
All 100 tests in `Whizbang.Data.Tests` pass, and all eleven target lines are now covered.

Note `DapperSqliteEventStore` lines 56, 134 and 170 still use `GetTypeInfo(...) ?? throw ...` on the
APPEND path. Those `??` operands are equally unreachable, but the behavior is already "throw", so
the only cost is a less helpful exception message than the one the author wrote. Left for the owner.

### The measurement trap this exposed — worth more than the fix

Three of the five tests in this file were passing while asserting nothing. Their fixture seeded rows
with a raw `SqliteCommand` binding `streamId.ToString()`, which stores a TEXT value the store's
Guid-parameterized `WHERE` clause never matches. Every row was invisible to every read, so tests
asserting "no events came back" passed without the code under test ever executing — the same
vacuity as a `StartAsync` that returns before `ExecuteAsync` runs.

They were caught only because two SIBLING tests in the same file asserted a POSITIVE result and
failed. A file of purely negative assertions would have gone green and been committed.

The existing `DapperSqliteEventStoreDeepPathTests._seedRawEnvelopeRowAsync` already carried a
comment naming this exact hazard. The rule: **when a fixture seeds data, at least one test in the
file must assert something came back.** A suite that only ever asserts absence cannot distinguish
correct filtering from an empty table.

## BX. EnvelopeSerializer 40-45: the second double-serialization guard the first one shadows

`SerializeEnvelope<TMessage>` checks for a `JsonElement` payload twice. The second check, at
lines 40-45, is unreachable.

When `TMessage` is `JsonElement`, `envelope.Payload` is statically a `JsonElement` — a sealed,
non-nullable struct — so `payload?.GetType()` always evaluates and always equals
`typeof(JsonElement)`, and the earlier "DOUBLE SERIALIZATION DETECTED" check at line 27 throws
first, every time. `IMessageEnvelope<out TMessage>`'s covariance cannot route around it, since
variance applies only to reference-type conversions.

The already-committed `EnvelopeSerializerTests.SerializeEnvelope_WithJsonElementPayload_ThrowsInvalidOperationExceptionAsync`
independently confirms this: it drives exactly this scenario and observes the FIRST throw.

No test file was kept. An agent produced one containing only this reasoning and no `[Test]`
method — the second such case this session (see BR). A test file with no tests is worse than no
file, because it reads as coverage that exists. The reasoning lives here instead.

Note for the owner: the two guards are not redundant defensive copies of each other — the second
is simply dead. Deleting it would make the intent clearer than leaving a check that cannot run.
