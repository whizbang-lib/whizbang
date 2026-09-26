> **Archived 2026-09-26.** The work this plan describes has shipped, or its open remainder is carried by a card on the Release 1.0 or 2.0 Planning board; the Done cards carry the Shipped dates. Status lines below are as last written and may be stale.

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

## Pattern A conversion: `IServiceInstanceProvider` (complete)

All ten sites converted to a required, non-nullable dependency. Baseline dropped 162 to 152.

**What the conversion surfaced, which is the point of doing it:**

1. **Registrations that were not self-contained.** Four extensions registered a type requiring the
   identity without guaranteeing the identity existed: the worker pipeline's run control, the
   signal bus hosted service, the transport consumer builder, and system event auditing. Each
   worked only because a fuller composition happened to register it first. `AddWhizbangInstanceIdentity()`
   now makes each stand alone.

2. **A fail-open gate that would have become fail-closed.** `TransportConsumerWorker` treats a null
   service name as "this service cannot know who it is" and accepts targeted messages rather than
   discarding them. Expressing the absent identity as a value made `"Unknown"` look like a real
   service name, so every targeted message would have been read as foreign and discarded. An
   existing test caught it. Without that test the change would have silently converted fail-open
   into fail-closed for precisely the hosts least able to notice.

3. **The four behavior-carrying null checks are now unreachable.** `InstanceStateRunControl`,
   `StandbyWatcher`, `OutboxPublishWorker`, and the Postgres work coordinator each had
   `if (_instanceProvider is null) return;`, meaning "no identity, skip identity-dependent work".
   Since the provider is registered unconditionally, those branches never fired in a composed
   application; they only protected direct construction. They are dead now and should be removed.

**`UnknownServiceInstanceProvider` was added, and deliberately not registered.** "This host has no
identity" is a real state with real behavior attached, so it needs to be expressible. Registering it
as a default would let a real composition quietly run anonymous, which is the outcome this work
exists to prevent, so it is available only to callers that construct these types directly.

**Cost:** 253 call sites updated, almost all in tests. Direct construction without an identity is
now a compile error, which was the deliberate intent of the original optional parameter and also
the reason the defect shipped.

## Triage outcome: the twelve candidates are not defects

All twelve resolved. None is a service silently missing from a deployed system, and the register
records that plainly rather than leaving a list of open suspicions behind.

| Interface | Outcome |
|---|---|
| `IChaosInjector` | now registered; converted to a required dependency with `NoChaosInjector` |
| `IDestructionHook` | now registered; converted with `NoOpDestructionHook` |
| `IConcurrencyGovernor` | optional, and each site constructs its own default inline |
| `IPerspectiveCompletionStrategy` | optional with an inline default |
| `IEnvelopeRegistry` | optional with an inline default |
| `IProcessedEventCacheObserver` | passed through to a cache that coalesces to a null object |
| `ILibraryVersionProvider` | every use is `?.`-guarded |
| `IInstanceAliveLockSource` | every use is guarded |
| `ICommandInboxAddressResolver` | every use is guarded |
| `IWorkChannelWriter` | guarded in `ClaimWorker`; supplied by a storage driver elsewhere |
| `IEventNamespaceRegistry` | a test seam; production reads a static registry |
| `ICallerInfo` | a nullable property on a message context, not an injected dependency |

`IConcurrencyGovernor` was singled out earlier as the highest-priority item because three
implementations existed and nothing registered any of them. That reasoning was wrong in a way worth
keeping: implementations existing does not imply a registration is missing. The workers select among
them in code, and the parameter is optional precisely so a host can override that selection.

## Confirmed defects, whole investigation

Three, all fixed:

1. **The audit decorator built with three of six arguments.** Shipped. Audit records written by any
   deployed version after the instance provider was introduced carry no writer identity.
2. **`DispatcherEventCascader` constructed without its logger** at both registration sites, so the
   cascader wrote no diagnostics in any composed application. Found by `WHIZ500` on its first run.
3. **The destruction hook default registration was never committed.** Self-inflicted during this
   work: the edit failed silently, and resolving `IStreamCloser` would have thrown. Found while
   writing documentation, not by any test.

The third is the one to remember. It passed the build, passed the suite, and passed the validator,
because the manifest could not see factory registrations at the time. Every guard here has a blind
spot, and the blind spots are where the defects live.

## Phase 3 conversion: every remaining declaration (complete)

Every optional interface-typed constructor parameter in the framework assembly is now required, and
every one of them has a registered default. Baseline dropped 140 to 1. The one that remains is
`ReceptorInfo.CallerInfo`, a positional record parameter: the record is a data carrier, there is no
container to register a default in, and the analyzer (WHIZ501) exempts positional records and
`System.Collections` interfaces for that reason. The reflection ratchet in
`CompositionSatisfiabilityTests` does not apply the exemption, so its baseline is 1, not 0.

How the defaults were supplied, by kind:

- **Working default, TryAdd** (`WhizbangDefaultsServiceCollectionExtensions.TryAddWhizbangDefaults`):
  channel writers, envelope registry and serializer, lifecycle deserializer, the batched completion
  strategy sized from the perspective retry options, routing strategies from `RoutingOptions`, an
  empty configuration root, `AddLogging` and `AddMetrics`.
- **Null default with a capability flag** where the real implementation comes from a storage driver or
  a transport: dead-letter store, snapshot store, stream locker, signal bus, startup assessor, duty
  elector, notification listener, publish strategy, event-type provider, message-type catalog,
  receptor registry, inbox-address resolver, alive-lock source, scoped event tracker. Each implements
  `INullDefault` and reports `IsConfigured` (or `IsAvailable`) false; consumers branch on the flag where
  they used to branch on null. Subsystems displace the placeholder with
  `TryAddSingletonOverNullDefault`, which leaves a host's own registration alone whatever the order.
- **Keyed per worker**: the two concurrency governors (`OutboxDrainWorker.GOVERNOR_KEY`,
  `PerspectiveWorker.GOVERNOR_KEY`).

Two pre-existing `#pragma warning disable WHIZ501` suppressions (heartbeat worker, inbox handler
worker) were removed and their sites converted. Every standalone extension (worker pipeline, routing
builder, transport consumer builder, message security, system events, signal bus, the transports,
the Postgres notification stack) registers the defaults first, because the 386 test failures that
followed the conversion were all one shape: a container composed from one extension without
`AddWhizbang`, activating a type whose logger was never registered.

### A finding this surfaced

Nothing registers `IInstanceAliveLockSource`. `PgSharedNotifyConnection` implements it but is only
exposed as `INotifySignalingGate` and `ISharedNotifyConnection`, so the heartbeat worker's watchdog
has always been told the alive lock is not held. Left as is here (changing heartbeat behavior is not
this conversion's job) and filed as ADO #19006.

## Next actions

1. Replace this reflection-based audit with the generated manifest once the generator lands, keeping
   the audit as a cross-check that the manifest is complete.
2. Register the alive-lock source from the notification stack (ADO #19006) and cover the watchdog
   decision it feeds.

Items 1 to 3 of the earlier list (classify, assess deployed impact, convert to required plus a
`TryAdd` default) are complete as of the phase 3 conversion above.
