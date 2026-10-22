// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests.Generators;

// Shared by the unit and component projects (one source, compiled into both), so the saga generator
// emits these sagas in each test assembly.

// ── Saga declarations the generator will pick up ───────────────────────

[Saga("GeneratorTestDefault")]
public partial class GeneratorTestDefaultSaga;

public class FakeProjectEventBase : Whizbang.Core.IEvent {
  /// <summary>Every emitted saga event overrides this with its own [StreamId] EntityId; the base
  /// carries one so the base type itself satisfies stream-id resolution (WHIZ009).</summary>
  [Whizbang.Core.StreamId] public Guid StreamEntityId { get; set; }
  public Guid MessageId { get; set; } = Guid.NewGuid();
}

[Saga<FakeProjectEventBase>("GeneratorTestCustomBase")]
public partial class GeneratorTestCustomBaseSaga;

/// <summary>A saga followed by enrichment over what it wrote, and by cleanup if it was abandoned.</summary>
[Saga("GeneratorTestChained")]
[ContinuesWith("GeneratorTestFollowOn")]
[ContinuesWith("GeneratorTestCleanup", SagaContinuationTriggers.Failed)]
public partial class GeneratorTestChainedSaga;

/// <summary>
/// A saga whose steps must not outlive a failed run. No generated service, because a generated one
/// brings a watchdog receptor every test host in this project would then have to register it for.
/// </summary>
[Saga("GeneratorTestSignalsOutcome", GenerateService = false)]
[SignalsOutcome]
public partial class GeneratorTestSignalsOutcomeSaga;

/// <summary>A saga that keeps a partly failed run, declared without a generated service.</summary>
[Saga("GeneratorTestTolerantOutcome", GenerateService = false)]
[SignalsOutcome(OnCompletedWithFailures = SagaOutcome.Commit)]
public partial class GeneratorTestTolerantOutcomeSaga;
