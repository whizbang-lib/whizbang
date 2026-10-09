---
name: testing
description: >-
  Which Whizbang test project a test belongs in, and how to write it so it is deterministic. Use
  whenever writing, moving, reviewing or fixing a test, adding a test project, choosing between
  Unit, Component and Integration, or deciding what to do with a test that sleeps, reads the real
  clock, starts a worker, touches the file system or starts a process.
---

# Testing Whizbang

Every test lives in a project whose `<WhizbangTestType>` matches what the test does. The type decides
which runs select it, so a test in the wrong project either slows the fast suite or, worse, runs
nowhere. `docs/TEST-PROJECTS.md` holds the per-type rules and the CI wiring; `ai-docs/testing-tunit.md`
holds the TUnit, Rocks and assertion patterns. This skill is the classification: read it before you
put a test anywhere.

## The standard behind the types

The types follow two widely used definitions, which agree with each other:

- **Michael Feathers' unit-test rules (2005).** A test is not a unit test if it talks to a database,
  talks across the network, touches the file system, can't run at the same time as other tests, or
  needs special environment setup.
- **Google's test sizes** (*Software Engineering at Google*, chapter 11). *Small*: one process, one
  thread, no sleeping, no I/O. *Medium*: one machine; threads, processes, sleeping, the file system and
  localhost allowed. *Large*: anything.

Unit is Feathers' unit test and Google's small test. Component and Integration split the medium and
large space at the process boundary.

## Classify by the first rule that matches

| # | The test... | Type | Project |
|---|-------------|------|---------|
| 1 | needs a container, a database, a broker, the network, or real cloud infrastructure | **Integration**, tagged with its suite (`Postgres`, `RabbitMQ`, `AzureServiceBus`, `AzureBlob`, `InMemory`, `Integration`), plus `Docker` when it starts containers | `<Source>.Integration.Tests` |
| 2 | starts a child process (`Process.Start`, a CLI run as a process, `dotnet` invoked as a tool) | **Integration**: it crosses a process boundary | `<Source>.Integration.Tests` |
| 3 | starts a hosted worker (`StartAsync`), runs `Task.Run` or a `Thread`, or otherwise depends on more than one flow of control | **Component** | `<Source>.Component.Tests` |
| 4 | reads or writes the real file system (temp files and directories included) | **Component**: in-process, real resources, no containers | `<Source>.Component.Tests` |
| 5 | sleeps or reads the real clock (`Task.Delay`, `Thread.Sleep`, `TimeProvider.System`, `DateTime.UtcNow`, `Stopwatch`) but is otherwise one flow | **Stays Unit, refactored**: inject `TimeProvider` and drive it with `FakeTimeProvider` | `<Source>.Tests` |
| 6 | none of the above: one deterministic flow, fake clock, no I/O | **Unit** | `<Source>.Tests` |

Rule 5 is a fix, not a move. A test that waits on real time is a race, and moving it to Component
keeps the race. Inject the clock (add the constructor parameter or option to the library if it isn't
there, as a public API) and advance it in the test. Only a class that genuinely cannot take a fake
clock moves to Component, and its pull request says why.

`Benchmark` and `Soak` are measurements, never part of the gate: BenchmarkDotNet projects and
`Whizbang.Soak.Tests` (`scripts/Run-Soak.ps1`).

## Determinism, in every type

- **Never** `Task.Delay`, `Thread.Sleep`, a polling loop, or a timeout used as the wait. A test that
  depends on timing is flaky, and flaky means a race. Raising a timeout hides it.
- In Component tests the test fixes every interleaving through signals (`TaskCompletionSource`,
  `SemaphoreSlim(0, 1)`, library events). Each asynchronous assertion waits on the signal for **the
  exact transition it asserts**, not on something that merely correlates with it: `StopAsync`
  returning or a completion report does not prove a gate was released; only a gate-released signal
  does.
- When the library has no signal for a transition, add one as a **public** event or callback with
  `<docs>` tags. It is a feature for monitoring as much as for tests, never a test-only internal.
- A failure in the merge queue that the PR run passed is the race announcing itself. Find it in the
  change; never re-run past it.

## Moving a test

1. Classify it with the table above. Move it unchanged: same namespace, same test names.
2. Move it with `scripts/Move-TestReference.ps1` (a CSV map of old to new locations, `-DocsSiteRoot`
   for the docs site). It relinks every `<tests>` tag in the library and every doc reference that
   names the test. Run it with `-DryRun` first and read the plan.
3. Prove nothing was lost or duplicated: the sorted executed test names of the source and destination
   projects after the move equal the source project's names before it.
4. A new project declares `<WhizbangTestType>` and `<WhizbangTestTags>`, goes in `Whizbang.slnx`, and
   gets an `InternalsVisibleTo` in the source project when the tests need internals.
5. A new **type** must be taught to every reader of `<WhizbangTestType>` in the same pull request;
   `docs/TEST-PROJECTS.md` "Adding a test type" lists all six places. Miss one and its projects build
   green and never run.

## Guards that enforce this

- **Build:** a test project with an unknown type, or none, fails to build (WHZ0002, WHZ0003).
- **Purity guard:** `.github/scripts/Get-TestPurity.ps1` finds unit-project classes that use a
  construct that makes a test non-unit. `-Inventory` rewrites `plans/test-separation-inventory.{md,csv}`.
- **Link guard:** every `<tests>` tag resolves to a real test.
- **Runner:** `Run-Tests.ps1 -NoBuild` fails when a selected project wasn't built, so a CI slice that
  misses a project fails instead of skipping it.
