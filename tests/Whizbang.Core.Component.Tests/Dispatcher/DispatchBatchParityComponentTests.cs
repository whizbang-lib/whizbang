// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A batch call treats each message exactly as the single-message call would (#1410 follow-up, E9):
/// the schedule, the deferred-local mark, cancellation and the missing-outbox behavior all carry over.
/// Each test pairs with the single-message behavior already pinned in DispatchPolicyBehaviorComponentTests.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchBatchParityComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.PolicyBehavior.Owned";
  private static readonly DateTimeOffset _future = DateTimeOffset.UtcNow.AddYears(10);

  [Test]
  public async Task SendMany_ScheduledInTheFuture_IsNotHandledNowAndQueuesAScheduledDeferredCopyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendManyAsync(new object[] { command }, new DispatchOptions().WithScheduledFor(_future));

    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(0)
      .Because("a scheduled message waits for its time in a batch as it does alone (#1397)");
    var queued = outbox.QueuedOf(typeof(PbUnownedCommand));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].ScheduledFor).IsEqualTo(_future);
    await Assert.That(queued[0].Envelope.DispatchContext.LocalDeferred).IsTrue()
      .Because("this service handles it when the scheduled copy comes back");
  }

  [Test]
  public async Task PublishMany_ScheduledInTheFuture_IsNotHandledNowAndQueuesAScheduledDeferredCopyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishManyAsync(new object[] { evt }, new DispatchOptions().WithScheduledFor(_future));

    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), evt.Id)).IsEqualTo(0);
    var queued = outbox.QueuedOf(typeof(PbEvent));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].ScheduledFor).IsEqualTo(_future);
    await Assert.That(queued[0].Envelope.DispatchContext.LocalDeferred).IsTrue();
  }

  [Test]
  public async Task LocalSendMany_ScheduledInTheFuture_IsRefusedLikeLocalInvokeAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    await Assert.That(async () =>
        await _dispatcher(provider).LocalSendManyAsync(new object[] { command }, new DispatchOptions().WithScheduledFor(_future)))
      .Throws<Exception>();
    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(0);
  }

  [Test]
  public async Task SendMany_CanceledBeforeDispatch_HandlesAndQueuesNothingAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbUnownedCommand(Guid.CreateVersion7());
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () =>
        await _dispatcher(provider).SendManyAsync(new object[] { command }, new DispatchOptions { CancellationToken = canceled.Token }))
      .Throws<OperationCanceledException>();
    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(0);
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty();
  }

  [Test]
  public async Task PublishMany_CanceledBeforeDispatch_HandlesAndQueuesNothingAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbEvent(Guid.CreateVersion7());
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () =>
        await _dispatcher(provider).PublishManyAsync(new object[] { evt }, new DispatchOptions { CancellationToken = canceled.Token }))
      .Throws<OperationCanceledException>();
    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), evt.Id)).IsEqualTo(0);
    await Assert.That(outbox.QueuedOf(typeof(PbEvent))).IsEmpty();
  }

  [Test]
  public async Task PublishMany_WithNoOutbox_HandlesTheEventLocallyWithoutThrowingLikePublishAsync() {
    await using var provider = _buildProvider(outbox: null);
    var single = new PbEvent(Guid.CreateVersion7());
    var batched = new PbEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(single);
    await _dispatcher(provider).PublishManyAsync(new object[] { batched });

    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), single.Id)).IsEqualTo(1);
    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), batched.Id)).IsEqualTo(1)
      .Because("with no outbox an event is handled here and its copy deferred or dropped with a log, in a batch as alone");
  }

  private static IDispatcher _dispatcher(ServiceProvider provider) => provider.GetRequiredService<IDispatcher>();

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy? outbox) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    if (outbox is not null) {
      services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    }
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
