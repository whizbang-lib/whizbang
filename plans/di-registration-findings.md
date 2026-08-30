# DI Registration Findings Register

Tracks every dependency discovered to be unregistered or unwired while implementing
[DI Registration Integrity](di-registration-integrity.md). Each entry is a service that may be
silently absent in an already-deployed system, so this list exists to drive impact assessment, not
just cleanup.

**Status of this list: candidates requiring per-item confirmation.** Two earlier counts in this
investigation were wrong because the search pattern was wrong (a first pass reported 25 unregistered
interfaces; the real figure after allowing namespace-qualified registrations is 12). Nothing here
should be acted on in a deployed environment until the specific item is confirmed against the
composition that environment actually builds.

## How these were found

`tests/Whizbang.Core.Tests/DependencyInjection/CompositionSatisfiabilityTests.cs` reflects over the
framework assembly and reports constructor parameters that are both **optional** and
**interface-typed**. That combination is the silent-null surface: when the type is hand-constructed
at a registration site, the container supplies nothing and the compiler supplies null, so the gap
never raises an error.

Reflection is used deliberately and only in the test assembly. The shipped validator
(`RegistrationValidation`) is reflection-free and reads a generated manifest; this audit reaches the
same conclusion by the opposite route and gives the generator a target to reproduce.

### A false negative worth recording

The first version of this audit inspected only `ServiceDescriptor.ImplementationType` and reported
zero findings. That was not a clean result: factory-lambda registrations carry a null
`ImplementationType`, so the audit skipped every hand-constructed service, which is precisely the
population the defect lives in. A guard that cannot see the failure it was built for reports success
in exactly the same way as a guard that found nothing wrong.

## Surface

| Measure | Count |
|---|---|
| Optional injected constructor parameters (occurrences) | 162 |
| Distinct parameter names | 161 |
| Types declaring at least one | 72 |
| Distinct interfaces involved | 58 |
| Of those, `ILogger` parameters | 36 |
| Non-logger service parameters | 125 |

## Confirmed defects

### F-001: audit decorator built with three of six arguments

**Status:** fixed, PR #614. **Impact: shipped.**

Both DI registration sites constructed `AuditingEventStoreDecorator` by hand and passed three of its
six constructor arguments, so `ILogger` and `IServiceInstanceProvider` were null in every composed
application. The instance provider had been added earlier specifically so audit records could name
the instance that wrote them; that never took effect outside unit tests, which supplied the argument
themselves and therefore could not observe its absence.

**Deployment impact to assess:** audit records written by any deployed version after the instance
provider was introduced carry no writer identity. They are not wrong, but they cannot be attributed
to an instance, so any forensic question of the form "which replica wrote this" is unanswerable for
that period.

## Candidates: registered only by generated driver code

Registered solely by code emitted from a data-driver generator, not by the framework's own `Add*`
extensions. A composition that does not include that driver, or in which the generator did not run,
lacks them with no error at registration time. This is the shape of the recurring multi-assembly
failure, so both warrant confirmation.

| Interface | Registered by | Risk |
|---|---|---|
| `ILibraryVersionProvider` | driver registration generator (`TryAddSingleton`) | absent without that driver |
| `IWorkChannelWriter` | driver snippet template (`AddSingleton`, guarded by an `Any` check) | absent without that driver |

## Candidates: no registration found anywhere

No registration in `src/`, generated or otherwise. Each needs classification before it counts as a
defect. Expected outcomes are a mix of genuine gaps, consumer-supplied extension points that are
optional by design, and types never resolved from a container at all.

| Interface | Implementations in `src/` | Note |
|---|---|---|
| `ICallerInfo` | none found | may be a data/context type, not a service |
| `IChaosInjector` | none found | likely a test-only injection point |
| `ICommandInboxAddressResolver` | none found | consumer-supplied routing override? |
| `IConcurrencyGovernor` | 3+ (`FixedWidthGovernor`, `AdaptiveConcurrencyGovernor`, `ObservedConcurrencyGovernor`) | several implementations, none registered; highest-priority item here |
| `IDestructionHook` | none found | consumer extension point? |
| `IEnvelopeRegistry` | 1 | confirm whether it is resolved or constructed directly |
| `IEventNamespaceRegistry` | none found | generator-backed registry? |
| `IInstanceAliveLockSource` | none found | confirm |
| `IPerspectiveCompletionStrategy` | none found | confirm |
| `IProcessedEventCacheObserver` | 1 | observer, plausibly optional by design |

`IConcurrencyGovernor` is the one to look at first: three implementations exist, so something was
meant to select among them, and nothing registers any of them.

## Next actions

1. Classify each candidate: genuine gap, extension point optional by design, or not a DI service.
2. For each genuine gap, determine whether a deployed version ran without it and what behavior was
   consequently absent.
3. Feed confirmed gaps into the migration phase: required constructor parameter plus a `TryAdd`
   default, so the service can no longer be silently missing.
4. Replace this reflection-based audit with the generated manifest once the generator lands, keeping
   the audit as a cross-check that the manifest is complete.
