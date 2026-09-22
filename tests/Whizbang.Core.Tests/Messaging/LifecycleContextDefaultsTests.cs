using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers the default interface method bodies on <see cref="ILifecycleContext"/>.
/// </summary>
[Category("Core")]
[Category("Messaging")]
public class LifecycleContextDefaultsTests {
  /// <summary>Implements only the abstract members so the defaults stay inherited.</summary>
  private sealed class MinimalLifecycleContext : ILifecycleContext {
    public LifecycleStage CurrentStage { get; init; }
    public Guid? EventId { get; init; }
    public Guid? StreamId { get; init; }
    public Type? PerspectiveType { get; init; }
    public Guid? LastProcessedEventId { get; init; }
    public MessageSource? MessageSource { get; init; }
    public int? AttemptNumber { get; init; }
    public Whizbang.Core.Messaging.ProcessingMode? ProcessingMode { get; init; }
  }

  [Test]
  public async Task IsNewEvent_WhenNotOverridden_ReturnsTrueAsync() {
    ILifecycleContext context = new MinimalLifecycleContext();

    await Assert.That(context.IsNewEvent).IsTrue();
  }

  [Test]
  [Arguments(Whizbang.Core.Messaging.ProcessingMode.Replay)]
  [Arguments(Whizbang.Core.Messaging.ProcessingMode.Rebuild)]
  public async Task IsReplay_WhenReplayingOrRebuilding_ReturnsTrueAsync(
      Whizbang.Core.Messaging.ProcessingMode mode) {
    ILifecycleContext context = new MinimalLifecycleContext { ProcessingMode = mode };

    await Assert.That(context.IsReplay).IsTrue();
  }

  [Test]
  public async Task IsReplay_WhenLive_ReturnsFalseAsync() {
    ILifecycleContext context = new MinimalLifecycleContext {
      ProcessingMode = Whizbang.Core.Messaging.ProcessingMode.Live
    };

    await Assert.That(context.IsReplay).IsFalse();
  }

  [Test]
  public async Task IsReplay_WhenProcessingModeUnknown_ReturnsFalseAsync() {
    ILifecycleContext context = new MinimalLifecycleContext { ProcessingMode = null };

    await Assert.That(context.IsReplay).IsFalse();
  }
}
