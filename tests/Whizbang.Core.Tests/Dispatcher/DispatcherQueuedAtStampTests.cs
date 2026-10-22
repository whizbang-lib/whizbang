// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>A command whose QueuedAt is stamped as it is written to the outbox (#1557).</summary>
public sealed record QueuedAtStampedCommand(
    Guid Id,
    [property: PopulateTimestamp(TimestampKind.QueuedAt)] DateTimeOffset? QueuedAt = null) : ICommand;

/// <summary>An event whose QueuedAt is stamped as it is written to the outbox (#1557).</summary>
public sealed record QueuedAtStampedEvent(
    [property: StreamId] Guid Id,
    [property: PopulateTimestamp(TimestampKind.QueuedAt)] DateTimeOffset? QueuedAt = null) : IEvent;

/// <summary>A class message, populated in place rather than through a record copy (#1557).</summary>
public sealed class QueuedAtStampedClassCommand : ICommand {
  public Guid Id { get; set; }

  [PopulateTimestamp(TimestampKind.QueuedAt)]
  public DateTimeOffset? QueuedAt { get; set; }
}

/// <summary>
/// <c>[PopulateTimestamp(TimestampKind.QueuedAt)]</c> is stamped when the message is written to the
/// outbox (#1557): every dispatch path that writes an outbox copy stores the message with the time it
/// was queued, read from the injected <see cref="TimeProvider"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>extending/attributes/auto-populate</docs>
[Category("Core")]
[Category("AutoPopulate")]
public class DispatcherQueuedAtStampTests {
  // Far from the wall clock, so a stamp read from anything but the injected provider cannot match.
  private static readonly DateTimeOffset _queuedAt = new(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  [Arguments("SendClassAsync")]
  public async Task Dispatch_WritesOutboxCopy_StampsQueuedAtFromTheTimeProviderAsync(string entry) {
    var strategy = new CapturingStrategy();
    var dispatcher = _createDispatcher(strategy, new FakeTimeProvider(_queuedAt));
    var id = Guid.CreateVersion7();

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync(new QueuedAtStampedCommand(id)); break;
      case "SendManyAsync": await dispatcher.SendManyAsync([new QueuedAtStampedCommand(id)]); break;
      case "PublishAsync": await dispatcher.PublishAsync(new QueuedAtStampedEvent(id)); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync([new QueuedAtStampedEvent(id)]); break;
      default: await dispatcher.SendAsync(new QueuedAtStampedClassCommand { Id = id }); break;
    }

    await Assert.That(strategy.Queued).Count().IsEqualTo(1).Because($"{entry}: the dispatch writes one outbox copy");
    var stored = _queuedAtOf(strategy.Queued[0].Envelope.Payload);
    await Assert.That(stored).IsEqualTo(_queuedAt)
      .Because($"{entry}: the stored copy carries the time it was written to the outbox, from the injected TimeProvider");
  }

  [Test]
  public async Task Dispatch_UnmarkedMessage_StoresItUnchangedAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _createDispatcher(strategy, new FakeTimeProvider(_queuedAt));

    await dispatcher.SendAsync(new QueuedAtUnmarkedCommand(Guid.CreateVersion7()));

    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(_tryGetProperty(strategy.Queued[0].Envelope.Payload, "queuedAt", out _)).IsFalse()
      .Because("a message with no QueuedAt property has nothing stamped onto it");
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  private static DateTimeOffset? _queuedAtOf(JsonElement payload) =>
    _tryGetProperty(payload, "queuedAt", out var value) && value.ValueKind != JsonValueKind.Null
      ? value.GetDateTimeOffset()
      : null;

  private static bool _tryGetProperty(JsonElement payload, string name, out JsonElement value) {
    foreach (var property in payload.EnumerateObject()) {
      if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
        value = property.Value;
        return true;
      }
    }
    value = default;
    return false;
  }

  private static IDispatcher _createDispatcher(CapturingStrategy strategy, TimeProvider timeProvider) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddSingleton(timeProvider);
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  private sealed class CapturingStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> Queued { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => Queued.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}

/// <summary>A command with no QueuedAt property: the outbox write leaves it as it is.</summary>
public sealed record QueuedAtUnmarkedCommand(Guid Id) : ICommand;
