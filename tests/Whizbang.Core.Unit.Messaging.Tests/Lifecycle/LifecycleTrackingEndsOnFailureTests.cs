// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// <para>A path that begins lifecycle tracking ends it on every outcome, not only on success (#2228). The inbox paths
/// ended their entry after the last stage, so a stage that threw or was canceled left the entry, which holds the full
/// typed envelope and payload, until the stale sweep. Each case here makes an inline stage throw and requires the
/// coordinator to hold no entry afterward.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Core")]
[Category("Lifecycle")]
public sealed class LifecycleTrackingEndsOnFailureTests {

  [Test]
  public async Task TransportConsumer_PostLifecycleStageThrows_EndsItsTrackingAsync() {
    var (coordinator, provider) = _build();
    await using var _ = provider;
    var eventId = Guid.NewGuid();

    await Assert.That(async () => await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
        _work(eventId), _envelope(eventId), new InlineThrowingInvoker(), _context(), provider, CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(coordinator.TrackedCount).IsEqualTo(0)
      .Because("the path that began the entry ends it whatever its stages did; nothing else would");
  }

  [Test]
  public async Task TransportConsumer_PostLifecycleCanceled_EndsItsTrackingAsync() {
    var (coordinator, provider) = _build();
    await using var _ = provider;
    var eventId = Guid.NewGuid();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.That(async () => await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
        _work(eventId), _envelope(eventId), new InlineThrowingInvoker(), _context(), provider, cts.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(coordinator.TrackedCount).IsEqualTo(0);
  }

  [Test]
  public async Task ServiceBusConsumer_PostLifecycleStageThrows_EndsItsTrackingAsync() {
    var (coordinator, provider) = _build();
    await using var _ = provider;
    var eventId = Guid.NewGuid();

    await Assert.That(async () => await ServiceBusConsumerWorker.InvokePostLifecycleForEventAsync(
        _work(eventId), _envelope(eventId), new InlineThrowingInvoker(), _context(), provider, _ => { }, CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(coordinator.TrackedCount).IsEqualTo(0)
      .Because("the path that began the entry ends it whatever its stages did; nothing else would");
  }

  private static (LifecycleCoordinator Coordinator, ServiceProvider Provider) _build() {
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    var services = new ServiceCollection();
    services.AddSingleton<ILifecycleCoordinator>(coordinator);
    services.AddSingleton<IReceptorInvoker>(new InlineThrowingInvoker());
    return (coordinator, services.BuildServiceProvider());
  }

  private static LifecycleExecutionContext _context() => new() {
    CurrentStage = LifecycleStage.PostInboxInline,
    MessageSource = MessageSource.Inbox,
    AttemptNumber = 1
  };

  private static InboxWork _work(Guid eventId) => new() {
    MessageId = eventId,
    Envelope = _envelope(eventId),
    MessageType = "Test.TestEvent, Test"
  };

  private static MessageEnvelope<JsonElement> _envelope(Guid eventId) => new() {
    MessageId = new MessageId(eventId),
    Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  /// <summary>Throws from every inline stage; a detached stage completes, so only the inline failure is in play.</summary>
  private sealed class InlineThrowingInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      cancellationToken.ThrowIfCancellationRequested();
      return stage.IsDetached()
        ? ValueTask.CompletedTask
        : ValueTask.FromException(new InvalidOperationException($"{stage} receptor failed"));
    }
  }
}
