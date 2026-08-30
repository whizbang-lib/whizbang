# DI Registration Integrity

## Context

Dependency-injection defects in this framework fail silently, and they recur. Three distinct
failure modes have each shipped more than once:

1. **Optional parameter never supplied.** A service is constructed by hand inside a DI factory
   lambda. A parameter added later is not added to that call, so it defaults to `null`. The code
   compiles, the app runs, and the feature is simply absent. Most recent instance: an event-store
   decorator gained an instance provider and a decision hook; both registration sites still passed
   three of six arguments, so audit records named their emitting instance in unit tests and never
   in a composed application.
2. **Registration behind a condition that is not live.** The registration exists in source, so
   nothing static can flag it, but the branch that runs it does not execute in a given composition.
3. **Registration call never runs at all.** Seen when a consumer's composition strips or replaces
   an assembly. Nothing is wrong with the source; the worker simply never starts.

The common shape is that **absence is invisible**. Nothing throws, nothing logs, and the missing
behavior is indistinguishable from behavior that was never requested.

Every guard considered so far that requires a developer to *remember* to arm it (a marker
attribute, a registration list, a checklist) reproduces the same defect one level up: a future
contributor who does not know the guard exists silently opts out of it. The guard must therefore
key off something a developer cannot omit, and the only such thing is **the constructor parameter
itself**: you cannot take a dependency without declaring it.

### Why the hand-written factory lambdas exist

They are not sloppiness. `Whizbang.Core` targets zero reflection, and `ServiceDescriptor`
registrations that supply an implementation *type* are activated reflectively by the container.
Factory lambdas avoid that. The drift risk is therefore inherent to the AOT discipline, and the fix
must be to **generate the factory lambdas**, not to remove them.

### Existing reflection leaks found while scoping this

The zero-reflection rule already has holes, all on the decoration path, and all in the code this
plan touches:

| Location | Leak |
|---|---|
| `src/Whizbang.Core/ServiceCollectionExtensions.cs:523` | `ActivatorUtilities.CreateInstance` |
| `src/Whizbang.Core/ServiceCollectionExtensions.cs:570` | `ActivatorUtilities.CreateInstance` |
| `src/Whizbang.Core/SystemEvents/SystemEventServiceCollectionExtensions.cs:85` | `ActivatorUtilities.CreateInstance` |
| `src/Whizbang.Core/SystemEvents/SystemEventServiceCollectionExtensions.cs:195` | `ActivatorUtilities.CreateInstance` |

Additionally, `Whizbang.Core` has ~55 type-based registrations (`AddSingleton<IFoo, Foo>()`), which
the container also activates reflectively. Closing these is in scope; see Phase 4.

## Requirements

1. **Zero reflection.** No `Activator`, no `ActivatorUtilities`, no `Type.GetConstructors`, no
   `MakeGenericMethod`, at build time or run time. Validation must read data, not shapes.
2. **Turnkey by default, overridable by choice.** Every injectable service ships a working default
   so an application works out of the box; a developer who registers their own implementation must
   win without any extra ceremony.
3. **Nothing to remember.** No opt-in attribute, no manually maintained list of services. The
   enforcement derives its inputs from code that already has to exist.
4. **Loud on absence.** A missing registration fails at build or at composition, naming the service
   and the type that needed it. Never a null that surfaces later as missing behavior.
5. **Fully documented and cross-linked.** Every injectable service has a documentation page, is
   listed on a single index page, and carries `<docs>` and `<tests>` links in source.
6. **Exhaustively tested**, including tests that would have caught each of the three historical
   failure modes.

## Design

### Part A: separate "overridable" from "optional"

The root cause of failure mode 1 is that one construct expresses two different intentions:

```csharp
// today: "the developer may swap this out" and "this may be absent entirely" are the same thing
public AuditingEventStoreDecorator(..., IAuditDecisionHook? hook = null)
```

Split them. Overridability belongs to the registration; the constructor stops being optional:

```csharp
// constructor: required. Omitting it at a construction site is now a compile error.
public AuditingEventStoreDecorator(..., IAuditDecisionHook hook)

// registration: a default always exists, and a developer's own registration wins.
services.TryAddSingleton<IAuditDecisionHook, NoOpAuditDecisionHook>();
```

`TryAdd` gives exactly the turnkey-plus-override behavior in requirement 2: register nothing and get
the shipped default; register your own first and `TryAdd` no-ops. The silent-null failure mode stops
existing because the container guarantees something is always present, which in turn lets every
resolution use `GetRequiredService` instead of `GetService`.

Consequence: after this pass there are **no optional injected parameters left**, so the validator
needs no exception list and no opt-out marker for future contributors to maintain.

### Part B: generated requirements manifest

Discovery happens at compile time in the generator, which already has the full compilation. It emits
the dependency graph as plain data:

```csharp
// generated
internal static class WhizbangServiceRequirements {
  public static readonly ServiceRequirement[] All = [
    new(typeof(AuditingEventStoreDecorator), [
      typeof(IEventStore), typeof(IDeferredOutboxChannel), typeof(IOptions<SystemEventOptions>),
      typeof(ILogger<AuditingEventStoreDecorator>), typeof(IServiceInstanceProvider),
      typeof(IAuditDecisionHook)]),
    // ...
  ];
}
```

Inputs are derived, not declared: every implementation type reachable from a Whizbang `Add*`
extension, plus each of that type's constructor parameters. A contributor who adds a seventh
dependency appears in the manifest on the next build without touching anything.

### Part C: reflection-free validation

Validation scans `IServiceCollection` **before** `BuildServiceProvider`. It compares `Type` handles
against `ServiceDescriptor.ServiceType`; it never activates anything:

```csharp
public static IServiceCollection ValidateWhizbangRegistrations(this IServiceCollection services) {
  List<(Type Needed By, Type Missing)>? missing = null;
  foreach (var req in WhizbangServiceRequirements.All) {
    foreach (var dep in req.Dependencies) {
      var satisfied = false;
      for (var i = 0; i < services.Count; i++) {
        if (services[i].ServiceType == dep) { satisfied = true; break; }
      }
      if (!satisfied) { (missing ??= []).Add((req.ImplementationType, dep)); }
    }
  }
  if (missing is not null) { throw new WhizbangRegistrationException(missing); }
  return services;
}
```

This is `Type` equality over a list. No reflection, no instantiation, no side effects, and it fails
before any service is constructed. It catches failure modes 2 and 3, which no static analysis can
see, because it inspects the graph that was actually composed.

Called automatically at the end of `AddWhizbang()`, with an escape hatch
(`options.ValidateRegistrations = false`) for consumers doing partial composition in tests.

### Part D: analyzers

Compile-time cover for failure mode 1, scoped to the framework's own `Add*` closure:

| Id | Rule | Severity |
|---|---|---|
| `WHIZ500` | A service constructed inside a DI factory omits an injectable parameter | Warning (shipped) |
| `WHIZ501` | A constructor declares an optional injected (interface-typed) parameter | Info (shipped) |
| ~~`WHIZ502`~~ | A dependency no registration satisfies | **Not built, deliberately** |

`WHIZ501` is informational on purpose. The existing surface is around 150 parameters, and a rule
that turns an established codebase red on first build gets suppressed globally, after which it
catches nothing. Growth is held by the ratchet test; the rule exists to put the reason in front of
whoever is editing the constructor.

### Why `WHIZ502` is not worth building

A static "nothing registers this" rule cannot see what the runtime validator sees, and would be
wrong in the one direction that matters. Storage and transport drivers register their services after
`AddWhizbang` returns, on the builder chain, and often from a different assembly. An analyzer
examining one compilation would report every driver-supplied service as unregistered.

That is the same failure that forced validation to move from the end of `AddWhizbang` to startup:
a guard that fires on correct compositions gets switched off, and takes the real failures with it.
The startup validator already performs this check against the graph that was actually composed,
which is the only place the answer exists. Building a static approximation would add false positives
and no coverage.

`WHIZ2003` is syntactic, so it has no annotation surface and cannot be silently un-armed.

## The conversion pattern

Every injected constructor parameter becomes **required**, and every default moves to a
**registration**. One rule, no optional injected parameters left, so a hand-construction that misses
one cannot compile and the validator needs no exception list.

Which registration to write is decided by one question:

> **Is there a behavior that is correct when this capability is absent?**

### Pattern A: inert default, when absence has a correct behavior

```csharp
// Nothing to do when no telemetry identity is configured. Reporting "unknown" is honest.
public sealed class NullServiceInstanceProvider : IServiceInstanceProvider { /* inert */ }

services.TryAddSingleton<IServiceInstanceProvider, NullServiceInstanceProvider>();

public CoalesceShipWorker(..., IServiceInstanceProvider instanceProvider)   // required
```

Turnkey and overridable: a developer registering their own before `AddWhizbang()` wins, because
`TryAdd` no-ops.

Fits: `IServiceInstanceProvider`, `IChaosInjector`, `IProcessedEventCacheObserver`, `ILogger<T>`
(via `AddLogging()`, whose `NullLogger` fallback is already the framework convention).

### Pattern B: required with no default, when absence has no correct behavior

```csharp
// A no-op gate would report "ready" and let work begin against a schema that is not there.
public PerspectiveMigrationWorker(..., ISchemaReadyGate schemaReadyGate)   // required, no default
```

**Do not write a Null-object for these.** A `NullSchemaReadyGate` that answers "ready" converts a
silent null into a silent *false assertion*, which is strictly worse: null at least fails somewhere
eventually, whereas a fake that satisfies the invariant lets work proceed on a premise nobody
checked. The whole purpose of this work is to make absence loud, and a permissive stub makes it
quieter than it is today.

Composition fails at `AddWhizbang()` naming the service, which is the correct outcome.

Fits: `ISchemaReadyGate`, `IWorkChannelWriter`, `ILifecycleMessageDeserializer`, the channel writers
and drain channels, `IReceptorRegistryQuery`.

### Pattern C: computed default, when the default depends on configuration

The 32 parameters that already resolve a default inline (`governor ?? CreateDefaultGovernor(...)`,
`ResolveGovernor(governor, options)`) keep their logic, but it moves into the registration so the
parameter can become required:

```csharp
services.TryAddSingleton<IConcurrencyGovernor>(sp =>
    CreateDefaultGovernor(sp.GetRequiredService<IOptions<PerspectiveWorkerOptions>>().Value));
```

**Open complication:** some of these need a *different* default per consumer. The perspective worker
and the outbox drain both take an `IConcurrencyGovernor` and want different widths, which a single
registration cannot express. Keyed services solve it; so does keeping a per-worker factory. This
needs a decision before Pattern C is applied to that interface, and it is the reason Pattern C is
listed last rather than treated as the general case.

### Staging risk

Pattern B changes latent absence into a hard composition failure. Where a dependency is null in a
deployed system today and nothing has obviously broken, making it required will stop that
composition from starting. That is the point, but it means Pattern B conversions land with a
migration note and want their own release, not a quiet inclusion in a batch.

## Migration

**One pass, converting all optional injected parameters** (~141 candidates in `Whizbang.Core`,
to be triaged down to genuinely injected interface-typed parameters; `CancellationToken`, timeouts,
and value-typed options are out of scope).

Per service: make the parameter required, add a Null-object default implementation where no sensible
real default exists, `TryAdd` it in the owning `Add*` extension, update every construction site.

**This changes public constructor signatures**, so consumers constructing these types directly will
break. Pre-1.0, so acceptable, but it is a real break and belongs in release notes rather than being
described as an internal refactor.

## `ISchemaReadyGate`: why Pattern B needs its own pass

Attempted and reverted. Recording the shape so the next attempt does not rediscover it.

**The defect is real and the codebase names it.** Every site carries a comment reading, in
substance, *"Optional only so existing fixtures construct unchanged; DI always supplies it."* The
optionality exists to avoid updating tests, and it is exactly what allows a worker to be constructed
with no gate and begin work without waiting for the schema. Ten types are affected, each guarding
the wait behind `if (_schemaReadyGate is not null)`.

**Why it is harder than the Pattern A conversions.** The gate parameter sits after other optional
parameters, so making it required forces a position change in the signature. Call sites then break
in a way that appending an argument cannot fix:

```csharp
new PerspectiveWorker(
  instanceProvider, scopeFactory, options,
  tracingOptions: null,      // named
  strategy,                  // positional, and now bound to a different parameter
  ...)
```

Calls mix named and positional arguments, so the new argument has to be inserted **positionally at
a per-constructor index**, not appended. Three mechanical passes failed on this: appending produces
CS8323, and a generic reorder produces malformed parameter lists.

**Scale:** 528 call sites across 7 test projects.

**Additional risk unique to this one:** tests that previously passed no gate skipped the wait. Once
a real gate is required they will actually wait, and any fixture that never marks it ready will
hang rather than fail. Supplying an already-open gate is therefore part of the conversion, not an
afterthought. `SchemaReadyGate.AlreadyReady()` is the right shape for that and is legitimate
production API: a host with no schema step to wait on needs a way to say "nothing to wait for" that
is distinguishable from forgetting to supply a gate.

**What the next attempt should do:** derive each constructor's new parameter index, rewrite call
sites positionally per constructor rather than with one generic rule, and convert one type at a
time with its tests green before moving to the next.

## Documentation

Mirrors the existing `operations/configuration/` structure, including its frontmatter contract
(`codeReferences`, `testReferences`, `verifiedAgainstCommit`, `lastMaintainedCommit`).

New folder: `src/assets/docs/v1.0.0/operations/dependency-injection/`

| Page | Purpose |
|---|---|
| `_folder.md` | Folder metadata, ordering |
| `injectable-services.md` | **Index of every injectable service**: interface, shipped default, lifetime, whether overriding is common, link to the per-service page |
| `overriding-defaults.md` | The `TryAdd` contract: how to supply your own implementation, ordering rules, common mistakes |
| `registration-validation.md` | What `ValidateWhizbangRegistrations` checks, how to read its exception, how to disable it for partial composition |
| `<service>.md` (one per injectable service) | Responsibility, the shipped default's behavior, when to replace it, a worked example |

**Status: written** (in the docs repo, uncommitted). `_folder.md`, `injectable-services.md`,
`overriding-defaults.md`, `registration-validation.md`, plus `diagnostics/whiz500.md` and
`diagnostics/whiz501.md`. Cross-links added from `configuration/all-services.md`, and
`ValidateRegistrations` documented on `configuration/whizbang-options.md`.

Per-service pages are deliberately not written yet: with four services carrying a shipped default,
the index table says everything a page would, and a page per service would be padding that then has
to be maintained. Worth adding when a service needs a worked example longer than a table row.

All four `<docs>` paths were verified to resolve by regenerating the map against this worktree. The
map itself was then restored to the main checkout's state, because the docs repo is on an unrelated
branch with its own pending changes; it needs regenerating once this branch lands.

Cross-link from `operations/configuration/all-services.md` and `service-registration-options.md`,
which currently cover registration but not injectability.

## Testing

The point of this work is that absence is invisible, so the tests must assert on absence. Categories:

**Generator (`Whizbang.Generators.Tests`)**
- Manifest emitted for: no-dependency ctor, all-required ctor, generic parameters, open generics,
  multiple constructors, inherited constructors, records, nested and struct types
- Types not reachable from an `Add*` extension are excluded
- Manifest is stable across incremental builds; no duplicate entries

**Validation (`Whizbang.Core.Tests`)**
- Single missing dependency; multiple missing; exception names both the missing service and the
  type that needed it
- All present passes; TryAdd default present passes; developer override present passes
- Open generics, keyed services, and multiply-registered service types
- Validation runs before any activation: a registered factory that throws is never invoked
- Disabling validation is honored

**Turnkey defaults**
- Every shipped default resolves from a bare `AddWhizbang()`
- Every shipped Null-object default is inert (no side effects, no throw)
- Every default is overridable: a prior registration wins and `TryAdd` no-ops
- Registration order independence

**Composition sweep**
- For each `Add*` extension: build the collection and assert the graph is satisfiable. This is the
  test that generalizes the audit-decorator regression to every service.

**Analyzers (`Whizbang.Generators.Tests`)**
- Each of `WHIZ2001`/`WHIZ2002`/`WHIZ2003` fires on the violating shape and stays quiet on the
  compliant one, including near-miss cases

**Regression, one per historical failure**
- Optional parameter dropped at a factory-lambda registration site
  (already written: `tests/Whizbang.Core.Tests/SystemEvents/AuditDependencyInjectionWiringTests.cs`)
- Registration behind a condition that is not live
- Registration call that never runs, simulating an assembly-composition strip

**AOT**
- No new reflection: assert the four `ActivatorUtilities` call sites are gone and none reappear

## Code / test / docs linking

- `<docs>` tag (versionless path) on every new public type: the validator, the exception, each
  injectable interface, each shipped default
- `<tests>` tag on each, pointing at its test file
- Regenerate both mappings after the pass:
  `node src/scripts/generate-code-docs-map.mjs` and `generate-code-tests-map.mjs`
- Validate with `validate-doc-links` and `validate-test-links`; confirm coverage with
  `get-coverage-stats`

## Phases

1. **Foundation.** `ServiceRequirement`, `WhizbangRegistrationException`,
   `ValidateWhizbangRegistrations`, hand-written manifest for one service. Full test suite for the
   validator. Proves the contract before any mass edit.
2. **Generator.** Emit the manifest; delete the hand-written one. Generator tests.
3. **Migration.** Convert optional injected parameters to required plus `TryAdd` defaults, service
   by service, each with its turnkey and override tests. Composition sweep goes green as it lands.
4. **Reflection removal.** Replace the four `ActivatorUtilities` decoration sites and the type-based
   registrations with generated factories.
5. **Analyzers.** `WHIZ2001`–`WHIZ2003`, warning first, then error once Phase 3 is clean.
6. **Documentation.** The new folder, the index, per-service pages, cross-links, mapping
   regeneration, link validation.

Phases 1 and 2 are independently useful: they catch failure modes 2 and 3 before any signature
changes.

## Open questions

- Should validation be on by default in `AddWhizbang()`, or opt-in for the first release and
  defaulted on afterward?
- Do partial-composition test fixtures need a documented supported path, or is the escape hatch
  sufficient?
- For services with no sensible inert default (if any survive triage), is failing composition the
  right behavior, or should they stay genuinely optional with an explicit marker?
