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

## BY. Wave 2: what 34 classes left behind, and two agent claims that did not survive verification

Covered and verified: 151 target lines across Core workers/resilience/coordinators, nine
generators and analyzers, the EFCore Postgres collective classes, and four transport classes.

### Declined, with reasons

**The sliding-window family, third instance.** `SlidingWindowOutboxBatchStrategy` 128, 150, 155,
163, 170 decline for the same five reasons already recorded for the Inbox and Apply siblings. Only
line 140 was drivable (flush observing `_stopCts` through `Task.Delay(Timeout.Infinite, ct)`, which
has no competing completion path). Three classes, same shape, same five declines — this is the
family's structure, not a gap.

**`PerStreamSerializer`** 166 (`TryRead` cannot fail immediately after `WaitToReadAsync` returned
true under the class's own `SingleReader` invariant), 197/198 and 225 (require `_stopCts` to cancel
a pending read BEFORE that same shutdown's `TryComplete()` continuation resolves it), 239
(two-sweep `TryRemove` race).

**`DeadLetterRecoveryWorker`** 227 — `Task.WhenAny` never propagates a constituent's exception to
its own awaiter, so this is structurally unreachable rather than a race. 244-247 — the loop-breaker
close branch reads `DateTimeOffset.UtcNow` directly with no `TimeProvider` seam and an int-minutes
cooldown, so driving it needs a real 60-second wait. Worth a `TimeProvider` for the owner.

**`BatchFlusher`** 142 — `_runAsync` converts every internal `OperationCanceledException` to a
`break`, so its task can only end `RanToCompletion` or `Faulted`, never `Canceled`. The catch
guards a status the current code cannot produce.

**`WhizbangIdProviderRegistry`** 148 — the `_diRegistrations.Count == 0` guard. This assembly's own
generated module initializer calls `RegisterDICallback` before any test runs, so the list is never
empty in-process. Emptying it means reflecting into a private static that other tests in the same
assembly read without their own `[NotInParallel]` guard.

**`LeaseHandle`** 166 — the `catch (ObjectDisposedException)` in `Dispose()`. The `_disposed` guard
sets its flag before either concurrent caller touches the CTS, so the catch guards a race the lock
already prevents.

**`ScopedWorkCoordinatorStrategy`** 210-214 — dead since Phase H moved claiming to `ClaimWorker`;
`WorkCoordinatorFlushHelper.ExecuteFlushAsync` now returns an empty `WorkBatch` unconditionally.
Rather than reflect into the private method to force the line green, the agent wrote a test that
PINS the invariant keeping it dead: with inbox work queued and a real `IInboxChannelWriter` wired,
`TryWrite` is never called. A future change that resurrects the branch fails that pin loudly. This
is the right treatment for dead-but-not-obviously-dead code and is worth copying.

**Roslyn-contract guards, eight more.** `LensQueryTypeArgumentAnalyzer` 64,
`ScopedLensFactoryGenerator` 45, `PerspectivePurityAnalyzer` 141/261,
`PerspectiveSyncInReceptorAnalyzer` 79, `PinnedIdRegistryGenerator` 46,
`MessageTypeCatalogGenerator` 73, `MintedCompositeConstructionAnalyzer` 115/155. Same catalogue as
before: a resolved symbol always has a containing type and namespace, `GetDeclaredSymbol` is
non-null for a matched node.

**`CollectiveSettersRewriter`** 134's true branch and 188 — both defeated by the BCL, not by the
test. `ICollectiveSetters<TModel>` exposes only 2-parameter `SetProperty` overloads and
`Expression.Call` validates argument count against the resolved `MethodInfo`, so
`Arguments.Count != 2` is unconstructable. And `Expression.Call` auto-quotes a bare
`LambdaExpression` passed for an `Expression<TDelegate>` parameter, so the `LambdaExpression direct`
arm can never see an unquoted lambda; defeating the auto-quote produces a node that is not a
`LambdaExpression` at all and lands in the throw branch instead.

**`ScopedLensFactoryGenerator`** 212/215 — `LensTypeShortName` and `ModelTypeShortName` are written
in `_extractLensInfo` and read nowhere. Fifth instance of the write-only-member pattern (BD, BE,
BJ, BR, BX). They want deleting.

**`WolverineHttpTransformer`** 108 — the fourth `if (root is not CompilationUnitSyntax)` guard;
`ParseText(...).GetRoot()` always returns a compilation unit.

### Two claims that did not survive the scoped coverage run

Both were caught by step 3, not by reading the agent's report.

**`PerspectiveMigrationWorker` 83** was reported covered. The whole inner region 77-85 was
unreached: the test called `StartAsync` then `StopAsync` immediately, so the stopping token was
already cancelled when the pending-migration loop made its cancellation check and the loop broke at
the top. The fixture was correct; the sequencing was not. Fixed by making the failing callback
itself the signal — a `TaskCompletionSource` set inside the throwing `UpdateMigrationStatus`,
awaited before `StopAsync`. The sibling `GetPendingRebuilds`-throws test had the identical race and
the identical fix. This is the `StartAsync` vacuity trap in a new dress: not "did `ExecuteAsync`
run" but "did it get far enough before I cancelled it".

**Three generator tests reached into internal records via reflection** to assert positional
properties round-trip (`PinnedIdInfo`, `JsonWhizbangIdInfo`). They failed on a guessed constructor
signature, and they were the write-only round-trip anti-pattern regardless — a compiler-generated
accessor is the only thing such a test exercises. Deleted rather than repaired.

### A vacuous assertion caught at build time

`Assert.That(true).IsTrue()` appeared in one worker test, with a comment explaining that reaching
the line was the point. TUnit's own analyzer rejected it (TUnitAssertions0005), which is a better
guard than review. Replaced with assertions on the worker's `ExecuteTask` state — `IsCompleted` and
`!IsFaulted` — which is the actual evidence that the best-effort catch swallowed both failures.

## BZ. Wave 3 declines, and a worklist error of mine worth not repeating

### A path I got wrong, caught by an agent rather than by a build

I assigned `PerspectiveModelArrayAnalyzer` and `SerializablePropertyAnalyzer` to
`Whizbang.Data.EFCore.Postgres.Tests`, having inferred their project from neighbouring rows in a
worklist view that printed only basenames. They live in `src/Whizbang.Generators/`, and that test
project references it as `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` — an analyzer
reference, not a compilable one — so any test file placed there could never have compiled. The
agent verified this from the csproj AND from `obj/project.assets.json`, wrote no file, and said so.

Two lessons. Keep the source path in the worklist view, not just the class name — the deduped
script emits the full path and I threw it away in formatting. And "create NO file when the class
turns out to be unreachable from here" is worth stating in every brief: the alternative is a file
that fails to compile and costs a whole integration cycle to diagnose.

### `PerspectiveCursorCache` 212 and 237 — two-read races with no seam

Both guard a window between two reads with nothing overridable in between: 211-212 is
`Interlocked.Read` followed by `CompareExchange`, and 236-237 is a live re-check against
`_streamLastActivityTicks` during enumeration. There is no `TimeProvider` call or injectable
dependency inside either window, so reaching them deterministically would need a production test
seam. A probabilistic thread-race test could not honestly claim to exercise the line.

### `CategoryBatch` 155, 160, 165 — write-only members, sixth instance

`MigrationItem.Decision`, `.OriginalCode` and `.TransformedCode` are never read or written anywhere
in `tools/`. The similarly-named properties that ARE read belong to unrelated types (`DecisionPoint`,
`MigrationOption`), which is what makes this one easy to mis-grep. Sixth instance of the pattern
(BD, BE, BJ, BR, BX, and `ScopedLensFactoryGenerator`'s pair in BY). They want deleting.

### `BaseSagaModel` 63 and 66 — auto-properties whose round-trip asserts nothing

`Summary` and `CreatedAt` are plain auto-properties. They are not dead — the ORM writes them and a
dashboard reads them — but a test that assigns one and reads it back exercises only a
compiler-generated accessor, which is the same filler as a write-only round-trip. Two such tests
were produced and removed.

Kept, and worth the distinction: `Hooks` (88) and `GetHooks` (193). `Hooks` has a lazy-init getter
(`get => _hooks ??= [];`), so "an assigned list survives the getter" is a real contract with a real
failure mode — a substituted default would silently drop the hook history a resumed saga had
already recorded. The test was sharpened from comparing counts to asserting reference identity,
because a count comparison passes even if the getter hands back a copy, and a copy is precisely
what would make a later `Add()` vanish.

### Analyzer guards, this wave

`PerspectiveModelPolymorphicAnalyzer` 216 — `type is IArrayTypeSymbol` on a parameter statically
typed `INamedTypeSymbol`; Roslyn's `ITypeSymbol` subkinds are mutually exclusive and the only caller
already filtered arrays out. 244 and `PerspectiveModelDictionaryAnalyzer` 218 — `ContainingNamespace
== null`, the established shape. `PerspectiveModelArrayAnalyzer` 61 — `GetDeclaredSymbol` null.
`SerializablePropertyAnalyzer` 179-180 — `return true` for a `Nullable<object>` check, dead by
construction since `Nullable<T>` constrains `T : struct` and `object` cannot satisfy it.

Left explicitly undetermined rather than guessed: `PerspectiveModelPolymorphicAnalyzer` 264 and
`PerspectiveModelDictionaryAnalyzer` 243, both `attrName == null` via a null `AttributeClass`. That
is not among the established unreachable shapes and no compiler experiment was run to settle it.

### One correction to a premise I put in a brief

I told an agent that line 61 appearing in three analyzer files hinted at a shared guard. It appears
in two, and in those it is two DIFFERENT guards sharing a number by coincidence. The genuinely
shared guard is `iface.TypeArguments[0] is not INamedTypeSymbol modelType` at three different line
numbers (Polymorphic 61, Dictionary 57, Vector 103), and it is reachable, not residue: a generic
perspective whose own type parameter stands in for `TModel` yields an `ITypeParameterSymbol`. It now
has a test in all three files, each written so that an unconditional-cast regression surfaces as an
extra `AD0001` analyzer-crash diagnostic rather than as silence.

## CA. Wave 3 verification: 156/158, and two more brace artifacts proven rather than assumed

`PgDutyElector` 110 and 165 both read uncovered while their blocks demonstrably run. Proven from
the same cobertura file rather than argued:

- **110** is `}` closing a `catch` whose last statement is `throw;`. Lines 107 (`} catch {`), 108
  (the dispose) and 109 (`throw;`) are all HIT. The catch runs; only its closing brace reads red.
- **165** is `}` closing a `catch (Exception)` whose body is a comment only. Line 163 (`} catch
  (Exception) {`) is HIT. Same shape: an empty catch's closing brace.

Sixth and seventh confirmed instances of the closing-brace-after-unconditional-transfer artifact.
The check that settles it in one step: read the block's OTHER lines out of the same coverage file.
If the catch line is hit and only the brace is not, it is the artifact and not a gap.

### `NotifySubscriptionRegistry` 45 and 72 — CAS retry paths, measured not guessed

Both are the "lost the race, retry" continuation points in compare-and-swap loops: 45 closes the
`else` arm after a failed `TryAdd`, 72 is the `continue` after a failed `ICollection.Remove`. Tests
using 64 real OS threads on a `Barrier` were written and DO pass — a scoped `--coverage` run shows
46, 60 and 78 hit but 45 and 72 missed, so the race did not land.

The tests were kept anyway, and the distinction matters: their assertions are deterministic and
they cover a real invariant (concurrent add/remove leaves the registry consistent). What is not
reliable is their coverage of those two specific lines. A test whose *assertions* always hold is
worth keeping; a test whose *coverage claim* is probabilistic must not be counted as covering the
line. Recorded as residue, tests retained, 327ms.

### `DbContextNotificationConnectionStringFallback` 59 and 89 — removed rather than kept

An agent reached these double-checked-locking inner branches by reflecting the private `_gate`
`Lock` out of the instance, taking it, starting a racer thread, then setting the private
`_resolved`/`_cached` fields. It was careful work and it was honest about the residual ordering
assumption — but `ai-docs/coverage-exclusions.md` forbids exactly this ("never assert an
unreachable branch via reflection to force the line green"), and a test coupled to three private
field names breaks on any rename while asserting nothing a caller can observe. Both tests and their
helper were removed. The outer-check behavior remains covered by the two tests that survive.

### Others this wave

`EFCoreDeadLetterRecoveryService` 123 — `if (!await reader.ReadAsync())` after
`evaluate_canary_campaign`. Every branch of that SQL function ends `RETURN QUERY SELECT ...;
RETURN;`, so it always returns exactly one row; the C# fallback cannot fire without faking
`NpgsqlDataReader` against a class designed around a real connection.

`DomainOwnershipDetector` 119 — `continue` on an empty `domain`. Both producers were traced:
`_extractDomainFromTypeName` always leaves at least one character (every strip requires
`Length > pattern.Length`), and `_extractDomainFromNamespace` returns null or a non-empty segment.
Apparently unreachable; flagged rather than forced.

`AnalysisTypes` 45/78/108/167 — the `FilePath` positional parameter on `HandlerInfo`,
`ProjectionInfo`, `EventStoreUsageInfo` and `MigrationWarning`. `Program.cs`'s report loops read
other members of each of these types but never `.FilePath` — and DO read `.FilePath` on the
unrelated `DIRegistrationInfo`, which is what proves the omission real rather than a bad grep.
Seventh write-only instance.

`RabbitMQConnectionRetry` 62/111/112 — the success path needs a real broker round trip, and
`ConnectionFactory` is `public sealed` with the method taking the concrete type, so unlike
`ServiceBusAdministrationClient` and `BlobContainerClient` there is no subclassing seam. 145 is
the closing brace after an unconditional `ExceptionDispatchInfo.Throw`. Structurally the same as
the ASB residue at AF/AP/BS.

### A rule the shard guard enforced better than review would have

`PhysicalFieldHydratorRegistryCoverageTests` landed in `Whizbang.Data.EFCore.Postgres.Tests` with
no `[Category("ShardN")]`, because the brief that produced it did not mention the rule — I had not
expected that file to land in that project. `ShardCoverageGuardTests` failed the build and said
exactly why: "a class with no shard category runs in NO slice and silently stops being tested."
That is the vacuity failure mode expressed as a build guard, and it is worth more than the
convention it protects.

## CB. A new residue shape: dead by C# generic covariance

`src/Whizbang.Core/Internal/MessageExtractor.cs` lines 72, 73, 77, 78 are the
`IEnumerable<IEvent>` and `IEnumerable<ICommand>` arms of `_tryExtractFromTypedEnumerable`. They
cannot be reached by any input.

`IEvent : IMessage` and `ICommand : IMessage`, and `IEnumerable<out T>` is covariant. So any
`IEnumerable<IEvent>` value ALREADY satisfies the `IEnumerable<IMessage>` test on line 66, which is
checked first and always wins. The two later arms are unreachable not because no caller passes such
a value, but because the type system converts every such value into the earlier case.

This is a distinct category from the Roslyn/BCL contract guards already catalogued — same "cannot
fire" conclusion, different mechanism, and worth naming separately because the check that settles
it is different: for a contract guard you read the API's documented invariant, but here you read
the ORDER of the type tests and the variance of the interfaces involved. A type test that is
strictly weaker than an earlier one is dead regardless of what callers do.

Whether the arms should be deleted or the order changed is the owner's call. If the intent was to
distinguish an event sequence from a command sequence, the check has to precede the
`IEnumerable<IMessage>` one, and the fact that it does not is arguably the real finding here.

## CC. Eighth write-only set: WorkCoordinatorFlushHelper's FlushContext

`src/Whizbang.Core/Messaging/WorkCoordinatorFlushHelper.cs` lines 21, 23, 30, 34 are the
`InstanceProvider`, `StrategyName`, `Flags` and `Metrics` fields of the `FlushContext` record
struct. Nothing reads any of them.

Unusually, the production code says so itself: the type's own doc comment states that most fields
are "kept for source compatibility with strategy call sites; the new-path helper only consumes the
ones documented below." So this is a deliberate, documented carry-over rather than an oversight —
which makes it residue with a known owner decision behind it, and the cleanest of the eight
instances to act on when someone chooses to.

Running total of write-only sets: BD, BE, BJ, BR, BX, BY (ScopedLensFactoryGenerator),
BZ (CategoryBatch/MigrationItem), CA (AnalysisTypes.FilePath ×4), and now CC.

## CD. Guards against inputs the code itself produced — three more

`src/Whizbang.Core/Security/DefaultMessageSecurityContextProvider.cs` 51, 55 and 121.

- 51 and 55 throw when `_options` or `_extractors` is null. The primary constructor already does
  `options ?? throw`, and `_extractors` is built with `[.. extractors.OrderBy(...)]`, an eager
  materialization. Neither field can be null after construction, so neither throw can fire.
- 121 is `if (extractor is null) continue;` over that same materialized list. A null element would
  have thrown inside the constructor's `OrderBy(e => e.Priority)` before the field was ever
  assigned, so the loop cannot observe one.

Same category as the entries already recorded, but worth logging because all three sit in security
code, where a defensive guard reads as prudent rather than dead. The distinguishing question is not
"could this be null in principle" but "could it be null HERE, given what the constructor
guarantees" — and the answer is set by the construction path, not by the field's type.

## CE. Expression-tree API contract guard, and a brace-artifact hint of mine that was wrong

### `CollectivePredicateSqlCompiler` 394-395 — cannot fire

`_readMember`'s default switch arm throws for a `MemberExpression.Member` that is neither a
`FieldInfo` nor a `PropertyInfo`. `Expression.MakeMemberAccess` validates at construction time that
the member is a field or a property, and there is no public route to an instance that violates it.
Same category as the Roslyn contract guards — a BCL invariant rather than a Roslyn one — and
verified against the framework's factory rather than assumed.

### `PgCommitOrderStamperWorker` 247 — NOT the brace artifact I predicted

I flagged 247 and 303 in the brief as "strong candidates" for the
closing-brace-after-unconditional-transfer artifact. 303 was one. **247 was not**, and the agent
was right to check rather than take the hint.

247 is the outer worker loop's closing brace, and it is genuinely reachable — but only by falling
through the loop body's retry tail rather than exiting via `break`/`continue`. Every existing
integration test leaves that loop through a `break` or a `continue`, so none of them ever reach the
fall-through. Driving it needed a non-cancellation exception on each iteration, produced in-process
with a syntactically invalid connection string so `new NpgsqlConnection(...)` throws before any
network I/O, and the loop-back was proven by waiting for a SECOND error log rather than by
inspecting state.

A scoped `--coverage` run confirms 247 now covered. The lesson is about the heuristic, not the
line: "closing brace on the uncovered list" is a HYPOTHESIS to check against the block's other
lines, not a classification. When the enclosing statements are covered and only the brace is not,
it is an artifact; when the whole tail is uncovered, the block genuinely never ran and there is real
behavior behind it. I gave that check to the agents for catch blocks and should have stated it for
loop bodies too.

### `SplitModeChangeTrackerHydrator` 102-104 — covered, with a constraint worth copying

`Clear()` empties a `private static` dictionary that two other test files register into inside
their own test bodies. Rather than decline it as a cross-test hazard (the treatment
`IntegrityManifestReceptors` got), the agent tagged the class with
`[NotInParallel("EFCorePostgresTests")]` — the SAME key those files already carry — which serializes
it against that whole fleet without needing a database. That is the better outcome, and it is only
available because the key already existed; inventing a new one would have serialized against
nothing.

## CF. Wave 4 verification: 114/118, and a test that passed via the wrong branch

### `AuditJsonSerializer` 46-48 — reported covered, was not

The runtime-type fallback. The test declared the value as `object`, reasoning that `object` is
unregistered so the compile-time lookup would fail. It does not fail: `GetTypeInfo(typeof(object))`
SUCCEEDS, the string serialized in the FIRST block at line 37, and the test's assertions — value
kind is String, content matches — were satisfied without the fallback ever executing.

This is the sharpest example this session of a test that passes for the wrong reason. Both
assertions were about the OUTPUT, and the output is identical whichever block produces it. Nothing
about the test was wrong except that it could not distinguish the two paths.

Fixed by making the two lookups genuinely disagree: a private interface with a
`DefaultJsonTypeInfoResolver` that returns null for that interface and delegates everything else, so
the compile-time type truly fails to resolve and the concrete record truly resolves. 46-48 now
covered.

The general lesson: when two code paths produce the same observable output, an output assertion
cannot tell you which one ran. Either assert something only one path can produce, or construct the
input so only one path is possible. A scoped `--coverage` run is what caught it.

### `DefaultMessageSecurityContextProvider` 193 — unreachable from its only caller

Line 193 closes `if (envelopeType.IsGenericType && ...GetGenericArguments() is { Length: 1 })`, and
is reached only when that outer test passes AND the inner
`typeof(IMessageEnvelope).IsAssignableFrom(envelopeType)` fails.

The method has exactly one caller (line 176), which passes `current.GetType()` where `current` is
an `IMessageEnvelope` being walked through nested payloads. So a generic-with-one-argument type
arriving here is always assignable to `IMessageEnvelope` and returns at 191. Reaching 193 needs a
generic single-argument type that is NOT an envelope, which the caller cannot construct.

Guard against inputs the code itself produced. Same category as CD.

### Confirmed reachable this wave, worth noting because the shapes look like residue

- **`SlidingWindowBatcher` 86, 101, 125** — the fourth encounter with this family, and the first
  where the race lines were driven DETERMINISTICALLY rather than declined: a custom
  `ChannelReader<T>` controls `WaitToReadAsync`/`TryRead` directly (phantom-ready signal, an
  already-cancelled task), and `SlidingWindow = TimeSpan.Zero` makes the elapsed check go negative
  on the first synchronous pass. No `FakeTimeProvider`, no real threads. Where the sibling classes'
  equivalent lines were declined as unconstructable races, these were constructable because the
  batcher takes its reader as a dependency. **The seam decides, not the shape.**
- **`SecurityContextHelper` 536** — an abandoned task's fault-observing continuation, proven with
  the `TaskScheduler.UnobservedTaskException` + forced-GC technique already established in
  `UnobservedExceptionDiagnosticsTests.cs`, with a unique marker string so a concurrent unrelated
  fault cannot produce a false pass.
- **`AuditOutboxMessageBuilder` 147-151** — an agent declined this as needing a `Type.GetType` input
  that throws rather than returns null, noting correctly that nothing in the repo constructs one.
  It was closable because THIS session had already proven the trigger while fixing the
  `MultiPassMessageTypeBinder` bug (residue BT): a malformed `Version=` segment raises
  `FileLoadException` out of `TypeNameParser` before any lookup. The same finding that produced a
  production fix also unblocked a coverage gap three waves later.

## CG. Measuring when the machine will not host a full sweep

Five consecutive `pwsh scripts/Run-Tests.ps1 -Mode Ai -Coverage` runs were killed for low memory,
including one at `-MaxParallel 3` and one started from a freshly reset build server. What worked,
what did not, and what the resulting number is worth.

### The build server is the recurring memory sink

`VBCSCompiler` reached **4.86 GB**, was shut down, and had regrown to **4.18 GB** a few hours later.
It accumulates across a long session of repeated project builds and nothing reclaims it.
`dotnet build-server shutdown` frees it cleanly and it respawns on demand. This belongs BEFORE a
measurement, not after a failure — and the first full sweep of the session succeeded precisely
because it ran before dozens of builds had gone through.

### Background jobs are reaped; foreground commands are not

Every background command — the sweeps, a sliced script, even a 30-second `sleep` loop — was killed
under memory pressure. Every foreground command in the same period succeeded: builds, scoped test
runs, per-project coverage runs. The practical rule for this machine: **run measurement work in the
foreground, chunked to fit the timeout**, rather than backgrounding it and hoping.

### `[Category=...]` treenode filters silently collect no coverage

`--treenode-filter "/*/*/*/*[Category=ShardN]"` RUNS the tests correctly — all four shards passed,
2,833 tests, zero failures — and writes a **178-byte empty cobertura**. Per-class filters
(`/*/*/ClassName/*`) preserve coverage normally; every scoped verification this session relied on
that and produced 13-15 MB files. So the property-filter form is the broken one.

This is a trap of the same family as the vacuity ones: the run reports success, a cobertura file
exists, and it contains nothing. Checking the file SIZE is the cheap guard — the empty one is
178 bytes against a real one's 13+ MB.

### What the sliced number is, and what it is not

34 of 35 projects measured cleanly, plus per-project pass/fail counts the banner never gave:

    Whizbang.Core.Tests                     11,384 passed   1 failed
    Whizbang.Data.EFCore.Postgres.Tests      2,833 passed   0 failed  (4 shards, coverage LOST)
    Whizbang.Data.Dapper.Postgres.Tests        537 passed  43 failed
    Whizbang.Core.Integration.Tests            149 passed   1 failed
    ...29 further projects                                  0 failed

Merged: **96.6% over 28 assemblies** (99,213 / 102,680).

**This is NOT comparable to the 98.7% baseline**, which covered 29 assemblies and 117,622 coverable
lines. `Whizbang.Data.EFCore.Postgres` is absent entirely — its full run exceeds the foreground
window and its shard-filtered runs emit empty coverage.

Nor is the gap fully explained by that one assembly. The missing assembly accounts for ~14,942
coverable lines, but the covered count differs by ~16,897 — roughly 2,100 lines more than the
missing assembly can explain. I have not chased that remainder, so the correct statement is that
the sliced figure measures a DIFFERENT population, not that coverage fell.

The last figure that can be stated without qualification remains **98.7% across 35/35 projects**,
measured before waves 3 and 4. Those waves added 270 lines verified individually by scoped
`--coverage` runs, which is a stronger guarantee per line than a suite total — but it is not a
suite total, and the two should not be added together and presented as one.

## CH. The 43 Dapper failures were the Postgres container dying, not defects

The sliced measurement reported 43 failures in `Whizbang.Data.Dapper.Postgres.Tests` against 537
passes. Re-running the same project immediately gave **6**, and a third run gave 12. A failure
count that swings 43 → 6 → 12 across identical binaries is not measuring the code.

Every one of them is a `BeforeTest` hook failing with a Postgres shutdown code:

    BeforeTestException: BeforeTest hook failed: 57P01: terminating connection due to
                                                 administrator command
    BeforeTestException: BeforeTest hook failed: 57P03: the database system is shutting down

And `docker ps` caught the container mid-cycle: `whizbang-test-postgres  Up 15 seconds`, during a
session in which it had been up for hours.

**Under memory pressure the Docker VM reclaims the test Postgres container mid-run.** Every test
whose `BeforeTest` hook is in flight at that moment fails, and the count is simply however many
tests were running when the database went down — which is why it is different every time.

Three consequences worth carrying:

1. **These are not defects and must not be triaged as such.** Nothing in the Dapper suite is
   broken; all three coverage tests added to that project this round pass when run scoped, and the
   whole project passes when the container stays up.
2. **A failure list from a run taken under memory pressure is worthless**, which is a sharper
   version of the rule already recorded (BC/BP) that agent load invalidates a failure list but not
   a coverage number. Here the load did not merely slow things down — it removed the database.
3. **The diagnostic is the error code, not the count.** `57P01`/`57P03` in a `BeforeTest` hook says
   "infrastructure went away" and can be distinguished mechanically from an assertion failure. Any
   future triage of this suite should grep for those codes before reading test names.

This is the fourth environmental cause found this session by refusing to write an anomaly off:
71 leaked test databases, a peer session competing for RAM, a `VBCSCompiler` growing to 4.86 GB,
and now a container being reclaimed mid-run. None of them were visible in a test name.

## CI. Wave 5 transports: a residue hint of mine that pointed at the wrong class

### `AsbBacklogPeek` is NOT residue — I conflated it with its neighbour

I told an agent that `AsbBacklogPeek` was covered by residue entry AL. AL is about
`AsbTrafficClassOpsRateSource`, whose guard reads a private field only a live subscription
populates. `AsbBacklogPeek`'s guard only needs `transport is AzureServiceBusTransport`, which the
existing offline tests already satisfy via `RecordingProvisioningAdminClient`. All three lines were
coverable offline and are now covered.

The two classes sit in the same file family and have the same shape — a guard that returns early
unless some transport state is present — which is exactly what made the conflation easy. A residue
entry names ONE class; applying it to a neighbour by resemblance is how real coverage gets written
off. The agent checked instead of accepting the decline, which is the behaviour worth reinforcing.

### `AzureServiceBusTransport` 1682, 1761, 2041 — confirmed as recorded residue AU

Verified against source rather than taken from the entry: two `default: throw` arms over
`AsbReceiveAction`, an internal enum with exactly four members, all handled, fed by a private
`_decisionMaker` with no injection seam; and an `if (_adminClient == null) throw` whose only call
site already guards with `if (_adminClient != null)`. No file created.

### Shared test doubles gained two opt-in seams — deliberate, and worth flagging

`tests/Whizbang.Transports.RabbitMQ.Tests/TestDoubles.cs` now has:
- `FakeConnection.IsOpen` as a settable property (was a readonly field fixed at construction), so a
  connection can go closed → open → closed. `RabbitMQReadinessCheck`'s recovery branch is
  unreachable otherwise, since the flag only resets on a transition.
- `FakeChannel.ExceptionToThrowOnDispose` and `SuppressChannelShutdownUnsubscribe`, both defaulting
  to off.

The second one deserves scrutiny rather than a free pass: it makes the fake's event `remove` a
no-op so a shutdown handler can still fire after `Dispose()` set the disposed flag. That models a
race the real client CAN produce — an event already dispatched before the unsubscribe completes —
which is precisely why the production guard exists. It is not an impossible state invented to turn
a line green. Same justification as the `ChannelReader` seam that made `SlidingWindowBatcher`'s
race lines deterministic in wave 4: **the seam reproduces a real interleaving, it does not
fabricate one.**

Both full suites pass unchanged with the seams present (RabbitMQ 310, ASB 604), which is the
evidence that the defaults really are inert.

## CJ. A process error of mine: `git add -A` with agents still in flight

Commit `b1f8e46bd` was meant to carry six verified transport classes. It also swept in **ten files
from two agents that were still running** — Migrate, LanguageServer, Generators, Sagas and two
EFCore.Postgres classes — none of which had been built or run at the time.

I had been deliberately holding builds while agents worked, precisely to avoid compiling a moving
target, and then staged with `git add -A` anyway. The agent noticed before I did and reported that
"something in this environment auto-commits working-tree changes" — it was me.

All ten were verified afterwards and all pass (14/14 target lines confirmed across the six from the
settled projects). That is luck, not process. **Stage by explicit path when any agent is running.**
`git add -A` is only safe once `ListAgents` shows none running.

## CK. Wave 5 declines: two classes blocked by a missing seam, not by shape

Both of these are worth separating from the usual residue, because the code is perfectly testable —
it is the ABSENCE of a seam that blocks it, and that is an owner-actionable finding rather than a
fact about the language.

### `GitExecutable` 64, 65, 75

`_resolve()` caches into a private static for the process lifetime (`_resolvedPath ??= _resolve()`),
so whichever branch fires first wins forever. On any developer or CI machine that is the
well-known-path branch, so the env-var-hit branch and the not-found branch never run.

Covering them means mutating `GIT_CLI` or the filesystem before ANY other test in the assembly
touches `GitExecutable.Path` — and three other coverage suites in that project
(`GitOperationsCoverageTests`, `GitWorktreeServiceCoverageTests`, `RevertCommandCoverageTests`)
need the real git binary. Poisoning the cache would break them. Needs an injectable resolver or a
reset hook.

### `PathResolver` 26, 31, 51

All three sit behind `Directory.GetCurrentDirectory()` with no override — the original author's own
comment already says "FUTURE: requires temporary git repository setup". Forcing the null-returning
branches means mutating process-wide CWD and clearing `WHIZBANG_DOCS_PATH`, while the existing
`PathResolverTests.cs` env-var tests carry strict equality assertions and are NOT `[NotInParallel]`
guarded. A concurrent CWD or env mutation would flip their result mid-race.

The agent declined to introduce that flake, which is the right call: **a test that turns one line
green by making a neighbouring test unreliable is a net loss**, and the effort already spent a
session chasing a manufactured flake.

### Also declined, ordinary categories

- `StatusHandler` 22 — `continue` on `info is null`. Every symbol from `GetAllSymbols()` comes from
  one of the three collections `SymbolResolver.Resolve` consults, and it falls back to the raw
  symbol name in each, so a match is structurally guaranteed.
- `PinnedIdCodeFixProvider` 48, 75 — `root is null` after `GetSyntaxRootAsync`. The provider is
  registered for `LanguageNames.CSharp` only, so a routed document always supports syntax trees.
  Roslyn contract guard, ~a dozen now recorded.
- `RestLensInfo` 21-23 — `EnableFiltering`/`EnableSorting`/`EnablePaging` are written by
  `_extractLensInfo` and read by nothing; the generated endpoint's filtering and sorting are
  literally `// TODO` comments. **Ninth** write-only set.

### One correction to a hint of mine

I suggested `StatusHandler`'s lines might be the same dead LSP surface as residue BR. They are not:
BR is `CacheAgeMinutes`/`ServerUptime` on `StatusInfo`, a different gap. Lines 35-36 are a live
`case "message"` arm that no existing entry reached because none had both `IsCommand` and `IsEvent`
false. Second time this wave a residue pointer of mine named the wrong thing (see CI), and both
times the agent checked rather than accepting the decline.

## CL. Wave 5 Core/data: the eighth brace artifact, and two provably dead lines

### `NotificationConnectionPlan` 89 — eighth confirmed closing-brace artifact

`OpenAsync`'s catch disposes the connection and rethrows. From the same cobertura file: 86
(`} catch {`), 87 (the dispose) and 88 (`throw;`) are all HIT; only 89, the closing brace, is not.
85 (`return connection;`) is also MISS, which confirms the connection genuinely failed rather than
succeeding.

So the test works and the line is the artifact. Eighth instance. The check is now routine and
should be the FIRST thing done with any uncovered `}`: read the block's other lines out of the same
coverage file. If the catch header and its statements ran, the brace is noise.

### `DapperWorkCoordinator` 232 — dead by both call sites

`_serializePerspectiveCompletions` has exactly two call sites in the file, and both pre-guard with
`cursors.Count == 0 ? "[]" : …`, so its own empty-collection short-circuit can never run. Notably
its sibling `_serializeFailures` (line 202) has NO such pre-guard at its call site, which is why
that one IS reachable and now covered. Two near-identical helpers, one live and one dead, decided
entirely by what the callers do.

### `PhysicalFieldExpressionVisitor` 63 and 73

63 guards `PropertyInfo.DeclaringType == null`, a CLR state that does not occur for property
members. 73 is provably dead by its enclosing condition: `_isPerspectiveRowType(dataAccess.Expression?.Type)`
already requires `dataAccess.Expression` to be non-null, so the `entityExpression == null` test
inside it can never be true. No file created.

### `NullScopeContextAccessor` 16-17 — null-object properties

Plain `{ get; set; }` on a null-object implementation, no decision logic. Tenth write-only-shaped
set, and the clearest: the type exists to do nothing.

### `BaseSagaService` 638

Closing brace after `throw;`. The agent confirmed the catch's own lines (632-637) are already
executed by the existing `SagaBackfillTests.BaseSagaService_TryRunHookAsync_WorkThrows_PublishesFailedThenRethrowsAsync`
before declining — the check applied correctly rather than the heuristic assumed.

## CM. The repo already has a coverage-exclusions doc — read it first next round

`ai-docs/coverage-exclusions.md` exists and I did not read it until an agent cited it in the final
round. It is the project's own rule for telling a real gap from a genuine exception, and two lines
this round were targeted that it already documents as unreachable by worked example
(`TemplateUtilities._consumeLineEnding`'s guard, and the mutation generators' single-arity
`CommandEndpointAttribute` guard). Those were cycles spent rediscovering a decision already made.

What it says that matters here:

- **The goal is 100%**, and the doc states plainly that a small number of lines are unreachable so
  the achievable figure sits just under it — "contorting a test to fake one of those is worse than
  leaving it red." That is the same conclusion this round reached empirically, arrived at from the
  other direction.
- **`[ExcludeFromCodeCoverage]` is member-level only.** That single fact decides most cases: a
  branch inside an otherwise-tested method cannot be expressed, and applying the attribute at
  method level would suppress genuinely tested code including future regressions in it. This is the
  same constraint the standing instruction states as "never member-level on a member with covered
  lines".
- **Never delete a defensive guard because it is uncovered** — "unreachable today is not
  unreachable after the next refactor."
- **Never assert an unreachable branch via reflection to force the line green** — which is exactly
  why the two `DbContextNotificationConnectionStringFallback` tests were removed this round, and
  why three reflection-into-internal-record tests were deleted earlier. Both decisions were made on
  first principles and both are already written down here.

### What this implies for the residue catalogue

The categories now split three ways for whoever acts on them:

1. **Whole-member unreachable** — the doc's rule 2 says `[ExcludeFromCodeCoverage]` with a
   `Justification` is the correct treatment. `SharedSelfTest`'s failure-recording arm (whose own
   remarks already say it is unreachable "in any build whose merged copies agree, which is the only
   build that ships") is the clearest candidate.
2. **One branch inside a covered member** — the attribute cannot express it, so these stay red by
   design: every closing-brace artifact, every contract guard sitting inside a tested method, every
   dead-by-enclosing-condition line. Documenting them, which is what this file is, IS the treatment.
3. **Write-only members** — eleven sets now. These are the one category the doc does NOT cover,
   because they are not defensive code and not unreachable by construction: they are members
   nothing reads. Suppressing them would hide the fact; the honest fix is deletion, and that is an
   owner decision rather than a coverage action.

I should have read this file in the first cycle. The lesson generalizes past this repo: **before
building a worklist of uncovered lines, look for the project's own record of which lines it has
already decided are unreachable.**

## CN. PRODUCTION BUG on develop: a signal wire-name longer than 20 characters cannot publish

Found by merging develop into this branch and running the suite.

`wh_notify_state.payload_kind` is declared `VARCHAR(20)` in migration
`130_NotifyDebounce.sql`. Migration `137_AdaptiveNotifyDebounce.sql` — which arrived with the
merge — rewrites `_notify_debounced` so the fire path does:

```sql
INSERT INTO wh_notify_state (instance_id, payload_kind, ...)
VALUES (p_instance_id, p_payload, ...)
```

For work doorbells `p_payload` is a short kind: `'inbox'`, `'outbox'`, `'receptor'`,
`'perspective_stream'` — all inside 20. But `PostgresSignalTransport.PublishAsync` on the
`SignalTarget.Streams` path calls `notify_instance_owners(@payload, @stream_ids)` with the
signal's **wire name** as the payload, and a wire name is arbitrary length.

**Consequence:** publishing any `SignalTargeting.Targeted` signal whose wire name exceeds 20
characters throws `22001: value too long for type character varying(20)` out of `PublishAsync`.
Real names are routinely longer — `PerspectiveCoverageGapDetected` is 30.

Evidence it is the merge and not this branch: the same test passed on the pre-merge tree
(`Shard1` reported 760 passed / 0 failed during the sliced measurement) and fails immediately after,
with `130` present in both trees and `137` arriving only with the merge.

Not fixed here — this is production SQL owned by the coordinator work, and the right fix is a
judgement call for its author: widen `payload_kind`, hash or truncate the payload for the state key,
or keep the debounce key separate from the notify payload. Flagged rather than patched.

The test that caught it (`PostgresSignalTransportStreamsTargetTests`) exists to verify Streams
ROUTING, so its wire name was shortened to 17 characters to keep it testing what it was written to
test. It is deliberately not doubling as the reproduction for this defect.

## CO. Two verification catches in the final round's tail

### `PostgresOptions` 97 — the worklist targeted a documentation comment

Line 97 of `PostgresOptions.cs` is `/// </summary>`. A doc comment has no sequence point and can
never be covered by anything; the property it documents sits at line 99.

The agent, given "line 97" as a target, wrote two auto-property round-trip tests for the nearby
property. They pass and cover nothing that was asked for. The property IS read — by both
`CollectiveEventsDapperExtensions` and `CollectiveEventsEFCoreExtensions` — so its accessor is
already exercised by real usage, which makes the round-trip pure filler of exactly the kind removed
twice before (BaseSagaModel, and the write-only sets). File deleted.

Two lessons. The worklist is derived from a coverage report and a line number in it can point at a
non-executable line, so **check that a target line is executable before assigning it**. And the
instruction "cover line N" is enough rope for an agent to write something adjacent that passes —
the brief should say what the line must DO, not only where it is.

### `DapperEventTypeRenameTool` 73 — a test that could not fail

The orphan-pinned-id test seeded one registry row, used an empty catalog, and asserted the result
was EMPTY. That assertion holds whether the loop skipped the row (the intent) or the row was never
visible to the tool at all — and the coverage run showed line 73 unhit, so the loop had not run over
it.

This is the seeding-vacuity rule (BW) recurring in a new project: **when a fixture seeds data, at
least one assertion in the file must prove something came back.** Rewritten to seed a SECOND row
whose pinned id the catalog still carries under a changed name, so the expected result is exactly
one `PendingRename`. The positive half proves the tool saw the seeded rows; the orphan half then
means something. Line 73 now covered, and 75/76 with it.

Worth noting what caught each: the first was caught by a line number that could not move, the
second by a line that did not move. Neither would have been visible from the test's own result.

## CP. Final measurement — and how to measure this repo when a full sweep will not run

**97.8% (117,912 / 120,501), 29 assemblies, 74 merged cobertura reports.** Directly comparable to
the 98.7% baseline: same 29 assemblies, same assembly list, same `-filefilters`.

```
coverable  117,622 -> 120,501   (+2,879)
covered    116,110 -> 117,912   (+1,802)
uncovered    1,512 ->   2,589   (+1,077)
percent       98.7 ->    97.8
```

**The percentage fell because the denominator grew, not because coverage regressed.** Merging
develop brought 46 commits of new production code — 2,879 newly coverable lines, of which 63%
arrive covered. This round closed roughly 350 previously-uncovered lines; the merge added about
1,400 new uncovered ones. Both facts are true at once and neither should be quoted without the
other.

### The measurement recipe, since five full sweeps were OOM-killed

1. **Shut down the build server first.** `VBCSCompiler` reached 4.86 GB, was cleared, and had
   regrown to 4.18 GB hours later. It accumulates across a session of repeated builds and nothing
   reclaims it.
2. **Run in the foreground, chunked.** Background jobs get reaped under memory pressure — even a
   30-second sleep loop was killed — while every foreground build and scoped run survived.
3. **Slice per project**, then merge with the same `reportgenerator` invocation and `-filefilters`
   the script uses. The merge is file-based (`MultiReport (Nx Cobertura)`), so it does not care
   whether one process or forty produced the inputs.
4. **For a project too large for the foreground window**, slice by CLASS-NAME PREFIX:
   `--treenode-filter "/*/*/C*/*"`. Twenty-six letter runs covered
   `Whizbang.Data.EFCore.Postgres.Tests` (2,883 tests) that no single run could finish.

### Two filter traps, both of which produce a green run and no coverage

- **`[Category=ShardN]` filters run the tests and collect NOTHING** — a 178-byte cobertura against a
  real one's 13 MB. All four shards passed, 2,833 tests, zero measured.
- **Character-class prefixes (`[A-C]*`) match no tests at all.** This one nearly fooled me twice:
  the resulting file is still ~13 MB, because a cobertura lists every source line whether hit or
  not. **File size is not evidence of coverage.** The check that works is counting lines with
  `hits > 0` — three "different" slices each reported exactly 1,541, which is what exposed it.

Plain single-letter prefixes (`C*`) work correctly.

## CQ. The DLQ operator-endpoint id guards, confirmed a third time

`BaseSagaService` 80, 309, 562 all moved this round (protected `SagaName` accessor, the
framework-default `LoadProjectionAsync`, and the degenerate-rate fall-through in
`_computeAdaptiveNextDelay`). `DeadLetterOperatorEndpoints` 126, 127, 136, 137, 146, 147, 181 did
not, and will not.

### `DeadLetterOperatorEndpoints` 126-127, 136-137, 146-147, 181 — dead by call site

Third confirmation, reached independently each time (sections BN and its predecessor hold the
first two). `MapWhizbangDeadLetterEndpoints` registers all three id-taking routes with the guid
route constraint:

```csharp
group.MapPost("/{id:guid}/retry",   _handleRetryAsync);
group.MapPost("/{id:guid}/hold",    _handleHoldAsync);
group.MapPost("/{id:guid}/give-up", _handleGiveUpAsync);
```

`GuidRouteConstraint` accepts a segment only when `Guid.TryParse` accepts it, and the route value
ASP.NET stores for a matched segment is the segment string. So by the time a handler runs,
`_tryGetIdFromRoute`'s `RouteValues.TryGetValue("id", …) && raw is string && Guid.TryParse(…)`
chain has already been satisfied by routing itself: `return false` (181) is unreachable, and with
it each handler's `400 BadRequest` + `return` pair. A malformed id is rejected with 404 before any
handler is entered — `DeadLetterOperatorEndpointsTests` already pins that (`/whizbang/dlq/not-a-guid/{action}`
→ `NotFound`), and its own comment says the assertion exists so a change of that contract from 404
to 400 cannot happen silently.

The only seams that would execute the guards are ones that fake the situation rather than
reproduce it: rewriting `HttpContext.Request.RouteValues["id"]` from a middleware between
`UseRouting` and `UseEndpoints`, or invoking the private handler by reflection. Both prove the line
ran and nothing else, which is the failure mode `ai-docs/coverage-exclusions.md` names outright.
Loosening the route template to `{id}` so the guard becomes live is a production contract change
(404 → 400) that an existing test deliberately blocks.

**These seven lines keep resurfacing** because the batch generator reads the merged CI cobertura,
where they are legitimately uncovered, and nothing in the source marks them. The member-level
`[ExcludeFromCodeCoverage]` rule forbids the obvious suppression: `_tryGetIdFromRoute`'s lines
176-179 are covered on every successful request, and each handler's remaining lines are covered
too. A future round that draws this file should read this entry and skip it rather than spend the
cycle rediscovering the call graph.

## CR. Round-24 transports batch: 19 of 31 closed, 12 declined

`src/Whizbang.Transports.AzureServiceBus` and `src/Whizbang.Transports.RabbitMQ`. Closed and
verified by scoped cobertura (`--coverage --coverage-output-format cobertura`, counting
`hits > 0` on the exact line numbers, not file size):

| File | Lines | How |
|---|---|---|
| `AsbTrafficClassOpsRateSource.cs` | 57-59, 62-63, 66-68, 72-73 | the existing tests only ever passed a non-ASB transport, so `_add` returned at its type guard and `_trafficClassFor` never ran at all. Two tests now build real `AzureServiceBusTransport`s over `RaisableServiceBusClient` and **subscribe** them (the projection only exists after a session subscription runs the self-check), give the two namespaces different acceptor budgets so a per-namespace projection cannot be faked by reporting one twice, and bind a routing tag to one of them. |
| `ServiceCollectionExtensions.cs` (ASB) | 146-151, 154-155 | the `ServiceBusClient` factory lambda. Every prior test pre-registered a client to stay offline, so the lambda never ran. It now runs against a connection string the `ServiceBusClient` constructor rejects — deterministic, no DNS, no retry sleep — and the test asserts the factory logged the retry knobs that only the **options pipeline** can produce (configuration says 0/false, the registration callback says 99/true). |
| `RabbitMQChannelPool.cs` | 94 | the empty catch around a stale channel's `Dispose()` in `Return`. `FakeChannel.ExceptionToThrowOnDispose` already existed; what was missing was a rental that spans a `Reset()` **and** whose channel fails to dispose. |

### Declined — `AzureServiceBusHealthCheck` 24-25: a catch over a try that cannot throw

```csharp
try {
  if (_transport is not AzureServiceBusTransport) { return ...Degraded("..."); }
  return ...Healthy("...");
} catch (Exception ex) {                                   // 24
  return ...Unhealthy("...", ex);                          // 25
}
```

The whole try body is one type test and two `HealthCheckResult` factory calls with constant
arguments. `is` never invokes user code, and neither factory can fail on a literal string. The
null guard that *can* throw (`transport ?? throw new ArgumentNullException(...)`) is a
primary-constructor field initializer, outside the try. Nothing a caller can pass reaches the
catch. Case 3 — the member's other lines are covered, so no member-level attribute.

### Declined — `AzureServiceBusTransport` 1682 and 1761: exhaustive-switch defaults

Both are `default: throw new InvalidOperationException($"Unknown AsbReceiveAction: ...")` in
`_deserializeReceivedMessageAsync` and its session overload. `AsbReceiveAction` has exactly four
members and all four have explicit cases. The only producer of an `AsbReceiveDecision` is
`AsbReceiveDecisionMaker.Decide`, whose seven `Action =` sites are the four enum members and
nothing else — no cast, no arithmetic. The decision maker is `internal sealed` and held as
`private readonly AsbReceiveDecisionMaker _decisionMaker = new();`, so there is no injection point,
and both enclosing methods are private. Dead by call site.

### Declined — `AzureServiceBusTransport` 2041: dead by call site

```csharp
if (_adminClient != null) {                       // 2002
  await _applyCorrelationFilterAsync(...);        // 2004 — the ONLY call site
}
...
if (_adminClient == null) {
  throw new InvalidOperationException("Administration client is not available");  // 2041
}
```

`_adminClient` is `private readonly IServiceBusAdminClient?` assigned once in the constructor, so
the guard at 2002 cannot go stale between the two statements. One call site, already guarded.

### Declined — `RabbitMQConnectionRetry` 62 and 111: need a live broker, and the factory is sealed

- **62** is the async epilogue of the connection-string overload, reached only when the delegated
  call returns a connection. Nothing in `src/` calls that overload (production goes through the
  `ConnectionFactory` one from `ServiceCollectionExtensions` and
  `IRabbitMQNamespaceConnectionFactory`), so only a test can reach it, and only with a broker.
- **111** is `LogConnectionEstablished`, guarded by `attempt > 1`: it needs a connection that
  fails and then succeeds. Even a live-broker integration test cannot stage that without taking
  the broker down mid-test.

There is no seam for either. `factory.CreateConnectionAsync` is called on a parameter typed as the
concrete `ConnectionFactory`, and in RabbitMQ.Client 7.2.0 that type is **sealed** — verified by
compiling a subclass: `CS0509: cannot derive from sealed type 'ConnectionFactory'` (and
`CS0115: no suitable method found to override`, so `new`-hiding would not be dispatched to
either). Covering these would mean changing the production signature to `IConnectionFactory`,
which is a public API change made solely to move two lines.

### Declined — `RabbitMQConnectionRetry` 145: brace after a `[DoesNotReturn]` call

`_logAndRethrowConnectionFailure` ends with `ExceptionDispatchInfo.Throw(ex);`. The closing brace
after it is the same brace-artifact shape recorded in CA and CL.

### Re-confirmed — `AzureServiceBusConnectionRetry` 76, 77, 80, 87

Already recorded in **BS**, and unchanged: the success path awaits
`ServiceBusAdministrationClient.GetNamespacePropertiesAsync`, a management-plane round trip the
emulator does not implement, and both the client and the admin client are constructed inline
inside the method with nothing to substitute. 76-77 additionally need `attempt > 1`, i.e. a
failure followed by a success against a live namespace. A fourth pass over this file found no new
seam; a future round should skip it rather than rediscover it.

**Container note for this round.** Neither RabbitMQ nor Azure Service Bus was running locally, so
every `*.Integration.Tests` project in this area was unrunnable. Nothing above was declined *because*
of that alone — each decline has a structural reason that a live broker would not change, except
`RabbitMQConnectionRetry` 62/111, which is the one place a broker genuinely would help and still
would not be worth the public-API change.

## CS. Round 24, Core batch 1 — eight worklist entries that were already recorded, and six new

The worklist for this batch (61 lines, 23 files in `src/Whizbang.Core`) re-listed eight groups that
already have entries above. Recording that here rather than re-deriving them next round:

| lines | existing entry |
|---|---|
| `PolicyContext` 186-192, 221-223 | **BO** — `IMessageEnvelope.GetMetadata` returns `JsonElement?`, so the `string[]`/`IEnumerable<string>`/numeric compatibility arms cannot receive anything |
| `JsonContextRegistry` 143-145, 830, 855 | the `_resolvers.IsEmpty` throw over process-global state, and the `_setter != null` arm no call site supplies |
| `BacklogAgeWorker` 103 | **Z** — `WaitForNextTickAsync` returns false only for a disposed `PeriodicTimer`, and the timer is a `using var` local |
| `ScopedWorkCoordinatorStrategy` 210-212 | dead since Phase H; a pinning test already asserts `TryWrite` is never called |
| `MultiPassMessageTypeBinder` 122-123 | **BT** — deployment-shaped triggers (`FileLoadException`/`BadImageFormatException`) a unit test cannot stage |
| `DefaultMessageSecurityContextProvider` 51, 55 | **CD** — the primary constructor's `?? throw` and `[.. …]` materialization make both fields non-null |
| `ServiceBusConsumerWorker` 226-227 | **BB** — `Task.Delay(Timeout.Infinite, token)` cannot fault with anything but `OperationCanceledException` |
| `ClaimWorker` 294 | **AR** — the `ObjectDisposedException` dispose race, no seam |

### New: `IntervalWorkCoordinatorStrategy` 359-361 — the twin of the Scoped entry

`_routeClaimedInboxWorkToChannel` is byte-for-byte the same method in both strategies, and it is
dead for the same reason: `WorkCoordinatorFlushHelper.ExecuteFlushAsync` returns a `WorkBatch` with
an empty `InboxWork` unconditionally since Phase H moved claiming to `ClaimWorker`, so the
`workBatch.InboxWork.Count == 0` guard above always returns. Worth naming as a pair — the Scoped
copy was recorded a round earlier and the Interval copy was not, which is how a worklist ends up
re-asking about half of a known-dead method.

### New: `IntervalWorkCoordinatorStrategy` 388 — dead by disposal ordering, not a race

`_flushTimerCallback` opens with `if (_disposed) { return; }`. `_disposed` is set at the very END of
`DisposeAsync`, *after* `await _flushTimer.DisposeAsync()` — and `Timer.DisposeAsync()`'s task
completes only once every in-flight callback has finished. So the timer is dead before the flag is
set, and no callback can ever observe it true. The only caller of the method is the timer itself.
This is worth distinguishing from the dispose-race entries (AR): those need an interleaving no seam
provides; this one is not a race at all, it is ordered shut.

### New: `InMemoryTraceStore` 143 — dead by BOTH call sites

`_addChildrenRecursive` opens with `if (chain.Contains(message.MessageId)) { return; }`. Both call
sites — the top-level walk in `GetCausalChainAsync` and the recursive one inside the method — build
their child list with `!chain.Contains(e.MessageId)` in the `Where`, so the argument is filtered
before the call.

The interesting question is whether a sibling can be *added to `chain` by an earlier sibling's
recursion* between the `ToList()` and its own turn in the `foreach`. It cannot, and the reason is
structural rather than incidental: a message carries exactly ONE causation id, so the children sets
of two different nodes are disjoint. For sibling `C2` (causation `X`) to appear inside `C1`'s
subtree, `X` would have to be visited by the recursion — and `X` is already in `chain` when its own
children are computed, so it is never re-entered. The existing
`GetCausalChainAsync_WithCircularReferenceInChildrenTree_…` test in `Whizbang.Observability.Tests`
does not construct a cycle at all (its "circular child" is simply a second child of `child1`), which
is why it never reached this line either.

### New: `DebugAwareStopwatch` 40 — `Debugger.IsAttached`

`Start()` sets `_debuggerWasAttached = true` when a debugger is attached, so `IsApproximate` can warn
that an elapsed time includes breakpoint pauses. No test run has a debugger attached, and the only
way to attach one is `Debugger.Launch()`, which is an interactive prompt. The surrounding lines —
`_stopwatch.Start()`, `Stop()`, `Reset()`, `Elapsed`, `IsRunning`, and `IsApproximate` in its false
state — are covered. Same family as `DebuggerAwareClock:132, 137` (BO), which is mode-gated rather
than debugger-gated but declined for the same reason.

### New: `JsonContextRegistry` 538 — a lambda in the trial branch that is never invoked

`ObjectCreator = () => []` inside the `_inTrialConfigure` arm of `GetLazyPolymorphicListTypeInfo`.
The line is the LAMBDA BODY, so it counts as covered only when a `List<TBase>` is actually
deserialized through that typeinfo. The trial branch exists so a trial-configure thread never caches
scratch-bound typeinfos; `_survivesTrialConfigure` only calls `scratch.GetTypeInfo(derivedType)` —
it configures and throws the whole scratch options away with the thread. Nothing ever deserializes
through the object it returns, so the creator cannot run. Same shape as 830/855 already recorded for
this file: an assigned delegate whose invocation path does not exist.

### New: `TransportConsumerBuilderExtensions` 430 — the "UnknownService" last resort

`_getServiceName`'s final `return "UnknownService"` is reached only when
`Assembly.GetEntryAssembly()` returns null or names a blank assembly. A managed host always has an
entry assembly; null is reserved for a CLR hosted from unmanaged code. Lines 424-426 (the entry-
assembly fallback itself) are NOT residue and are now covered by
`tests/Whizbang.Core.Tests/Workers/TransportConsumerServiceNameFallbackTests.cs`: both
`AddTransportConsumer` overloads call `AddWhizbangInstanceIdentity()`, so a normal composition
always has an `IServiceInstanceProvider` by resolve time — the test reaches the fallback by
composing normally and then `RemoveAll<IServiceInstanceProvider>()` before building the provider,
which is the only condition the fallback exists for.

### New: `DeadLetterRecoveryWorker` 231 and 248-251

Both were already reasoned out in `DeadLetterRecoveryWorkerCoverageTests`' own class doc; moving the
conclusion here so it is where the next round looks.

- **231** is `break;` in the `catch (OperationCanceledException)` around
  `await Task.WhenAny(pollDelay, wakeTask)`. `Task.WhenAny`'s returned task completes *successfully*
  as soon as either constituent reaches any terminal state — it never propagates a constituent's
  cancellation to its own awaiter — and neither constituent is awaited a second time. The two
  constituents cannot throw synchronously either: `Task.Delay(ts, ct)` and
  `SemaphoreSlim.WaitAsync(ct)` both RETURN a canceled task for an already-canceled token rather
  than throwing. So the catch guards a statement that cannot throw the type it catches.
- **248-251** is the loop breaker closing after its cooldown. `_isBreakerOpen` takes wall-clock
  `DateTimeOffset.UtcNow` (no `TimeProvider` seam anywhere in `_scanOnceAsync`), and
  `LoopBreakerCooldownMinutes` is an `int` whose only "auto-close eventually" values are whole
  minutes — `<= 0` means "stay open until restart". Closing the breaker deterministically therefore
  needs a real ≥60-second wall-clock gap between two scans. Declined as written; the tractable fix
  is a clock seam on the worker, not a slow test.

## CT. .NET 10 changed `BackgroundService.StartAsync` — and it silently hollowed out a family of worker tests

**This is not residue. It is a live defect in the test suite, and it explains an intermittent CI
failure.** Found while chasing why five different `catch (OperationCanceledException) { return; }`
lines stayed red despite each having a dedicated, passing test.

`Microsoft.Extensions.Hosting.Abstractions` 10.x:

```csharp
public virtual Task StartAsync(CancellationToken cancellationToken) {
  _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  _executeTask = Task.Run(() => ExecuteAsync(_stoppingCts.Token), _stoppingCts.Token);
  return Task.CompletedTask;
}
```

Two changes from the shape every test in this repo was written against:

1. **`ExecuteAsync` no longer runs synchronously up to its first await.** It is a thread-pool work
   item now. "StartAsync returned, so the worker is parked at its first await" is false.
2. **`Task.Run(action, token)` never invokes the delegate at all if the token is already canceled
   when the work item is dequeued** — the task goes straight to `Canceled`.

So the two shapes these tests use both fail to run the body:

```csharp
await worker.StartAsync(preCanceledCts.Token);   // delegate never invoked
await worker.StartAsync(CancellationToken.None);
await worker.StopAsync(CancellationToken.None);  // cancels before the pool dequeues — often never invoked
```

And the usual assertions — `ExecuteTask.IsCompleted` is true, `ExecuteTask.IsFaulted` is false —
are **both satisfied by a `Canceled` task**. The test goes green having executed no production code.

**Measured, not inferred.** A scoped run of the single test
`OutboxPublishWorkerCoverageTests.ExecuteAsync_StoppedWhileWaitingOnSchemaGate_…` reports
`OutboxPublishWorker..ctor` at 45/45 lines hit and `<ExecuteAsync>d__36::MoveNext` at **0/22** — the
constructor ran, the body never did. The same pattern explains why `PerspectiveMigrationWorker` 37
was hit in one run and 44 in another: it is a thread-pool race, so which lines appear depends on
whether the pool won.

**It also explains two red tests, and their flakiness.** Both are in
`InboxDispatchWorkerCoverageTests`, and which one fails changes between runs because both are races
against the thread pool:

- `SchemaGateCanceledBeforeReady_ReturnsCleanlyWithoutDispatchingAsync` fails with
  `TaskCanceledException` out of `await worker.ExecuteTask!.WaitAsync(...)` — `ExecuteTask` is the
  *canceled `Task.Run` wrapper*, not a body that returned cleanly.
- `ShutdownBeforeConsumersStart_AbsorbsTheCanceledConsumerTasksInsteadOfFaultingAsync` fails
  asserting the "stopped" log line was emitted — it was not, because the body that emits it never
  ran.

Neither is caused by this round's changes: the first failed in a baseline run of this branch before
any edit, the second in the verification run after. They are the same defect wearing two faces.

**The fix, and the rule.** Never treat `StartAsync` returning as evidence the body ran. Wait on a
signal the body itself emits, then stop:

```csharp
private sealed class BlockingGate : ISchemaReadyGate {
  private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task Entered => _entered.Task;
  public bool IsReady => false;
  public void MarkReady() { }
  public async Task WaitForReadyAsync(CancellationToken ct) {
    _entered.TrySetResult();                       // "the body reached the barrier"
    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
  }
}
// await worker.StartAsync(None);  await gate.Entered.WaitAsync(timeout);  await worker.StopAsync(None);
```

`tests/Whizbang.Core.Tests/Workers/SchemaGateShutdownCoverageTests.cs` does this for
`OrphanInboxJanitor`, `CoalesceShipWorker`, `OutboxPublishWorker`, `TransportConsumerWorker` and
`PerspectiveMigrationWorker`, and each case then asserts on the work that must NOT have happened
(no subscription, no scope, no channel read, no pending-rebuild query) rather than only that the
task settled.

**Measured.** With that shape, `OrphanInboxJanitor:80`, `CoalesceShipWorker:81`,
`OutboxPublishWorker:137`, `TransportConsumerWorker:243` and `PerspectiveMigrationWorker:37,44` all
moved from uncovered to covered in one run — the same five lines that stayed red across a full
baseline sweep with the older tests in place.

**Still to do, deliberately not done here** (these files belong to other batches in this round and a
shared working tree makes concurrent edits lossy): the older gate tests in
`OrphanInboxJanitorCoverageTests`, `OutboxPublishWorkerCoverageTests`,
`CoalesceShipWorkerCoverageTests`, `PerspectiveMigrationWorkerCoverageTests`,
`DeadLetterRecoveryWorkerCoverageTests` and `InboxDispatchWorkerCoverageTests` all use the
superseded shape and should be converted to a body-emitted signal. Grep for
`StartAsync` immediately followed by `StopAsync`, and for `StartAsync(alreadyCanceledToken)`.

## CV. Round-24 data/Postgres batch: 13 of 31 closed, and three of BG's "races" were not races

`src/Whizbang.Data.Postgres` + `src/Whizbang.Data.Dapper.Postgres`, 31 lines across 12 files. The
shared `whizbang-test-postgres` container was up, so every line here was **measured** against a
real database rather than argued from the source. Verification throughout: scoped
`--coverage --coverage-output-format cobertura`, counting `hits > 0` on the exact line numbers.

Nine of the twelve files were already recorded in this document by earlier rounds (AZ, BG, BQ, CA,
CE, AA). Re-deriving those arguments is most of what a batch like this costs. Three things are
worth carrying forward.

### 1. Closed — 13 lines

| File | Lines | How |
|---|---|---|
| `DapperPostgresPerspectiveStore.cs` | 80, 88, 104, 109, 113, 129, 215 | Seven API-surface methods with no test at all: both `UpsertWithPhysicalFieldsAsync` overloads, the two scoped `UpsertByPartitionKeyAsync` overloads, `FlushAsync`, `PurgeByPartitionKeyAsync`, and the Guid arm of the partition-key-to-row-id mapping. Written as behavior, not as calls: the overload pairs are separated by whether a **later event may reassign an existing row's tenant** (`forceUpdateScope`), purge is asserted against a second surviving key so a wrong id derivation cannot pass, the Guid key is proved to address the same row a stream-id read finds *and* to differ from the hashed derivation, and `FlushAsync` is exercised on a store built with an unreachable connection string so a regression that made it dial the database fails. |
| `PgSharedNotifyConnection.cs` | 230, 461, 462, 463 | See §2. |
| `PgInstanceLifecycleMonitor.cs` | 69 | The existing `ThrowingBus(new OperationCanceledException())` fixture was already there, used only against `TickForTestsAsync`. Driving the same fake through `StartAsync`'s hosted loop reaches the loop's cancellation arm — and the test never cancels the stopping token, so the loop condition stays true and the 5 s inter-tick delay never faults. The **only** thing that can end that loop is line 69, which makes `ExecuteTask` completing the whole assertion. |
| `PgDurableSignalRetentionWorker.cs` | 74 | See §3. |

### 2. BG's probe-timeout "race" was a missing fixture, not a race

BG recorded `230` and `461-463` as unreachable because the self-test timeout has to fire "strictly
after LISTEN and NOTIFY have succeeded but before the already-sent notification is read back" — a
race against real delivery latency.

That framing assumed the NOTIFY must be delivered. It does not have to be:

```sql
CREATE SCHEMA notify_blackhole;
CREATE FUNCTION notify_blackhole.pg_notify(text, text) RETURNS void LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$;
-- connection string: Search Path=notify_blackhole,public,pg_catalog
```

`pg_catalog` is searched first *unless it is explicitly listed*, so naming it last lets the shadow
win. The probe's `SELECT pg_notify(@channel, 'ping')` then succeeds and delivers nothing — which is
precisely the production failure the probe exists to catch (pgbouncer in transaction pooling
returns from the statement and swallows the delivery). With delivery impossible there is no race
left: the wait loop is entered, the timeout is the only exit, `230` breaks, and `ExecuteAsync`
records the failure and recycles the connection at `461-463`.

The test asserts the gate never publishes `IsAvailable = true` and that `LastFailureReason` names
the probe — an Npgsql connection error would fail that assertion rather than pass quietly, so a
fixture that never reached the probe cannot masquerade as a pass.

**Generalizable:** when a round-trip test needs the round trip to *fail*, look for a way to make
the database lie about it rather than a way to out-run it. A shadowing function on the search path
is a real, supported Postgres mechanism, not a reflection poke.

Still uncovered in that file, and still for BG's reasons: `163-164` (Npgsql converts an internally
timed-out cancel to `TimeoutException`/`NpgsqlException` before it escapes, and a real caller
cancellation fails the `when` filter), `241` and `335-336` (UNLISTEN failing needs the connection
to break inside a specific window), `294` (the `ObjectDisposedException` dispose race, AR).

### 3. All three hosted-loop tests here follow CT's rule, and are backed by measurement

CT (above) is the load-bearing caveat for everything in §2 and §3: under .NET 10,
`BackgroundService.StartAsync` dispatches `ExecuteAsync` to the thread pool, so `StartAsync`
returning proves nothing and `ExecuteTask.IsCompleted && !IsFaulted` is satisfied by a `Canceled`
wrapper that never invoked the body. Each test added in this section waits on a signal the **body**
emits before it asserts or stops — `LastFailureReason` going non-null for the probe, a
`pg_stat_activity` row for the sweep, and for the monitor a bus that has actually been published to
once (`Attempts == 1`) with no cancellation anywhere that could have produced a `Canceled` wrapper.
All four target lines were then confirmed `hits > 0` in a scoped cobertura run, which is the only
evidence that settles it.

### 4. `pg_stat_activity` is the "a pass is in flight" seam AA asked for

AA declined `PgInstanceLifecycleMonitor:69` and `PgDurableSignalRetentionWorker:74` together,
noting the honest route would be "a seam on each worker that reports when a pass starts". The
monitor turned out not to need one (§1). The retention worker does, and the database already has
it:

```sql
LOCK TABLE wh_signals IN ACCESS EXCLUSIVE MODE   -- from a second session, transaction left open
-- then poll: SELECT count(*) FROM pg_stat_activity
--            WHERE wait_event_type = 'Lock' AND query LIKE '%DELETE FROM wh_signals%'
```

The sweep's DELETE parks on the lock indefinitely, and the `pg_stat_activity` row is proof — not a
guess — that the statement is in flight. `StopAsync` then cancels into a genuinely-running sweep.
Npgsql converts the server's `57014 query_canceled` into `OperationCanceledException` when the
token was the cause, so it lands on the cancellation arm.

`ExecuteTask` completing is not enough on its own to prove line 74 (deleting the arm still exits,
one iteration later, via the inter-tick delay). The distinguishing assertion is that **no
sweep-failure warning was logged**: filing a shutdown as a failed sweep would warn on every pod on
every rolling deploy. A recording `ILogger` counting `EventId 1` makes the intended arm the only
one that satisfies both assertions.

### 5. `PostgresSchemaInitializer:470` is not uncovered

The batch listed it; a scoped run of the 64 `PostgresSchemaInitializer*` tests shows it **HIT**,
while `136`, `493` and `746` in the same file and same report are MISS, exactly as BQ says. Recorded
so the next round does not spend a cycle on it. Whatever produced the merged number, `470` is not a
gap.

### 6. New residue

**`PgAppSignalChannel:71`** — `_removeHandler`'s "topic is no longer in `_byTopic`" early return.
Single-threaded it is unreachable by construction: `RemoveHandler` returns `_handlers.Count > 0`,
and every live `HandlerSubscription` removes exactly one entry (its own `Dispose` is idempotent),
so the count reaches zero exactly when the last live handle disposes — and that same call is what
removes the topic. The only way to hold a live handle for an absent topic is the interleaving where
a `Subscribe` reads the existing `TopicSubscription` out of `GetOrAdd` while another thread's last
`Dispose` is between its `RemoveHandler` and its `TryRemove`. **A test for exactly this already
exists** (`PgAppSignalChannelCoverageTests.Subscribe_DisposedConcurrentlyFromManyThreads_…`,
whose own comment names the window) and the line is still red — the interleaving does not land.
Same shape as `NotifySubscriptionRegistry` 45/72 in CA: deterministic assertions, a race that will
not be scheduled. Do not write a second one.

**`PostgresSignalTransport:186`** — the `sink is null || map is null` guard in `_onNotification`.
Dead by call-site ordering. `_onNotification` has exactly two callers, `BroadcastSubscription` and
`InstanceSubscription`, and both are constructed and registered at `StartAsync` lines 80-81 —
after `_sink` (66) and `_wireNameToEntry` (77) are assigned. Neither field is ever reset to null;
there is no `StopAsync` and no disposal path on this type. So no notification can reach the handler
before both are set. (The transport's *other* not-started guard, `_typeToWireName is null` on the
publish side, is live and covered — publishing before `StartAsync` is reachable; receiving before
it is not.)

### 7. Re-confirmed without new work

`CollectivePredicateSqlCompiler` 394-395 (CE), `EventEnvelopeJsonbAdapter` 182 (AZ),
`DapperWorkCoordinator` 232, `PgDutyElector` 165 (CA), `PostgresSchemaInitializer` 136/493/746 (BQ).

One correction to the `DapperCollectiveSpecCompiler:189` entry above: it argued `Expression.Call`
"quotes it or throws" for a bare `LambdaExpression` in a parameter typed `Expression<Func<…>>`.
Measured: it **quotes**. A hand-built `Expression.Call(sParam, setPropertyMethod,
Expression.Lambda<Func<_jobModel,int>>(…), …)` is accepted, compiles to the right jsonb path — and
a scoped cobertura run shows line 188 (the `Quote` arm) HIT with 189 still MISS. The disjunction is
resolved to its first branch; the conclusion (189 unreachable) stands, now on a measurement. The
probe test was not kept: it added no coverage and duplicated an existing quoted-selector test.

## CU. Round-24 generators batch: 6 of 80 closed, and why the other 74 are shaped alike

Batch: 80 uncovered lines over 35 files in `src/Whizbang.Generators`,
`src/Whizbang.Generators.Shared` and `src/Whizbang.Generators.CodeFixes`. Six closed. The rest is
residue, and it is unusually homogeneous: **a source generator's uncovered lines are overwhelmingly
guards against inputs Roslyn's own API contract forbids, or against shapes an upstream method in the
same file has already filtered out.** Measured from a full `Whizbang.Generators.Tests` run with
`--coverage --coverage-output-format cobertura` (2,102 tests), baseline 0/80, after 6/80.

### Closed

- **`ReceptorInfo` 59** — `HasSyncAttributes`. No production caller at all; tested directly the way
  `ReceptorInfoTests` already tests `IsVoid`, across all three states of the slot (null /
  present-but-empty / populated), because the predicate is `is { Length: > 0 }` and a regression to a
  plain null check would claim a sync barrier the receptor never declared.
- **`GuidInterceptorGenerator` 323** — `_isMatchingRestore`'s `!isRestore` arm. Reached by a SECOND
  `#pragma warning disable` (an unrelated code) sitting between the `WHIZ055` disable and the
  `Guid.NewGuid()` call: the restore scan collects every pragma directive in that span, so a
  *disable* lands in `_isMatchingRestore` and has to be rejected on the keyword before its error
  codes are read. Paired with a control that puts a real `restore WHIZ055` in the same slot and DOES
  resume interception, so the negative assertion is attributable.
- **`PerspectiveDiscoveryGenerator` 206-207** — the `<c>typeToValidate is not INamedTypeSymbol</c>`
  arm of `_validateEventStreamId`. The half-written test left in this file aimed at a JAGGED ARRAY
  `TEvent`; that shape does not reach the guard (see the defect below). An **open type parameter**
  `TEvent` does, compiles cleanly, and is the realistic shape: a generic perspective can never be
  stream-keyed at generation time.
- **`ReceptorDiscoveryGenerator` 428 and 672** — `_tryExtractFireAtStage` / `_resolveEnumValueName`
  both recover the enum TYPE from `AttributeClass.GetMembers().OfType<IMethodSymbol>()
  .FirstOrDefault(ctor)?.Parameters.FirstOrDefault()?.Type`. When the FIRST-declared constructor is
  parameterless there is no such type and the chain lands on null. Reached by declaring
  `Whizbang.Core.Messaging.FireAtAttribute` / `Whizbang.Core.Dispatch.DefaultRoutingAttribute` in the
  test source with a parameterless ctor first (source wins over the referenced metadata copy, CS0436)
  — the same shadowing idiom `ReceptorDiscoveryGeneratorCoverageTests` already uses for
  `FireDuringReplayAttribute`. Worth stating what the guard buys: without it the transform throws
  inside the incremental pipeline and the **entire assembly's** receptor registry disappears, not just
  the one receptor. That is what the tests assert.

### The dominant category: Roslyn API-contract guards (33 lines, 21 files)

Every one of these is `if (<Roslyn API result> is null / is not <the type the API guarantees>)
return`. None can fire in a compiling — or even a badly broken — program:

| lines | shape |
| --- | --- |
| `MessageTagDiscoveryGenerator` 71, `AutoPopulateDiscoveryGenerator` 110, `WhizbangIdGenerator` 112/175/236, `EventNamespaceRegistryGenerator` 76/122, `CollectiveApplyDiscoveryGenerator` 62, `MessageTypeCatalogGenerator` 73, `PerspectiveRunnerRegistryGenerator` 70, `PinnedIdRegistryGenerator` 46, `RawReceptorDiscoveryGenerator` 50, `ScopedLensFactoryGenerator` 45, `SignalTypeRegistryGenerator` 41, `TopicFilterGenerator` 80, `PerspectivePurityAnalyzer` 141/261, `PerspectiveModelArrayAnalyzer` 61, `PerspectiveModelConsistencyAnalyzer` 77 | `GetDeclaredSymbol(<declaration syntax>)` returning null / not a named symbol |
| `PinnedTypeLedgerGenerator` 51, `InheritScopeAnalyzer` 64 | `context.Node`/`context.Symbol` not the kind the registration already filtered for |
| `ReceptorDiscoveryGenerator` 170, `MessageJsonContextGenerator` 206 | `context.Attributes.FirstOrDefault() is null` on a `ForAttributeWithMetadataName` provider — a provider that fires only because the attribute is present |
| `MessageRegistryGenerator` 213, `PerspectiveSyncInReceptorAnalyzer` 79, `MintedCompositeConstructionAnalyzer` 155 | `ISymbol.ContainingType` null on a resolved method/property |
| `MessageJsonContextGenerator` 3003, `MintedCompositeConstructionAnalyzer` 115 | `ContainingNamespace` null — it is the global namespace, never null |
| `MessageTagDiscoveryGenerator` 273 | `AttributeClass is null` on an entry that came from `GetAttributes()` |

`TopicFilterGenerator` 98 (`attrClass is null` inside the attribute-matching `Where`) deserves a
line of its own because it is the one that looks reachable: an unresolvable attribute. It is not —
`TopicFilterGeneratorCoverageTests.Generator_UnresolvableAttribute_DoesNotPreventFilterDiscoveryAsync`
already puts `[TotallyUnknownAttributeThatDoesNotExist]` on a command and line 98 stays unhit, because
Roslyn gives an unresolvable attribute an ERROR type symbol, not a null one. Measured, not argued.

### Dead by call site or by an enclosing condition (30 lines)

- **`ReceptorDiscoveryGenerator` 849-850** — the second `Routed<` prefix, without `global::`. Every
  string that reaches `_unwrapRoutedTypeString` is a `FullyQualifiedFormat` display string or a
  substring of one that keeps the prefix (`receptor.ResponseType`, tuple segments,
  collection element types). The un-prefixed constant has no producer.
- **`ReceptorDiscoveryGenerator` 1070** — `_addTupleElement`'s empty-element short-circuit.
  `_extractTupleElements` is only ever handed a real ValueTuple display string, whose top-level
  comma-separated segments are non-empty after `Trim()`. C# cannot express a 0- or 1-arity tuple.
- **`ReceptorDiscoveryGenerator` 1817-1818** — the `useStageFiltering: false` arm. Both call sites of
  `_buildReceptorInvocationsCore` pass `true`. Second instance in this file of a parameterized helper
  keeping an arm no caller selects.
- **`ReceptorDiscoveryGenerator` 1972, 2001, 2016** — `_extractReceptorInfoFromSnippet` returning
  null and the manual-fallback call it guards. `ReceptorRegistrySnippetInvariantTests` pins the
  `new global::Whizbang.Core.Messaging.ReceptorInfo(` marker in all four snippets `_selectSnippet` can
  return, so the marker search always succeeds and the paren walk always closes. The FALLBACK METHOD
  already carries `[ExcludeFromCodeCoverage]` with the reasoning in its remarks; these three lines are
  the same fact leaking out of the excluded member into its caller.
- **`MessageJsonContextGenerator` 2292** — `_isCollectionType` inside `_extractDirectPropertyType`.
  Every name `_isCollectionType` matches starts `global::System.Collections.Generic.`, and the
  immediately preceding line already returned for anything starting `global::System.`. Shadowed guard.
- **`MessageJsonContextGenerator` 2297** — the array short-circuit in the same method.
  `_extractDirectPropertyType` is called only when `_extractElementType` returned null, and
  `_extractElementTypeSingleLevel` returns a non-null element for every `T[]`. So the method never
  sees an array.
- **`MessageJsonContextGenerator` 2248** — `_findTopLevelComma` returning -1. Both callers first
  match a `Dictionary<`/`IDictionary<`/`IReadOnlyDictionary<` prefix and slice between it and the last
  `>`, which is always a two-argument list.
- **`MessageTagDiscoveryGenerator` 248** — `null => "null"` in the Primitive arm. `value.IsNull`
  (`_value == null`) is checked at the top of the method and returns `"null"` there, so a primitive
  constant with a null value never reaches the switch. Shadowed guard.
- **`MessageTagDiscoveryGenerator` 267** — the `default:` arm. `TypedConstantKind` has five members;
  four are cased. The fifth, `Error`, carries a null `_value`, so `IsNull` catches it at the top for
  the same reason as 248. The switch's default has no reachable kind.
- **`MessageTagDiscoveryGenerator` 579** — `_escapeString(null)`. Its call sites pass `tag.Tag`
  (non-nullable, `?? ""` at extraction) or `tag.ExtraJson` guarded by `!string.IsNullOrEmpty`.
- **`PerspectiveRunnerGenerator` 104, 113, 804, 1063, 1083** — all five, one chain.
  `_extractModelType` (804) needs both interface lists empty, but line 94 already returned null in
  that case, which also makes 104 unreachable. 113 (`eventTypes.Count == 0`) cannot fire because
  `_extractSingleStreamInterfaces` requires `TypeArguments.Length >= 2` and `_extractGlobalInterfaces`
  `>= 3`, so the `Skip(1)`/`Skip(2)` always yields at least one event. 1063 and 1083 need a model type
  that is not an `INamedTypeSymbol` — but `_findModelStreamIdProperty` runs first on the same symbol
  and iterates `GetMembers()`, which is empty for both array and type-parameter symbols, so the method
  returns the WHIZ033 warning long before either line.
- **`AutoPopulateDiscoveryGenerator` 395** — `_ => null` over `info.PopulateKind`, which is set to one
  of five constants at extraction; the preceding `if` handles Header and the switch handles the other
  four.
- **`AutoPopulateDiscoveryGenerator` 599** — `_ => "default"` in `_getValueExpression`. Two layers:
  each `_extract*Info` collapses an unknown int to a KNOWN kind name via its own `_ =>` arm, and the
  caller filters to `(Timestamp && SentAt) || Context || Service || Identifier || Header` before
  invoking the selector. Every surviving `(kind, specificKind)` pair has an arm.
- **`TopicFilterGenerator` 155** — `enumValue is null` where `firstArg.Type.TypeKind == Enum`. A
  Roslyn `TypedConstant` whose Type is an enum always carries the boxed underlying value; an argument
  that fails to bind produces `default(TypedConstant)`, whose `Type` is null and so never reaches the
  enum branch.
- **`ServiceRegistrationGenerator` 206** — the `return ServiceCategory.Lens` fallback, which the code's
  own comment already calls "shouldn't happen". `_getServiceCategory` is only called after
  `_isUserInterfaceExtendingWhizbang` returned true, and the two methods loop over the SAME
  `AllInterfaces` with the SAME two prefix tests.
- **`TemplateUtilities` 127** (Shared) — `_consumeLineEnding`'s "not a line ending" bail. Its single
  caller invokes it immediately after `_captureTrailingContent`, which advances the position until it
  hits end-of-string or a `\r`/`\n`. The preceding bounds check covers the first case.
- **`Analyzers/MessageTagParameterAnalyzer` 56** — "skip the `MessageTagAttribute` base class
  itself". It cannot run: the guard above it, `_inheritsFromMessageTagAttribute`, starts its walk at
  `typeSymbol.BaseType`, so it is false for the base class itself and returns first. The skip is
  shadowed by the check that was supposed to make it necessary.

### Provably dead by the language, not by the call graph

- **`SerializablePropertyAnalyzer` 180** — the branch tests for `Nullable<T>` whose type argument has
  `SpecialType.System_Object`. `Nullable<T>` is constrained `where T : struct`; `Nullable<object>`
  cannot exist. The comment above it ("Also check for `Nullable<object>` (`object?`)") confuses the
  nullable-REFERENCE annotation with `Nullable<T>` — `object?` is `System.Object` with an annotation
  and is already caught by the `SpecialType.System_Object` test five lines earlier.

### A whole type nothing constructs

- **`JsonMessageTypeInfo` 64-68** — `internal sealed record JsonWhizbangIdInfo(TypeName, SimpleName,
  ConverterName)`. Its only occurrence in `src/` is its own declaration; `MessageJsonContextGenerator`
  discovers WhizbangId converters by another route entirely. All five lines are the record's primary
  constructor and positional members, never instantiated. This is not a guard to keep — it is dead
  code, and the honest fix is deletion, not a test that constructs it to make the lines green.
  Left in place (deleting production types is out of scope for a coverage round) and recorded here so
  the next reader does not spend the cycle re-deriving it.

### NEW SHAPE — a line that cannot be hit because of how the compiler attributes the IL

- **`PerspectiveSchemaGenerator` 132.** The statement is

  ```
  130    var modelProperties = modelType is INamedTypeSymbol namedModelType
  131        ? namedModelType.GetAllProperties().ToList()
  132        : [.. modelType.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic)];
  ```

  A new test drives an ARRAY `TModel` (`IPerspectiveFor<OrderRow[], OrderIndexed>`) alongside a
  sibling perspective on the bare `OrderRow`, and asserts the two estimated row sizes the generated
  CREATE TABLE comments carry: `~20 bytes` (base overhead, zero properties) for the array one and
  `~140 bytes` (20 + 3x40) for the sibling. The 20 can only come from the FALSE arm running. It
  passes — **and line 132 still reports zero hits.** Lines 130, 131 and 134 are all hit.

  So the collection-expression spread in the false arm of a conditional is lowered such that its
  sequence points land elsewhere; the line can never show a hit even when the branch demonstrably
  runs. The test was kept (the invariant is real and the two-number assertion is not satisfiable by
  one code path), but the line stays in the worklist forever.

  **Check for this before writing a test for any uncovered `[.. ...]` line**: drive the branch, assert
  an observable that only that branch produces, and look at the neighbouring lines in the same
  cobertura. It is the same technique as the closing-brace artifact (CL), applied to a different
  lowering.

### Already recorded, re-confirmed without new work

`PathResolver` 26/31/51 and `PinnedIdCodeFixProvider` 48/75 (CK); `GuidInterceptorGenerator`
105/280/318/417 and `TopicFilterGenerator` 80, each already documented in that generator's own
coverage-test `<remarks>`; `PerspectiveDiscoveryGenerator` 243 and 399, likewise.
`SharedSelfTest` 96 (`failures.Add`) documents itself: the arm runs only when an ILRepack-merged copy
has diverged from the source, which no shipping build does.

### A latent defect found on the way

`TypeNameUtilities.FormatTypeNameForRuntime` throws `NullReferenceException` on a JAGGED array:

```
var assemblyName = typeSymbol is IArrayTypeSymbol arrayType
    ? arrayType.ElementType.ContainingAssembly.Name   // ElementType of T[][] is T[] -> ContainingAssembly is null
    : typeSymbol.ContainingAssembly.Name;
```

`PerspectiveDiscoveryGenerator` calls it (line 121) BEFORE it validates event types (line 126), so a
perspective declared `IPerspectiveFor<TModel, TEvent[][]>` does not get the WHIZ030 it deserves — the
generator raises CS8785 and **every** perspective registration in the assembly is lost. Reaching it
needs source that already violates `where TEvent1 : IEvent`, so it is behind a compile error and was
left alone; noted because it is what made the half-written jagged-array test in
`PerspectiveDiscoveryGeneratorCoverageTests` unfinishable, and the next person to try that shape
should know why.

### Operational note, third round running

Two agents measured `Whizbang.Generators.Tests` with `--coverage` at the same time. **Concurrent
coverage collectors on the same test assembly silently produce an EMPTY report** — a 178-byte
cobertura with `<packages />` and a green test run. Six consecutive scoped attempts returned 178
bytes while another agent's retry loop was running; the same command returned 20 MB once it stopped.
The size check is the tell, and it belongs in any retry loop: `stat -f%z` on the output, retry if it
is under a megabyte. Same family as the shard-filter trap in CP, different cause.

## CW. Round-24 EFCore/transports generators batch: 9 of 55 closed, and the record-model trap

Batch: 55 uncovered lines over 11 files in `src/Whizbang.Data.EFCore.Postgres.Generators`,
`src/Whizbang.Transports.HotChocolate.Generators` and
`src/Whizbang.Transports.FastEndpoints.Generators`. Tests live in
`tests/Whizbang.Generators.Tests` (the EFCore *generators*; the EFCore *analyzers* are tested from
`tests/Whizbang.Data.EFCore.Postgres.Tests`). Verified by scoped cobertura from a full
`Whizbang.Generators.Tests` run — 2,102 tests, all green — counting `hits > 0` on the exact line
numbers before and after. Baseline for every line below was 0.

### Closed

| File | Lines | How |
|---|---|---|
| `EFCorePerspectiveConfigurationGenerator` | 683, 706, 709, 729, 771 | see the record trap below — the existing tests for these shapes could not fail |
| `EFCoreServiceRegistrationGenerator` | 552 | `_extractPhysicalFields(modelType as INamedTypeSymbol)` with a null argument. Reached by the generic-base-perspective shape: `abstract class SharedPerspectiveBase<TModel> : IPerspectiveFor<TModel, TEvent>` is itself a discovered candidate, and its model symbol is an `ITypeParameterSymbol`, so the `as` yields null. Asserted against a closed sibling perspective whose `[PhysicalField(ColumnName = "ext_id")]` DOES reach the DDL, so "no columns" is attributable to the open model rather than to physical-field extraction being dead. |
| `EFCoreServiceRegistrationGenerator` | 618 | `depth > MAX_COALESCE_DEPTH` in `_appendCoalesceStatements`. The cycle guard only stops a type that repeats, so a chain of nine DISTINCT nested classes has nothing to catch it. `Level8` carries a `List<string> InsideCap` that IS coalesced and `Level9` a `List<string> PastCap` that is not — the positive half is what proves the walk actually descended eight levels rather than stopping early for some other reason. |
| `GraphQLMutationTypeGenerator` | 76 | |
| `RestMutationEndpointGenerator` | 77 | the `!attrClass.IsGenericType \|\| TypeArguments.Length < 2` arity guard. The attribute is matched by a **prefix** on its display string, so any type in `Whizbang.Transports.Mutations` whose name starts `CommandEndpointAttribute` reaches the type-argument read; a non-generic one declared in the test source does. Each test also carries a valid two-argument endpoint in the same compilation and asserts THAT one is generated, so an empty output file cannot pass. |

### The trap: a record model makes every polymorphic-detection test vacuous

`EFCorePerspectiveConfigurationGeneratorCoverageTests` already had tests for
`Dictionary<string, Abstract>`, `List<Wrapper>`, `IReadOnlyList<Abstract>` and `[JsonPolymorphic]`.
All were green. None of the code they name had ever run.

Every one of them used a **record** as the perspective model. A record has a compiler-generated
`EqualityContract` property of type `System.Type`, `System.Type` is an abstract class, and
`_checkForPolymorphicTypes` is a `.Any(...)` over the model's properties in declaration order —
so the FIRST property of any record model answers "polymorphic" before the property under test is
ever looked at. The tests asserted the polymorphic marker and got it, from a property they did not
write.

The same file's own comment in `EFCoreServiceRegistrationGeneratorCoverageTests` names the hazard
("records classify polymorphic via compiler-generated EqualityContract in other generators"), so it
was known — it just had not been applied here. The five tests added here use **class** models, and
a sixth gives the identical property shapes a fully concrete graph and asserts the marker is
ABSENT, so the marker is not something every class model produces.

One shape genuinely was unreachable the way it was written. `Dictionary<K, V>` does **not** reach
`_hasPolymorphicTypeArguments`: `Dictionary` exposes an explicit
`IReadOnlyDictionary<TKey,TValue>.Values` property typed `IEnumerable<TValue>`, `_isAnalyzableProperty`
does not filter by accessibility, and `IEnumerable<` IS in the recognized-collection list — so the
abstract value type is found by the member walk (line 683) and the type-argument scan never runs.
Lines 706 and 709 needed a generic type that does **not** surface its argument as a property:
`HashSet<T>` does exactly that, reaching them via its `Comparer` property (`IEqualityComparer<T>`).
That also happens to be the more valuable test — a set of an abstract element type is a model shape
`_getCollectionElementType` does not recognize, and if it were not detected the model would be
routed back to `ComplexProperty().ToJson()`, which cannot round-trip it.

### Already covered — worklist entry that did not need work

`EFCorePerspectiveConfigurationGenerator` 715 (the closing brace of `_hasPolymorphicTypeArguments`)
reads `hits=1` in a scoped local run **before** any change in this round; 704, 705, 708 and 714 in
the same method are hit as well. The merged CI report listed it as uncovered. Line numbers around
it are demonstrably correct (233/281/323 and 683/706/709/729/771 all match the code they describe),
so this is a report artifact, not drift.

### Declined — Roslyn API-contract guards (9 lines, 9 files)

All of the form `if (GetDeclaredSymbol(<declaration syntax>) is not INamedTypeSymbol) return null;`
or `is null`. Roslyn's contract guarantees a named type symbol for a class/struct/type declaration,
including for a declaration full of errors. Same category as the 33 lines catalogued in CU.

| file | line |
|---|---|
| `EFCoreServiceRegistrationGenerator` | 256, 446 |
| `EFCorePerspectiveConfigurationGenerator` | 233, 323 |
| `EFCorePerspectiveAssociationGenerator` | 60 |
| `PerspectivePersistenceJsonContextGenerator` | 116 |
| `GraphQLMutationTypeGenerator` | 53 |
| `GraphQLLensTypeGenerator` | 57 |
| `RestMutationEndpointGenerator` | 54 |
| `RestLensEndpointGenerator` | 57 |

`EFCoreServiceRegistrationGenerator` 841 belongs here too: `GetTypeInfo(genericName).Type is not
INamedTypeSymbol`. A generic name in type position binds to a named type or to an ERROR named type;
both implement `INamedTypeSymbol`. This is the same fact CU measured for `TopicFilterGenerator` 98.

### Declined — `_deriveSchemaFromNamespace` empty-namespace default: dead by call site, and it hides a bug

`EFCoreServiceRegistrationGenerator` 394 and `EFCorePerspectiveConfigurationGenerator` 281 are both
`if (string.IsNullOrEmpty(namespaceName)) return "public";`. Each has exactly one call site, and
both pass `symbol.ContainingNamespace.ToDisplayString()`. For a type in the global namespace that
returns the literal string `"<global namespace>"`, **not** `""` — measured, with a
`[WhizbangDbContext] class RootDbContext : DbContext` at global scope: the guard did not fire and
the generated DDL came out as `CREATE SCHEMA IF NOT EXISTS ""<global namespace>""` with
`SchemaInitializationLockKey.Compute("<global namespace>")`. So the fallback is unreachable AND the
case it was written for is mishandled. Left alone — fixing it is a production change, not a coverage
change — but it is worth a follow-up: the guard wants
`ns.IsGlobalNamespace` (or `ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)` compared
against the sentinel), not `IsNullOrEmpty`.

### Declined — dead by an enclosing condition or by the pipeline predicate (5 lines)

- **`EFCoreServiceRegistrationGenerator` 835 and 854.** The syntax provider's predicate is
  `node is ParameterSyntax { Type: GenericNameSyntax { TypeArgumentList.Arguments.Count: >= 2 } }`.
  The transform then re-checks `parameterSyntax.Type is not GenericNameSyntax` (835) and
  `type.TypeArguments.Length < 2` (854). Neither can differ from what the predicate already
  established — a syntactic two-argument generic name binds to a two-argument type.
- **`EFCoreServiceRegistrationGenerator` 531 and 543.** `_extractKeysFromAttribute`'s
  `ConstructorArguments.Length == 0` and non-array-kind arms. Its two call sites pass
  `WhizbangDbContextAttribute` and `WhizbangPerspectiveAttribute`, matched by EXACT display string.
  Both declare a single primary constructor, `params string[]? keys`, so Roslyn always supplies
  exactly one constructor argument of `TypedConstantKind.Array` — `[WhizbangDbContext]` yields an
  empty array, not zero arguments.
- **`LensQueryTypeArgumentAnalyzer` 64.** `method.TypeArguments.FirstOrDefault() == null` after
  `_isTargetMethod` has already required `method.IsGenericMethod && method.TypeArguments.Length == 1`.

### Declined — `PerspectiveModelPolymorphicAnalyzer` 216: dead by the type system

```csharp
private static INamedTypeSymbol? _getCollectionElementType(INamedTypeSymbol type) {
  if (type is IArrayTypeSymbol arrayType) {          // 216
    return arrayType.ElementType as INamedTypeSymbol;
  }
```

The parameter is `INamedTypeSymbol`. No Roslyn symbol implements both `INamedTypeSymbol` and
`IArrayTypeSymbol` — arrays are `IArrayTypeSymbol` only. The compiler permits the test because both
are interfaces; nothing can satisfy it. (`EFCorePerspectiveConfigurationGenerator` has the same
helper WITHOUT the array arm, which is the shape that is actually correct here.)

`PerspectiveModelPolymorphicAnalyzer` 244 and `PerspectiveModelDictionaryAnalyzer` 218 are the
`type.ContainingNamespace?.ToDisplayString()` null arm of `_isSystemPrimitiveType` — the global
namespace is a symbol, never null. Third and fourth instance of the `ContainingNamespace` guard
already catalogued in CU.

### Declined — "the generator would have to ship broken" (15 lines)

- **`EFCoreServiceRegistrationGenerator` 176-184** — the `catch` around `_generateRegistrationMetadata`
  reporting EFCORE996. Its sibling callbacks (997/995/994) can be made to throw by forcing duplicate
  `AddSource` hint names, and
  `Generator_WithDuplicateDbContextClassNames_ReportsGeneratorErrorDiagnosticsAsync` does exactly that
  — and asserts EFCORE996 is NOT among them, because this callback's hint names are fixed. The only
  indexing inside is `dbContextGroups[0]`, and `_groupPerspectivesByDbContext` returns one entry per
  DbContext under a `dbContexts.IsEmpty` early return, so it cannot be empty. No reachable throw.
- **`EFCoreServiceRegistrationGenerator` 1264 and 1353-1364** — `_tryLoadRegistrationSnippets`'s
  failure path and the caller's abort. The snippets are embedded resources OF THE GENERATOR ASSEMBLY;
  extraction fails only if the generator itself is built wrong.
- **`EFCoreServiceRegistrationGenerator` 2166 and 2181** — `migrationResources.Length == 0` and
  `GetManifestResourceStream(name) == null` for a name `GetManifestResourceNames()` had just
  returned. Same category.

### Note for the next round: the generator PDBs do not survive an incremental copy

`Whizbang.Data.EFCore.Postgres.Generators`, `.Transports.HotChocolate.Generators` and
`.Transports.FastEndpoints.Generators` are ILRepack-merged. After a rebuild, the merged `.dll`
lands in `tests/Whizbang.Generators.Tests/bin/Debug/net10.0/` but the matching `.pdb` does not —
and with no PDB the coverage collector **silently omits the whole assembly**. The run is green, the
cobertura is a normal multi-megabyte file, and the generator simply is not in it (grep for
`filename=` and the class is absent). Distinct from the 178-byte empty-report trap: the file looks
healthy. Recipe that works:

```
dotnet build tests/Whizbang.Generators.Tests/... -c Debug --no-restore
for p in <the three generator projects>; do
  cp -p src/$p/bin/Debug/netstandard2.0/$p.pdb tests/Whizbang.Generators.Tests/bin/Debug/net10.0/
done
dotnet run -c Debug --no-build -- --coverage --coverage-output-format cobertura ...
```

Do the build, the copy and the run as ONE command. With other agents building concurrently, the
src-side `.dll` gets replaced mid-window and the copied PDB then belongs to a different build than
the `.dll` in the test output — which the collector also silently ignores. `cmp -s` the two DLLs
before trusting a result.

## DA. Round 24, Core batch 2: proofs from the callee, and one seam worth adding

Twenty-three files, sixty-one lines. Fifteen closed, forty-six unreachable — but several of the
forty-six were previously recorded as "declined" when the sharper answer is "cannot happen", so the
reasons below replace the earlier ones. Three of the fifteen are CT's .NET 10 defect in this
batch's files, wearing a disguise I nearly filed as residue.

### The proof usually lives in the callee, not the file with the uncovered line

Five of this batch's entries look like ordinary defensive arms until you read what the method they
call actually does. In each case the callee has already handled the condition the guard is
watching for.

**`SlidingWindowApplyBatchStrategy` 183 / `SlidingWindowOutboxBatchStrategy` 150** — the outer
`catch (OperationCanceledException)` in `_drainBufferAsync`. BU and BY recorded these as "an outer
catch whose inner handlers absorb everything reachable". The proof is stronger and it is in
`SlidingWindowBatcher.ReadBatchesAsync`, which **never throws**: it converts every cancellation
into a `yield break` — the loop condition (66), the catch around the first `WaitToReadAsync` (71),
the re-check after `WhenAny` (111), and the catch around the arrival await (124). The only other
awaited call inside the drain loop is `_flush`, whose `OperationCanceledException` is taken by the
filtered catch when `_stopCts` is canceled and by the general `catch (Exception)` when it is not.
Nothing inside the `try` can hand an `OperationCanceledException` to the outer catch.

**`Dispatcher` 1489-1490** — `_cascadeEventsExcludingResponseAsync`'s `result == null` arm. Its
single call site (1458) is preceded by `ResponseExtractor.TryExtractResponse<TResult>`, whose FIRST
statement is `if (result == null) { response = default; return false; }`, and a `false` return
throws at 1448. A null result cannot reach 1458.

**`Dispatcher` 4145** — the trailing `return null` of `_extractStreamIdFromMetadata`. The only
argument it is ever passed (3998) is the output of `_createHopMetadata`, which returns either
`null` — handled at 4135 — or a dictionary holding exactly one entry, `["AggregateId"]`, built by
`JsonDocument.Parse("\"" + streamId.Value + "\"")`. A non-null dictionary therefore always has a
`String`-kind `AggregateId` that `Guid.TryParse` accepts, and always returns at 4142.

**`SystemEventEmitter` 196** — the `_ => null` arm of the tenant switch. The enable guard at
174-177 falls through only when `IsEnabled<TSystemEvent>()` is true, or when `AuditEnabled` holds
AND `TSystemEvent` is `EventAudited` or `CommandAudited`; and `SystemEventOptions.IsEnabled(Type)`
returns true for those two types alone. Both are `sealed record`s, so the two preceding switch arms
cover every value that can reach the switch.

**`MessageEnvelope` 343** — the inner `return null` of the obsolete `GetCurrentSecurityContext()`.
It is guarded by `Hops[i].Type == HopType.Current && Hops[i].Scope != null`, which is exactly the
predicate `GetCurrentScope()` filters on — so its `Aggregate` has at least one element and returns
`ScopeDelta.ApplyTo(...)`, whose `_applyValueChanges` starts from
`previous?.Scope ?? new PerspectiveScope()` and can never produce a `ScopeContext` with a null
`Scope`. Inside that branch `scope?.Scope != null` is always true.

### Dead by call site, where the call site's own comment says so

**`TransportPublishStrategy` 519-522** — "Null check should never trigger due to early return in
PublishAsync, but be defensive". Correct: both call sites of `_resolveDestination` (176 in
`PublishAsync`, 309 in the batch overload) sit behind `if (string.IsNullOrEmpty(work.Destination))`
early returns, and `OutboxWork.Destination` is a record property with no mutation in between.

**`Dispatcher` 3762** — `if (composite is not ICompositeEvent) return;`, whose two call sites
(3211, 3310) are both inside `if (eventData is ICompositeEvent && _isOwnedNamespace(...))`.

**`IntervalUnitOfWorkStrategy` 189** — the `catch (OperationCanceledException)` around
`await _flushTask` in `DisposeAsync`. `_flushTask` is `_runFlushLoopAsync`, whose entire body is
already wrapped in a `catch (OperationCanceledException)`. It can only end `RanToCompletion`, or
`Faulted` with a non-OCE — never Canceled and never faulted with an OCE. Exactly the shape recorded
for `BatchFlusher` 142.

**`SignalHandlerList` 27** — `_remove`'s `index < 0` guard. `Add` is the only writer, and
`Subscription.Dispose` — the only caller of `_remove` — is one-shot via
`Interlocked.Exchange(ref _handler, null)`. Every handler reaching `_remove` was added to this
list, and removals can never outnumber adds for an equal delegate, so `Array.IndexOf` (which
compares delegates structurally, not by reference) always finds one.

**`BasePollSignalSource` 75** — `_onTick`'s null-sink guard. `_sink` is assigned in `StartAsync`
*before* `_clock.CreateTimer` is called, and nothing sets it back to null — the class has no Stop
and no Dispose. The timer that invokes `_onTick` does not exist until after the assignment.

**`DebuggerAwareClock` 326** — the `catch (ChannelClosedException)` in `_readLoopAsync`.
`_pauseStateChannel` is completed only by `Dispose()`, through the no-argument `TryComplete()`, and
a gracefully completed channel ends `ReadAllAsync`'s enumeration rather than throwing. (132 and 137
remain as recorded: mode-gated, since the sampler timer only exists for `CpuTimeSampling`/`Auto`.)

**`SerialExecutor` 217-223** — `RecordDefensiveException` around `workItem.ExecuteAsync`. The
delegate is always `_executeWithPooledStateAsync<TResult>`, whose whole body is
`try { … } catch (Exception ex) { state.Source.SetException(ex); } finally { … }`. A queued item
goes through `ExecuteAsync` **or** `CancelAsync`, never both, so the source is completed exactly
once and `SetException` cannot throw a double-completion either. This is the current line range for
the entry the original table recorded as line 210.

**`ScopeDelta` 381** — `_scopesEqual(null, null)`. `IScopeContext.Scope`, `ScopeContext.Scope` and
`SecurityExtraction.Scope` are all non-nullable `PerspectiveScope` (the latter two `required`), so
`b` (`current.Scope`) is never null at the only call site. Only the `a == null` half — 383-384,
covered — is reachable.

**`EnvelopeSerializer` 40-45** — see BX; re-confirmed unchanged this round.

### Races with no seam

**`SerialExecutor` 185-190** — `RecordDefensiveCancellation` in `DrainAsync`, the current range for
the entry recorded as line 182. It needs `_workerTask` to end canceled while `_state` is still
`Running`. `StopAsync`/`DisposeAsync` set `_state = Stopped` under `_stateLock` before cancelling
`_workerCts`, and `DrainAsync` returns early under that same lock — so the only window is between
`DrainAsync` releasing the lock and reaching its `await _workerTask`, reachable only by a concurrent
`StopAsync`. Any test for it is a scheduling coin flip.

**`InboxDeserializeCache` 124** — the double-checked `overflow <= 0` recheck inside
`_evictionLock`. Two `Set` calls must both pass the unlocked `Count > _maxEntries` test and the
first must evict enough that the second finds nothing to do. The recompute is the first statement
after the lock is taken, so there is no seam between the two reads.

**`DebuggerAwareClock` 112** — `_sampleCpuTime`'s `_disposed` early return. `Dispose()` sets the
flag and then disposes the `System.Threading.Timer`; reaching 112 needs a callback that was queued
before the disposal but had not started when the flag was set. Forcing that means starving the
thread pool of workers so queued callbacks cannot start, which blocks pool threads for the rest of
the suite.

**`PerspectiveCursorCache` 212, `LeaseHandle` 166, `WhizbangIdProviderRegistry` 148,
`PerStreamSerializer` 198 and 225** — already recorded (AO, BY, and the two entries near the end of
BY); re-confirmed unchanged.

**`PerspectiveRunnerCallbackRegistry` 53** — the `_callbacks.Count == 0` guard, the exact twin of
the `WhizbangIdProviderRegistry` 148 entry. This assembly's own generated
`PerspectiveRunnerRegistry.g.cs` calls `RegisterCallback` from a module initializer, so the static
list is non-empty before the first test runs, and there is no clear seam.

### The one seam worth adding

`SlidingWindowApplyBatchStrategy` 188 and `SlidingWindowOutboxBatchStrategy` 155 — the
`_runIdleSweepAsync` disposed guard — were declined in BU/BY as "a timer callback that must fire in
the statement gap between `Interlocked.Exchange(ref _disposed, 1)` and
`_idleSweepTimer.DisposeAsync()`". That is true of the *timer*, but the sibling class in this same
family, `PerStreamSerializer`, already exposes `public Task RunIdleSweepNowAsync()` for exactly this
reason, and `PerspectiveCursorCache` exposes `RunSweepNowForTests()`. Both strategies now carry
`internal Task RunIdleSweepNowForTestAsync()`, and the guard is covered by a test that shuts the
strategy down, advances a `FakeTimeProvider` well past the eviction window, drives one sweep, and
asserts the mapped buffer is still mapped — which is only true because the guard returned first.

The sweep's `catch { }` around `await buffer.Worker` (Apply 203, Outbox 170) stays declined even
with the seam. Given the `ReadBatchesAsync` proof above, the drain worker cannot fault at all
unless the injected `ILogger` throws, so a test for that catch would be staging an exception the
class cannot otherwise produce.

**Line numbers moved.** Adding the seam shifted the two files: `SlidingWindowApplyBatchStrategy`
183/188/203 are now 190/195/210, and `SlidingWindowOutboxBatchStrategy` 150/155/170 are now
157/162/177. Worklists built before this round will point one method too high in both files.

### CT's .NET 10 defect again — this time it looked like a stale worklist

`BackupTickCoordinator` 80 and `PerspectiveCompletionFlushWorker` 53-54 both had a committed,
passing, dedicated test. Measuring the first produced two contradictory readings of the SAME test
against the SAME binary:

- filter `/*/*/BackupTickCoordinatorCoverageTests/*` (three tests) — line 80 `hits="0"`, and the
  `ExecuteAsync` entry point at line 70 `hits="1"` even though two of the three tests start a
  coordinator;
- the same filter narrowed to that one test — line 80 `hits="1"`.

The first reading is the true one and the cause is CT, not a measurement artifact: since .NET 10,
`BackgroundService.StartAsync` does `Task.Run(() => ExecuteAsync(token), token)`, so the body is a
thread-pool work item. `ExecuteAsync_SchemaGateCanceledDuringStartup_…` did
`StartAsync(cts.Token)` then `StopAsync(None)` — which cancels the linked token, and a `Task.Run`
whose token is already canceled when the item is dequeued never invokes the delegate at all. Run
alone, the pool wins and the body runs; run alongside other tests, it often does not. Both
assertions (`IsCompleted` true, `IsFaulted` false) are satisfied by that `Canceled` wrapper, so the
test is green either way. `PerspectiveCompletionFlushWorkerTests.WhenDisabled_NothingIsWrittenAsync`
has the same shape, and its "nothing was written" assertion is satisfied outright when the body
never ran.

Both are rewritten to CT's shape — wait on a signal the body itself emits, then assert on the work
that must NOT have happened:

- the schema gate now signals `Entered` before returning its faulted task, the test waits on that,
  and **the stopping token is never canceled at all**. That is what makes `IsCompleted` mean
  something: with the token live, the only exit from `ExecuteAsync` other than the schema-gate catch
  is the polling loop, which would run until shutdown. A registered backstop tick and
  `IdleThreshold = 0` give the second assertion — a coordinator that swallowed the cancellation and
  carried on would have fired that tick against a database whose migration has not run.
- the flush worker's test waits for the `LogDisabled` `EventId` through a capturing logger, then
  asserts `ExecuteTask.IsCompleted` is **false** — the disabled branch parks on an infinite delay,
  and an early `return Task.CompletedTask` (the obvious "simplification") would tell the host the
  service had finished while its channel is still open.

After the rewrites, line 70 reads `hits="2"` — both coordinator tests now really enter the body —
and 80, 53 and 54 are hit on every run. **The lesson to carry: a line the worklist calls uncovered
that has an obvious passing test is a CT candidate, not a stale worklist entry.** Grep the test for
`StartAsync` followed by `StopAsync`, or `StartAsync(alreadyCanceledToken)`, before assuming the
measurement was wrong.

### One tooling note

A `}` that closes a `try` and opens a `catch` on one source line carries TWO sequence points, so a
per-line checker that takes the max reports HIT when only the try-end ran. That is exactly the shape
of `} catch (OperationCanceledException) {`. Read the `return`/`break` INSIDE the catch, never the
catch line itself — several of the declines above would have looked covered otherwise.

### Closed this round — fifteen lines, each verified by re-measuring the line

| File | Lines | Test |
|---|---|---|
| `Observability/NotifyDebounceMetrics.cs` | 44, 52, 60, 66, 71 | `NotifyDebounceMetricsTests.Gauges_ProjectEveryCachedReading_TaggedByPayloadKindAsync` + `…_ReportTheLatestCachedReading_NotTheFirstAsync` |
| `Observability/NotifyDebounceStatsCollector.cs` | 52, 69 | `NotifyDebounceStatsCollectorTests.ShutdownDuringSchemaWait_ExitsWithoutQueryingTheProviderAsync`, `…ProviderThrowsObjectDisposed_LeavesTheLoopInsteadOfRetryingAsync` |
| `Security/ScopeDelta.cs` | 459, 464 | `ScopeDeltaCoverageTests.CreateDelta_ScopeWithSeveralExtensions_…`, `…_ExtensionWithNullValue_SerializesAnExplicitJsonNullAsync` |
| `Signals/BasePollSignalSource.cs` | 98 | `PollSignalSourceTests.TimerTick_DetectionThrows_GoesToOnTickErrorAndTheSourceKeepsPollingAsync` |
| `Workers/SlidingWindowApplyBatchStrategy.cs` | 188 (now 196) | `SlidingWindowApplyBatchStrategyCoverageTests.IdleSweep_AfterShutdown_LeavesTheBuffersAloneAsync` |
| `Workers/SlidingWindowOutboxBatchStrategy.cs` | 155 (now 163) | `SlidingWindowOutboxBatchStrategyCoverageTests.IdleSweep_AfterShutdown_LeavesTheBuffersAloneAsync` |
| `Workers/BackupTickCoordinator.cs` | 80 | `BackupTickCoordinatorCoverageTests.ExecuteAsync_SchemaGateCanceledDuringStartup_ReturnsWithoutFaultingAsync` (rewritten) |
| `Workers/PerspectiveCompletionFlushWorker.cs` | 53, 54 | `PerspectiveCompletionFlushWorkerTests.WhenDisabled_ExecuteAsyncParksUntilShutdownAsync` |

Two of these are worth copying elsewhere. The gauge test asserts `sampled.Count == 8` — four
instruments times two seeded payload kinds — because the failure mode a per-kind gauge actually has
is collapsing into ONE aggregate measurement, and every per-instrument value assertion still passes
when that happens. And the `ObjectDisposedException` test starts the collector with
`CancellationToken.None`: the only thing that can end that task inside the timeout is the `break`
under test, since the log-and-continue arm would sit in a fifteen-second delay. Cancelling anything
would have made the assertion pass either way.

## CX. Round-24 data/EFCore.Postgres batch: 20 of 38 closed, 18 declined

Batch: 38 uncovered lines across 14 files in `src/Whizbang.Data.EFCore.Postgres`. A live
`pgvector/pgvector:pg17` container was available, so the Postgres-backed suites ran locally and
every claim below was verified by re-running the scoped suite under
`--coverage --coverage-output-format cobertura` and counting `hits > 0` on the exact line numbers.

### Closed (20)

| File | Lines | How |
| --- | --- | --- |
| `EFCoreLensQueryFactory.cs` | 83,84,85,87 | The synchronous `Dispose()` had no test at all — only `DisposeAsync` did. Three tests: it returns the pooled context exactly once, the disposed latch suppresses a second disposal, and it closes the factory to `GetQuery`. |
| `EFCoreWorkCoordinator.cs` | 404,486,920 | `CREATE OR REPLACE` of `reclassify_events_ephemeral` / `register_type_definition` / `wh_integrity_ledger_summary` with `BEGIN RETURN; END` — same declared signature, no rows. Follows `CountOutstandingWorkNoRowSqlTests`. |
| `EFCoreWorkCoordinator.cs` | 2939 | The lost advance race. A `BEFORE UPDATE` trigger on `wh_settings` returning NULL for the watermark key reproduces the loser's only observable — its conditional UPDATE matched zero rows. |
| `EFCoreWorkCoordinator.cs` | 4325 | An orphaned lifecycle event with a NULL `scope` column. The helper's null guard is what keeps the row in the reconciler's list; without it the per-row catch swallows the event and the lifecycle completion is never written. |
| `EFCoreWorkCoordinator.cs` | 4511 | `ReapExhaustedOrphanedPerspectiveRowsAsync` with an empty stream list, added to the existing empty-input class. |
| `IntegrityManifestReceptors.cs` | 589,839,840,882 | See "two existing tests that proved nothing" below. |
| `EFCorePostgresPerspectiveCheckpointCompleter.cs` | 84,175 | A `HasDefaultSchema` context plus `svc.wh_perspective_cursors` created `LIKE public.… INCLUDING ALL`; the cursor must land in the service schema and NOT in public. Line 84 (`} finally {`) came along with it — it was NOT the brace artifact it looks like. |
| `EFCorePostgresLensQuery.cs` | 182,183 | Split-mode `FilteredScopedAccess`. A hydrator registered against a model type unique to the new class (the split-mode decision latches into a `static readonly` on first touch of the closed generic type), then assert the rows come back tenant-filtered AND tracked AND that the hydrator actually ran. |
| `EFCorePerspectiveReplayReader.cs` | 119 | `DROP TABLE wh_perspective_events CASCADE` makes the pending-id read raise 42P01; the connection the reader opened must still be closed. A replay that keeps failing otherwise leaks one pooled connection per attempt. |
| `EFCoreDeadLetterRecoveryService.cs` | 123 | `evaluate_canary_campaign` replaced with a no-row body. No row must read as Pending — Pass would release a held cohort and Fail would hold a healthy one, both off evidence never read. |

### Two existing tests that proved nothing — the "assertion satisfied by two paths" trap, live

`ManifestReceptor_BulkBackfillDeficit_UnknownOriginTopic_WithholdsTheBackfillAsync` and
`ManifestReceptor_StreamDeficit_MissingRequesterIdentity_WithholdsTheRepairAsync` both read as
targeted tests for exactly the lines in this batch. Both were green. Neither reached the method
it names.

Both omit `RepairMode = IntegrityRepairMode.AutoRepairCapped`, and `StreamIntegrityOptions`
defaults to `ReportOnly`. So the bulk-escalation block (`bulkCandidates.Count > 0 && RepairMode ==
AutoRepairCapped`) never ran and no repair batch was ever built — `_sendBulkBackfillRequestAsync`
and `_sendRepairRequestAsync` were never called at all.

The first one's log assertion is the instructive part. It asserts the message contains
"no origin-carried request address", which is emitted by TWO different `[LoggerMessage]`s — the
repair withhold (EventId 56) and the drill-down withhold (EventId 58). The drill-down fired, the
assertion passed, and the bulk path stayed dark. The replacements assert the EventId-56 shape
including its `0 stream(s)` count, which only the bulk call site can produce, and the second one
asserts the ledger GRANTED the repair (via the published `IntegrityDivergenceDetected`'s
`AutoRepairRequested`) so the send is provably attempted before it is withheld.

The originals are left in place — they still assert something true about the receptor — but they
are not the tests their names claim.

### Declined (18)

**`EFCoreEventStore.cs` 683-688 — `_localInstance()` is dead code, and a duplicate.**
Zero references anywhere in `src/` or `tests/`. It is a character-for-character duplicate of
`_replayInstance()` (same file, line 577), which IS called from `_restoreScopeInHops`. It was
orphaned when the drain-mode copy of the scope-restore path was consolidated into the shared
helper. Not covered here because covering it is impossible: private, no callers, and this repo
does not use reflection. **This should be deleted rather than excluded** — it is 6 lines of a
helper that already exists under another name. Not deleted in this round because the round's
mandate was tests, and a source deletion wants its own review.

**`EFCoreWorkCoordinator.cs` 275 — `CountServiceBacklogAsync`'s no-row guard.**
Unlike its three siblings above, this one calls no function: the statement is
`SELECT (SELECT count(*) …), (SELECT count(*) …), COALESCE(…)` with no top-level `FROM`. A
PostgreSQL `SELECT` without `FROM` returns exactly one row, always. There is no function to
replace and no shape that yields zero rows. Genuinely unreachable; the guard is correct and stays.

**`EFCoreWorkCoordinator.cs` 2924 — brace artifact.**
The `}` closing `if (prior is null) { … return … ; }`. Both arms of the ternary at 2921-2923 are
covered (verified: 2921, 2922 and 2923 all report hits) and the block has no path that falls out
of it. Ninth instance of this artifact in the round.

**`IntegrityManifestReceptors.cs` 835 — dead by enclosing condition.**
`_sendBulkBackfillRequestAsync` re-reads `ITransport`, `IEnvelopeSerializer`, the requester name
and the topic from the same `services` scope and the same `options`, then guards on them. Its only
call site sits after line 691-693 in the type-level comparison, which returns on the *identical*
predicate over the *identical* values. By the time the callee's guard is evaluated it cannot be
true. Confirmed by the new test: 834 and 838-840 all report hits, 835 does not.

**`IntegrityCheckpointReceptor.cs` 215-216 — dead by enclosing condition, and it hides a dead
diagnostic.** Line 214 is `if (measurable && !settled && options.RepairMode == AutoRepairCapped)`.
Line 149 — 65 lines earlier in the same loop body — is `if (measurable && !settled) { …; continue; }`.
So `measurable && !settled` is false for every iteration that reaches 214, and
`LogRepairWithheldConsumerBehind` can never fire. Worth flagging beyond coverage: that log line
exists to tell an operator *why* a confirmed gap shows `autoRepair=false`, and it has been
unreachable since the #667 deferral guard was added above it. Not touched here — removing it or
moving it is a behavior change, not a test change.

**`QueryTranslation/PhysicalFieldExpressionVisitor.cs` 63, 73.**
63 guards `propertyInfo.DeclaringType == null`. `MemberInfo.DeclaringType` is null only for a
global member on a module, which C# cannot declare for a property — a BCL contract guard.
73 guards `dataAccess.Expression == null`, but the enclosing `if` at 56-58 requires
`_isPerspectiveRowType(dataAccess.Expression?.Type)`, and that helper returns false for null
(line 109-111). Dead by enclosing condition.

**`EFCoreDeadLetterStore.cs` 68 — dead by call-site.**
`result switch { null => null, DBNull => null, _ => (Guid)result }` over
`ExecuteScalarAsync` on `SELECT move_to_dead_letters(…)`. `move_to_dead_letters` is
`RETURNS UUID` — a scalar function, so the statement yields exactly one row whatever happens, and
a SQL NULL arrives as `DBNull.Value` (the arm below, which is covered). `ExecuteScalarAsync`
returns `null` only for an empty result set, which this statement shape cannot produce. Reaching
it would need the function redefined as `RETURNS SETOF uuid`, which is not a shape the product
has.

**`VectorSearchExtensions.cs` 732 — dead by call-site.**
`_toSnakeCase`'s empty-input guard. Both call sites (`_buildEfPropertyAccess`,
`_buildEfPropertyAccessForNullCheck`) are handed a `propertyName` that came from
`_getPropertyNameFromSelector`, i.e. a `MemberExpression`'s `Member.Name`. A C# member name is
never the empty string.

**`Collective/CollectiveSettersRewriter.cs` 188 — dead by call-site, verified experimentally.**
`_unwrapLambda`'s `LambdaExpression direct => direct` arm, for a selector argument that is a bare
lambda rather than a `Quote`. I did not trust the reasoning here, so I built the tree by hand —
`Expression.Call(sParam, setProperty, Expression.Lambda<Func<M,int>>(…), …)` — and measured: line
187 (the Quote arm) reports a hit, 188 does not. `Expression.Call` validates each argument through
`ExpressionUtils.TryQuote`, which wraps a `LambdaExpression` in a `Quote` whenever the parameter
type is `Expression<TDelegate>`; `MethodCallExpression.Update` routes back through the same
factory. There is no supported way to put an unquoted lambda in that slot. The probe test was
removed after it answered the question.

**`DbContextNotificationConnectionStringFallback.cs` 59, 89 — race with no seam.**
The inner `if (_resolved) return _cached;` of a double-checked lock (and its `_searchPathResolved`
twin). Reaching it needs a second caller to have read the latch as false at the outer check and
then park on `_gate` *before* the winner sets the latch inside it.

The winner's side is controllable — `IServiceProvider.CreateScope()` / `GetRequiredService` are
called inside the lock and can be a fake that blocks. The loser's side is not: .NET exposes no way
to observe a thread parking on a `Lock`, and every construction that would wait for it deadlocks,
because the only thread that could signal "the loser has arrived" is the one blocked behind the
lock the winner is holding. Polling `Thread.ThreadState` for `WaitSleepJoin` would work most of
the time, which is worse than an uncovered line: it flakes in CI and it is not a signal the code
emits. Declined on the same grounds as the other races in this file.


## CZ. Round-24 Core batch 3: 19 files, and eight previously-declined lines re-checked

`src/Whizbang.Core`, 61 lines across 19 files. Everything in the closed table was verified with a
scoped cobertura and `hits > 0` counted on the exact line numbers.

### A tooling trap that cost an hour: `--coverage-output` silently yields an EMPTY report

```
dotnet run ... -- --coverage --coverage-output-format cobertura --coverage-output name.xml
```

writes a **178-byte** cobertura containing `<packages />`, and the run reports "Passed!". Drop
`--coverage-output` and the identical run writes a full ~11 MB report under a GUID filename.
Nothing warns. Same family as the `[Category=ShardN]` trap in CP, and the same check catches it:
count `hits > 0`, never trust the run's exit status. Collection also fails intermittently under
parallel agent load (a `Coverlet ... UnloadModule` `FileStream` error) and produces the same
178-byte file, so assert a size floor before reading any report.

### A sequence-point fact that explains several "impossible" readings

For `} catch (SomeException) {` the line carries the **try block's `leave`**, not the handler. So a
member whose try body always succeeds reports that line as HIT while the handler's own lines stay
at zero. `IntegrityCheckpointWorker.cs` read 45:1, 46:1, 47:0 — 46 is the `} catch (...) {` line,
hit by the success path, and only 47 (`return;`) reports the handler. Do not read a hit on the
`} catch` line as evidence the catch ran.

### Closed and verified

| File | Lines | How |
|---|---|---|
| `Messaging/CompositeExpansionBudget.cs` | 49 | `MaxChildrenPerExpansion` had no production reader and no test. Asserted against the OBSERVED chunking boundary — `Plan(cap)` within budget, `Plan(cap + 1)` split with `ChunkSize == MaxChildrenPerExpansion` — so the property is pinned to the limit the planner enforces rather than to the constructor argument. |
| `Tags/TagOptions.cs` | 171 | `.OrderBy(r => r.Priority)` in the non-generic `GetHooksFor(Type)`. The existing test enumerated a ONE-element result, and .NET's ordered-enumerable `ToArray` short-circuits at count <= 1 without ever invoking the key selector — so the line stayed dark under a passing test. Two matching hooks registered in reverse priority order, asserted on order, not count. |
| `Signals/SignalTypeRegistry.cs` | 53 | `Clear()`, which had ZERO callers repo-wide. Asserted on all three query surfaces (count, `IsRegistered`, `GetAll`), captures the pre-existing union and re-registers it through the public `Register` seam in a `finally`, and the three suites that read this process-wide static now share a `[NotInParallel("SignalTypeRegistryStatic")]` key so nothing observes the empty window. |
| `Workers/IntegrityCheckpointWorker.cs` | 47 | The schema-gate cancellation return. The existing round-23 test stops via `StopAsync` without ever confirming the worker reached the gate, and measurement showed line 47 never running under it. New test parks in a gate that publishes an "entered" signal, then cancels, and asserts the coordinator saw ZERO checkpoint cycles — the half that proves it RETURNED rather than falling through to SQL against un-migrated tables. |
| `Workers/SubscriptionExpansionWorker.cs` | 134-136 | The missing-infrastructure skip. The existing `MissingTransport_LeavesPendingForNextBootAsync` never reaches it: default `RepairMode` is `ReportOnly`, so the run returns at the backfill gate two branches earlier and its Pending assertion is satisfied by the wrong path. New test enables repair (`AutoRepairCapped`) and asserts on the LOG EVENT (74 present, 73 absent), because "disabled" and "skipped" leave identical registry state. |
| `Workers/TransportBatchCollector.cs` | 64 | `Enqueue`'s disposed guard. A post-dispose enqueue must be dropped: the timers that would flush it are gone, so buffering it swallows a message the broker was told had been taken. Asserted via a second dispose that flushes nothing. |
| `Messaging/BatchWorkCoordinatorStrategy.cs` | 373 | `_resetDebounceTimer`'s disposed guard. Forced, not raced: the injected logger parks inside `QueueOutboxMessage`'s trace log — past the disposed check, past the lock — until `DisposeAsync` has fully returned. Without the guard the call reaches `Change()` on a disposed `Timer` and throws out of a worker completion path during an ordinary stop. |
| `Workers/PerspectiveWorker.cs` | 469-470 | The schema-gate cancellation arm. `StartupScanComplete` is PUBLIC and awaited by the read-model barrier, so the assertion is that the task SETTLES CANCELED — not merely that the worker exited. Left pending, an interrupted migration is a hang, not a shutdown. |
| `Workers/InboxDrainWorker.cs` | 535 | See the overturned section above. |
| `Workers/SlidingWindowInboxBatchStrategy.cs` | 170 | See the overturned section above. |
| `Workers/InboxDispatchWorker.cs` | 244 | See the overturned section above. Eight tests in the class, all green; verified 244 at hits=1. |

### Re-checked and OVERTURNED — three lines a previous round declined

Recorded here because the failure mode of this file is turning "not checked" into "cannot be
done", and each of these had a seam the earlier pass did not look for.

- **`SlidingWindowInboxBatchStrategy:170`** (BO listed it under "Races declined"). The disposed
  guard at the top of `_runIdleSweepAsync` is reachable deterministically, because the strategy
  takes an injectable `TimeProvider` and creates its idle-sweep timer through it. A provider that
  hands back a timer whose `DisposeAsync` **parks** holds `FlushAndStopAsync` at exactly the point
  after `_disposed` is set (line 99) and before the timer is torn down (line 102) — the window a
  real tick lands in — and the test then fires the tick by hand. `IdleEvictionWindow` is set to
  zero so an unguarded sweep WOULD evict, which is what makes the surviving buffer attributable to
  the guard rather than to the eviction window.
- **`InboxDrainWorker:535`** (BI: "a timing window, not a fixture"). It is a fixture. Cancelling
  from the coordinator's `AfterCall` hook — what the existing sibling test does — puts the token
  in a cancelled state *before* the page is written, so `ChannelWriter.WriteAsync` returns a
  cancelled `ValueTask` on the first row and the drain leaves through the exception path instead.
  Cancelling from the **channel writer**, after the last row of a FULL page has been handed over,
  lands the stop on the one boundary where the loop can only exit through its `while` condition.
  `CapturingInboxChannel` gained an `AfterWrite` hook (invoked after the inner write, so it cannot
  turn the awaited write into a cancelled one) to make that possible.
- **`InboxDispatchWorker:244`** was not previously recorded. The arm needs the stopping token to be
  dead by the time `Task.Run(..., stoppingToken)` spawns the partition consumers, so that
  `Task.WhenAll` throws from inside a `finally`. Reaching it took two attempts, and the failed one is
  an independent re-derivation of CT — see the next section. What works is the **gate**: `ISchemaReadyGate`
  promises to block "until MarkReady is called OR the token fires", and an implementation may
  legitimately observe readiness and RETURN rather than throw. A gate that parks, lets the test
  cancel while parked, and then returns normally puts the worker past the barrier with a dead
  token — the real shape of a stop landing between the schema gate and the fan-out.

### An independent confirmation of CT, arrived at from the other direction

The first version of the `InboxDispatchWorker:244` test cancelled the CTS and then called
`StartAsync(cts.Token)`, expecting `ExecuteAsync` to run with a dead token. It does not — and the
failure is **silent in the passing direction**.

Measured with a capturing logger: `ExecuteTask.Status == Canceled`, `IsFaulted == false`, and **not
one log entry emitted**, not even the "started" line that is the first statement of `ExecuteAsync`.
The body never ran. Two of that test's three assertions — "the task completed cleanly" and "nothing
was dispatched" — passed over a worker that had never started; only the assertion on a log line the
worker itself emits caught it.

**CT already documents the cause** (`_executeTask = Task.Run(() => ExecuteAsync(...), token)` in
Hosting.Abstractions 10.x, which skips the delegate entirely when the token is already canceled).
Recording the second sighting because it was reached independently, and because it shows the
diagnostic that settles it: assert on something the worker itself EMITS, not on the shape of its
task. A completed, non-faulted `ExecuteTask` is exactly what a worker that never started looks like.

### Declined — five newly proven "dead by call site", four in `PerspectiveWorker`

Each was traced to its single call site rather than judged by shape.

- **`PerspectiveWorker:604`** — `break` in the `catch (OperationCanceledException)` around
  `await Task.WhenAny(workWait, drainWait, idleTimeout, perspectiveSignal)`. `Task.WhenAny` never
  propagates a constituent's exception to its own awaiter; its task completes successfully the
  moment any constituent completes, in whatever state. The catch guards an exception this await
  cannot raise. Identical to the `DeadLetterRecoveryWorker:227` entry already recorded in BY.
- **`PerspectiveWorker:1289`** — `if (evictedStreams.Count == 0) return;` in the cursor-cache
  eviction handler. The handler is private and wired only to `PerspectiveCursorCache.OnStreamsEvicted`,
  and `PerspectiveCursorCache._raiseEvicted` is itself called from one place, under
  `if (evicted.Count > 0)`. An empty list cannot reach the handler.
- **`PerspectiveWorker:3669`** — `if (batchProcessedEvents.IsEmpty) return;` at the top of
  `_firePostLifecycleDetached`. Its one call site (line 1248) is already inside
  `if (!batchProcessedEvents.IsEmpty)` at line 1231, and nothing removes from that dictionary
  between the check and the background task that reads it.
- **`PerspectiveWorker:3924`** — `if (_streamLocker is null) return;` in `_startLockKeepaliveAsync`.
  The one call site (3337-3338) is the true arm of `lockAcquired ? ... : Task.CompletedTask`, and
  `lockAcquired` can only be true inside `if (_streamLocker is not null)` at 3321.
- **`InboxDrainWorker:654`** — re-confirms BI. `_admitRow`'s fallback `return true;` when the row
  is not found in the fetch it is checked against; both call sites derive the row from that same
  list through non-copying projections.

### Declined — dead by enclosing condition

- **`TypeMatcher:111` and `:137`** — the `string.IsNullOrEmpty` early returns in `_stripVersionInfo`
  and `_stripAssembly`. Both are private with only three call sites between them
  (`Matches(string, string, MatchStrictness)` lines 53/54, 59/60 and `_getSimpleName` line 160).
  `Matches` returns at lines 42-47 for any null-or-empty input, so both helpers only ever see a
  non-empty string; and neither helper can PRODUCE an empty one from a non-empty input —
  `_stripVersionInfo` either returns its argument or `$"{parts[0]}, {parts[1]}"`, which always
  contains ", ". `_getSimpleName`'s own empty guard at 155 IS reachable (via
  `IgnoreAssembly|IgnoreNamespace` over an input like `", Assembly"`), which is what makes the
  other two look reachable at a glance. They are not.
- **`TransportSubscriptionBuilder:108`** — re-confirms BK. `RoutingOptions.InboxStrategy` is
  non-nullable, assigned in the constructor and settable only through a setter that throws on null.

### Declined — a `return` after an infinite delay that can only end by throwing

**`RecentlyProcessedEventCacheSweepWorker:37`.**

```csharp
if (!_options.Enabled) {
  LogDisabled(_logger);
  await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);   // 36
  return;                                                                    // 37
}
```

`Task.Delay(Timeout.Infinite, ct)` has exactly two outcomes: never complete, or throw on
cancellation. There is no path on which control reaches line 37.

Worth flagging beyond the coverage point: every sibling worker wraps the same call
(`IntegrityCheckpointWorker:39`, `InboxDispatchWorker:172`) in
`try { ... } catch (OperationCanceledException) { }` so the disabled worker exits cleanly. This one
does not, so a disabled sweep worker's `ExecuteTask` settles Canceled on every shutdown instead of
RanToCompletion. Benign under the host's default exception behavior, but it is the inconsistency
that makes the dead `return` look alive. **The fix is the try/catch, not a test.**

### Declined — races with no seam, re-confirmed

- **`BatchFlusher:74`** (`break` on `OperationCanceledException` from `_channel.Reader.ReadAsync(ct)`).
  `_stop` is cancelled in exactly two places, both in `DisposeAsync` and both AFTER
  `_channel.Writer.TryComplete()`, so a pending read resolves as `ChannelClosedException` (the very
  next catch). Reaching the cancellation arm needs the cancel to land between the `while` condition
  and the `ReadAsync` call.
- **`BatchFlusher:175-176`** — re-confirms BY's entry for the same catch under its old line number
  (142). `_runAsync` converts every `OperationCanceledException` it can produce into a `break`, and
  the `WaitAsync` it guards is passed `CancellationToken.None`, so `_loop` can only end
  RanToCompletion or Faulted-with-something-else.
- **`BatchWorkCoordinatorStrategy:413`** — the `_disposed` guard in `_debounceTimerCallback`.
  Unreachable for a sharper reason than a race: `DisposeAsync` awaits `_debounceTimer.DisposeAsync()`
  at line 448 and only sets `_disposed = true` at line 480. Timer disposal drains in-flight callbacks
  and prevents new ones, so no callback can ever observe the flag set. (Its sibling at line 373 IS
  reachable and is now covered — see the closed table.)
- **`TransportBatchCollector:116`** — the same guard in `_slideTimerCallback`, and here the ordering
  is the opposite one: `_disposed = true` at line 99 precedes both timer disposals at 101-108, so a
  callback CAN observe it, but only inside that few-microsecond window. No seam; the timers are
  created with `new Timer(...)` directly rather than through a `TimeProvider`. Giving this class the
  `TimeProvider` seam `SlidingWindowInboxBatchStrategy` already has would make it testable the same
  way — that is the owner's call, not a coverage edit.
- **`LifecycleCoordinator:245`** — `return false;` when `_fired` is already set in
  `WhenAllState.TrySignalAndComplete`. `SignalSegmentCompleteAsync` removes the state from
  `_whenAllStates` on the very next statement after a `true` result, so a later call cannot obtain
  the same instance; only two callers that both read the dictionary before the removal can, and
  there is nothing between the `TryGetValue` and the `TrySignalAndComplete` to suspend. The metrics
  counters, which would otherwise be a `MeterListener` seam, are all recorded after the removal.
- **`SlidingWindowInboxBatchStrategy:165` and `:185`** — re-confirm BO. 165 is the outer
  shutdown catch, reachable only if the batcher's enumerator observes `_stopCts` cancellation, which
  happens on the hard-stop path *after* `_stopCts.Dispose()`; the existing stop test releases the
  hung flush and returns without awaiting the worker, so whether it lands is scheduling. 185 needs
  a FAULTED drain worker, and `_drainBufferAsync` absorbs every exception its body can raise —
  the only way to fault it is to inject a logger that throws from `_logFlushFailed`, which tests the
  fake rather than the code.
- **`PerspectiveWorker:275`** (SemaphoreFullException coalesce) and **`:1462`** (CAS-loss return in
  the affinity-gate sweep) are both genuine multi-writer races. 275 has a possible seam — the
  notification listener is injectable, and the worker does not consume `_perspectiveWake` during its
  startup phase — but it needs the startup work held open to be deterministic.

### Tractable — available work, NOT residue

Listed so a later round does not re-derive them.

- **`PerspectiveWorker:3825-3827`, `:3857`, `:3862-3866`** — the `catch (Exception)` arms of the two
  detached-stage helpers. Both need only a fake `IReceptorInvoker` that throws, and both are
  observable: `_fireDetachedStageAsync` adds its task to `_detachedTasks` (there is a
  wait-for-detached-tasks seam), and both log through `LogDetachedStageError`. The obstacle is
  reaching them — 3246 is on the drain path — not the arm itself.
- **`PerspectiveWorker:3984-3986`** — the log-and-rethrow around
  `GetEventsBetweenPolymorphicAsync`. A throwing `IEventStore` is an ordinary fake.
- **`PerspectiveWorker:1604`, `:1606`** — the reactive-orphan-disposal catch. Needs
  `ReapExhaustedOrphanedPerspectiveRowsAsync` to throw on a fake coordinator, plus a drain-mode
  fixture whose `GetStreamEventsAsync` returns nothing and `MaxPerspectiveEventAttempts` set.
- **`PerspectiveWorker:4518-4519`** — the claim-window `TryAdd` collision. It does NOT need
  concurrency: two entries with the same `WorkId` in ONE `WorkBatch.PerspectiveWork` list take the
  branch on the second item, single-threaded. `PerspectiveWorkerDedupTests`'s coordinator already
  lets a test supply the batch contents; the open question is only whether the harness's
  enqueue-per-item pump keeps both items in the same claim cycle.
- **`PerspectiveWorker:1878`, `:1923`** — drain-mode refetch guards; ordinary fixtures, deep path.
- **`PerspectiveWorker:2849-2850`** — re-confirms AT: not reachable through
  `PerspectiveWorkerTestHarness`, which never reaches `_resolveDependenciesAndLoadEventsAsync`.
  Note the existing `Worker_RegistryNotRegistered_SkipsPerspectiveAndContinuesAsync` is named for
  this branch and asserts `ConsecutiveEmptyPolls >= 0`, which is true either way.

### Already recorded, re-confirmed without change

- **`MessageExtractor:72, 73, 77, 78`** — CB, dead by C# generic covariance. Re-read: `IEvent : IMessage`
  and `ICommand : IMessage`, `IEnumerable<out T>` is covariant, and the `IEnumerable<IMessage>` test
  on line 66 is checked first, so it always wins. Unchanged.
- **`MessageTagProcessor:86` and `:113`** — logger-null by construction (recorded in BO). Both debug
  blocks require `_scopeFactory is null`, and the lazy `Logger` property resolves to
  `NullLogger.Instance` in exactly that case, so `IsEnabled(Debug)` is pinned false there.

### Already covered — two batch lines that were already green locally

- **`TableStatisticsCollector:45`** is covered by the round-23
  `TableStatisticsCollectorCoverageTests.ExecuteAsync_SchemaGateCancelledBeforeReady_...` test
  (measured 41:1 42:1 43:1 44:1 45:1).
- **`LifecycleCoordinator:65`** — the closing brace of `ExpectCompletionsFrom` — is covered by the
  existing `LifecycleCoordinatorTests` (measured 60:1 through 65:1 across 52 tests).

No test was written for either. **Measure a worklist entry against a local scoped run before
writing anything for it**: the merged CI cobertura can list a line that is already green, and two of
this batch's sixty-one were. The reverse also happens — the same merge listed
`IntegrityCheckpointWorker:47` and `SubscriptionExpansionWorker:134-136` as uncovered, which was
correct, and each had a *named, passing test* that did not reach them.
