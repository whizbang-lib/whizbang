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
