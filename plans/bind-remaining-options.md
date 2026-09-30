# Binding the remaining options classes

Tracking list for closing the gap between what an operator can set at deploy time and what needs a
code change and a redeploy. Raised as whizbang-lib/whizbang#1012.

## Where it stands

Whizbang binds **44 configuration sections** through 35 `ConfigurationBinder.Bind(` call sites, each
a literal `configuration.GetSection("Whizbang…")` compiled by the binder source generator, plus a
handful of hand-rolled binders. `BindConfiguration` is deliberately never used, for AOT.

Counting the configuration reference's own tables, that is **264 values reachable from configuration
against 169 that are not**. The line between them is hard to defend: `Whizbang:Workers:OutboxDrain`
binds, while the redelivery pump, the circuit breaker and the stream rate limiter do not — which is
what someone reaches for during an incident.

## A — already settable, no framework change (done)

Four shapes own no section but are properties of one that does, and the binder walks into them. They
were documented as unreachable; that was wrong and is corrected in the docs site (PR #753). **57
keys.**

| Shape | Reachable as | Keys |
|---|---|---|
| `WhizbangGuardrailsOptions` | `Whizbang__Guardrails__…` | 4 |
| `WorkerRetryOptions` | `Whizbang__Workers__Perspective__RetryOptions__…` | 4 |
| `BatchFlusherOptions` | `Whizbang__Workers__<Worker>__Flusher__…` × 5 workers | 40 |
| `SlidingWindowBatcherOptions` | `Whizbang__Workers__<Worker>__Batcher__…` × 3 workers | 9 |

The lesson worth keeping: a class is settable when something reads a section that reaches it, not
when it owns one.

## B — process-wide, binds cleanly

One instance per process, so one section is meaningful. These follow the existing pattern directly:
`services.AddOptions<T>()` plus an `IConfigureOptions<T>` that binds the section, exactly as
`ClaimWorkerOptions` does in `WorkerPipelineExtensions.cs:560`.

| Class | Proposed section | Keys | Note |
|---|---|---|---|
| `WhizbangCoreOptions` | `Whizbang:Core` | 7 | `ShowBanner` already duplicates `WhizbangOptions.ShowBanner` **and** a raw `configuration["Whizbang:ShowBanner"]` read in `WhizbangStartupLog.cs:33`; resolve the duplication as part of this |
| `RedeliveryPumpOptions` | `Whizbang:Redelivery` | 6 | already falls back to `GetService<RedeliveryPumpOptions>()`, so registering a bound singleton is enough |
| `ThrottleRetryOptions` | `Whizbang:ThrottleRetry` | 4 | |
| `StreamRateLimiterOptions` | `Whizbang:StreamRateLimiter` | 4 | |
| `SystemEventOptions` | `Whizbang:SystemEvents` | 7 | |
| `MessageSecurityOptions` | `Whizbang:MessageSecurity` | 5 | |
| `WhizbangHealthOptions` | `Whizbang:Health` | 1 | per-component overrides are a dictionary, which binds |
| `StandbyWatcherOptions` | `Whizbang:StandbyWatcher` | 3 | currently a singleton registration |
| `WhizbangLifecycleOptions` | `Whizbang:Lifecycle` | 2 | |
| `DebuggerAwareClockOptions` | `Whizbang:DebuggerAwareClock` | 3 | |
| `PerspectiveSnapshotOptions` | `Whizbang:Perspectives:Snapshots` | 7 | already `AddOptions<T>` in the Dapper driver |
| `PerspectiveRewindOptions` | `Whizbang:Perspectives:Rewind` | 6 | already `AddOptions<T>` in the Dapper driver |
| `PerspectiveStreamLockOptions` | `Whizbang:Perspectives:StreamLock` | 2 | |
| `PerspectiveStreamAffinityOptions` | `Whizbang:Workers:PerspectiveAffinity` | 3 | |
| `PerStreamSerializerOptions` | `Whizbang:PerStreamSerializer` | 4 | |
| `SagaOptions` | `Whizbang:Sagas` | 6 | lives in `Whizbang.Sagas`, bind at its registration |
| `WhizbangScopeOptions` | `Whizbang:Scope` | 11 | GraphQL claim/header mappings |
| `WhizbangAvailabilityOptions` | `Whizbang:AspNet:Availability` | 3 | |
| `WhizbangCorrelationOptions` | `Whizbang:AspNet:Correlation` | 1 | |
| `WhizbangSecurityHeadersOptions` | `Whizbang:AspNet:SecurityHeaders` | 8 | |
| `WhizbangGraphQLOptions` | `Whizbang:GraphQL` | 5 | |
| `WhizbangStartupStatusGraphOptions` | `Whizbang:StartupStatusGraph` | 1 | |
| `ServiceRegistrationOptions` | `Whizbang:Core:Services` | 1 | nested under `WhizbangCoreOptions`, so it comes free with that one |

Roughly **100 keys**. Doing these alone would take the operator-settable share from 61% to about 85%.

## C — one section is the wrong shape

Not "harder", but wrong: there is more than one instance, and a single global section cannot express
that. Each needs a decision before any key is promised.

| Class | Why | Possible shape |
|---|---|---|
| `CircuitBreakerOptions` | constructor parameter per `CircuitBreaker<TResult>`; several exist with different settings | a named map, `Whizbang:CircuitBreakers:<name>` |
| `MessageProcessingOptions` | `TryAddSingleton` per transport consumer builder, two call sites, explicitly overridable per consumer | per-consumer, keyed by consumer name |
| `TransportOptions`, `TransportConsumerOptions`, `ServiceBusConsumerOptions`, `RabbitMQOptions` | per transport registration; `Whizbang:Transports:AzureServiceBus` already shows the per-transport shape | `Whizbang:Transports:<transport>` |
| `TransportBatchOptions` | per transport | with its transport |
| `SubscriptionResilienceOptions`, `ServiceBusInfrastructureOptions` | per Service Bus subscription | under the ASB section |
| `CoalescePolicyOptions` | a per-tag map on `TagOptions`, but `CoalesceBindings` is a get-only `IReadOnlyDictionary` over a private field, so the binder cannot populate it even though `Whizbang:Tags` binds | make the property settable, or expose a bindable map beside it |
| `PostgresOptions` | per database registration | per named database |

## D — not options classes

`ConnectionPool` (root section, generated DbContext registration) and the `wh_settings` database-side
settings. Neither belongs in this work.

## Sequencing

**A** is done. **B** is mechanical and can go in batches by area — core, perspectives, ASP.NET,
sagas, resilience — each a separate PR with tests, since every new bound section is public API that
then has to keep working. **C** needs the design decision in #1012 first.

**The docs must not run ahead of the code.** A published environment variable that nothing reads is
the same failure as the `Whizbang__WorkCoordinator__LeaseSeconds` warning that sent operators away
from a working knob, only inverted — and it is worse, because the key looks authoritative and fails
silently. Each batch ships its binding and its documentation together, or the documentation carries
the version the binding lands in.
