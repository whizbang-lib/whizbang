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

### Triage by backing-field nullability

Optionality alone does not mean a dependency is ever absent. Two patterns look identical in a
constructor signature and differ completely in risk:

```csharp
// SAFE: optional, but a real default is constructed when none is supplied
governor ?? CreateDefaultGovernor(options.Value)
ResolveGovernor(governor, _options)

// SILENT NULL: optional, stored nullable, and simply absent when nobody passes it
private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;
```

The discriminator is whether the backing field is nullable. A non-nullable field proves a fallback
exists; a nullable one means the dependency can genuinely be absent at run time.

| Classification | Count |
|---|---|
| Nullable backing field, can be silently null | 118 |
| Non-nullable backing field, a fallback exists | 32 |
| No matching field found, needs manual review | 11 |

This classification is a heuristic over source text and matches both known ground truths: it flags
`AuditingEventStoreDecorator.instanceProvider`, the confirmed defect, and clears
`OutboxDrainWorker.governor`, which resolves its default through a named helper. It is triage, not
proof. Confirm any individual item before acting on it.

### Counting corrections made during this investigation

Three figures in this work were wrong before they were right, all from search patterns that could
not see what they were looking for. Recorded because the pattern matters more than the numbers:

| Reported | Actual | Cause |
|---|---|---|
| 0 unsatisfied dependencies | 162 | audit read only `ImplementationType`, which is null for factory-lambda registrations |
| 25 unregistered interfaces | 12 | pattern missed namespace-qualified registrations (`TryAddSingleton<Messaging.IFoo, …>`) |
| 125 silently-null parameters | 118 | `??` heuristic missed named resolver helpers such as `ResolveGovernor(...)` |

Each first number was reassuring and wrong. A zero from a check that could not look is not a clean
result.

## Clusters worth investigating as a group

The same dependency is optional-and-nullable at many sites. The confirmed defect is one instance of
the first cluster, which is the reason to treat the others as candidates rather than noise.

| Dependency | Nullable sites | Consequence if absent |
|---|---|---|
| `IServiceInstanceProvider` | 9 | records and telemetry cannot name the instance that produced them |
| `ISchemaReadyGate` | 8 | work may begin before schema readiness is established |
| `ILifecycleMessageDeserializer` | 7 | lifecycle messages silently not deserialized |
| `IWorkChannelWriter` | 5 | work not handed to the channel |
| `IReceptorRegistryQuery` | 5 | receptor lookups fall back or find nothing |

### `IServiceInstanceProvider`, all nine sites

`AuditingEventStoreDecorator` (confirmed, fixed), `CoalesceShipWorker`, `InstanceStateRunControl`,
`OutboxPublishWorker`, `RedeliveryPump`, `SignalBusHostedService`, `StandbyWatcher`,
`SystemEventEmitter`, `TransportManager`.

One of these nine was verified null in every composed application. The same question is open for the
other eight, and each should be checked against the registration sites that construct it.

## Next actions

1. Classify each candidate: genuine gap, extension point optional by design, or not a DI service.
2. For each genuine gap, determine whether a deployed version ran without it and what behavior was
   consequently absent.
3. Feed confirmed gaps into the migration phase: required constructor parameter plus a `TryAdd`
   default, so the service can no longer be silently missing.
4. Replace this reflection-based audit with the generated manifest once the generator lands, keeping
   the audit as a cross-check that the manifest is complete.
