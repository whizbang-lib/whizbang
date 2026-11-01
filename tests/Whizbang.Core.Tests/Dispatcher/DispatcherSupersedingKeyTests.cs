// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A superseding event leaves the dispatcher flagged <see cref="EventFlags.Superseding"/> with its key in the outbox
/// row's metadata (#1729), on the publish path and on the cascade path, so the emit chain can stamp the key on the
/// stored event. An ordinary event carries neither.
/// </summary>
/// <docs>fundamentals/events/superseding-events#the-key</docs>
[Category("Dispatcher")]
public sealed class DispatcherSupersedingKeyTests {
  [Superseding]
  public sealed record ProfileEmbedded([property: StreamId] Guid ProfileId, [property: SupersedingKey] string Model) : IEvent;

  public sealed record ProfileRenamed([property: StreamId] Guid ProfileId) : IEvent;

  public sealed record EmbedProfileCommand(Guid ProfileId, string Model);

  public sealed class EmbedProfileReceptor : IReceptor<EmbedProfileCommand, ProfileEmbedded> {
    public ValueTask<ProfileEmbedded> HandleAsync(EmbedProfileCommand message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new ProfileEmbedded(message.ProfileId, message.Model));
  }

  private static readonly Guid _profile = Guid.Parse("0199a1b2-0000-7000-8000-000000000101");

  [Test]
  public async Task PublishAsync_ASupersedingEvent_CarriesTheFlagAndItsKey_AnOrdinaryEventNeitherAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _createDispatcher(strategy);

    await dispatcher.PublishAsync(new ProfileEmbedded(_profile, "model-a"));
    await dispatcher.PublishAsync(new ProfileRenamed(_profile));

    await Assert.That(strategy.Queued.Count).IsEqualTo(2);
    await Assert.That(strategy.Queued[0].Flags).IsEqualTo(EventFlags.Superseding);
    await Assert.That(strategy.Queued[0].Metadata!.SupersedingKey).IsEqualTo("model-a")
      .Because("the emit chain reads the key from the outbox row's metadata");
    await Assert.That(strategy.Queued[1].Flags).IsEqualTo(EventFlags.None);
    await Assert.That(strategy.Queued[1].Metadata!.SupersedingKey).IsNull()
      .Because("the control: an ordinary event is stored with no key");
  }

  [Test]
  public async Task Cascade_ASupersedingEventAReceptorReturns_CarriesItsKeyAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _createDispatcher(strategy);

    await dispatcher.LocalInvokeAsync<EmbedProfileCommand, ProfileEmbedded>(new EmbedProfileCommand(_profile, "model-b"));

    var queued = strategy.Queued.Single(m => m.MessageType!.Contains(nameof(ProfileEmbedded), StringComparison.Ordinal));
    await Assert.That(queued.Flags).IsEqualTo(EventFlags.Superseding);
    await Assert.That(queued.Metadata!.SupersedingKey).IsEqualTo("model-b");
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

  private static IDispatcher _createDispatcher(CapturingStrategy strategy) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
