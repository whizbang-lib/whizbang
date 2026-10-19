// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PostLifecycleCounts;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1413, against the REAL generated dispatcher and receptor registry: PostLifecycle fires once for
/// every message this service handles in-process, whichever call handled it. Before the fix only
/// <c>LocalInvokeAsync</c> fired it; a send, a publish, a batch and a cascade's local run did not, so a
/// receptor declared at PostLifecycle ran or not depending on the call.
/// </summary>
/// <remarks>
/// <para>Once means once per handling service. A stored event that a perspective on this service applies
/// reaches PostLifecycle through the perspective worker, after its perspectives, so the local run leaves
/// it to the worker rather than firing it a second time.</para>
/// <para>The Inline count is read after the dispatch returns: the dispatcher awaits that stage on every path,
/// so a second firing path shows up there deterministically. The Detached stage runs in the background
/// under a lifecycle coordinator, so its count is read once its signal arrives.</para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/lifecycle/lifecycle-stages#post-lifecycle</docs>
public class PostLifecycleOncePerHandlingServiceComponentTests {
  public enum Entry {
    SendGeneric,
    SendObject,
    SendWithOptions,
    SendManyGeneric,
    SendManyObject,
    PublishGeneric,
    PublishObject,
    PublishManyGeneric,
    PublishManyObject,
    CascadeFromSend,
    LocalInvokeVoid,
  }

  [Test]
  [MatrixDataSource]
  public async Task EveryLocalRun_FiresPostLifecycleExactlyOnceAsync(
      [Matrix] Entry entry,
      [Matrix(false, true)] bool withOutbox,
      [Matrix(false, true)] bool withLifecycleCoordinator) {
    await using var provider = _buildProvider(withOutbox, withLifecycleCoordinator, withPerspective: false);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, id);
    // The Detached stage runs in the background under a lifecycle coordinator; the bound only stops a
    // missing invocation from hanging the run
    await PostLifecycleProbe.DetachedRanAsync(id).WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(PostLifecycleProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the business receptor runs once");
    await Assert.That(PostLifecycleProbe.DetachedCount(id)).IsEqualTo(1)
      .Because("PostLifecycleDetached fires once for every message this service handles in-process");
    await Assert.That(PostLifecycleProbe.InlineCount(id)).IsEqualTo(1)
      .Because("PostLifecycleInline fires once for every message this service handles in-process");
  }

  [Test]
  [Arguments(true, 0)]
  [Arguments(false, 1)]
  public async Task AnEventAPerspectiveHereApplies_LeavesPostLifecycleToThePerspectiveWorkerOnlyWhenStoredAsync(
      bool withOutbox, int expectedLocalFirings) {
    await using var provider = _buildProvider(withOutbox, withLifecycleCoordinator: false, withPerspective: true);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await dispatcher.PublishAsync(new PlProjectedEvent(id));

    await Assert.That(PostLifecycleProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the business receptor runs once either way");
    await Assert.That(PostLifecycleProbe.DetachedCount(id)).IsEqualTo(expectedLocalFirings)
      .Because("a stored event a perspective here applies reaches PostLifecycle through the perspective worker; one that is not stored never reaches it, so the local run fires it");
    await Assert.That(PostLifecycleProbe.InlineCount(id)).IsEqualTo(expectedLocalFirings)
      .Because("both PostLifecycle stages follow the same rule");
  }

  private static async Task _dispatchAsync(IDispatcher dispatcher, Entry entry, Guid id) {
    var command = new PlCommand(id);
    var @event = new PlEvent(id);
    switch (entry) {
      case Entry.SendGeneric:
        await dispatcher.SendAsync(command);
        break;
      case Entry.SendObject:
        await dispatcher.SendAsync((object)command);
        break;
      case Entry.SendWithOptions:
        await dispatcher.SendAsync((object)command, new DispatchOptions());
        break;
      case Entry.SendManyGeneric:
        await dispatcher.SendManyAsync<PlCommand>([command]);
        break;
      case Entry.SendManyObject:
        await dispatcher.SendManyAsync([(object)command]);
        break;
      case Entry.PublishGeneric:
        await dispatcher.PublishAsync(@event);
        break;
      case Entry.PublishObject:
        await dispatcher.PublishAsync((object)@event);
        break;
      case Entry.PublishManyGeneric:
        await dispatcher.PublishManyAsync<PlEvent>([@event]);
        break;
      case Entry.PublishManyObject:
        await dispatcher.PublishManyAsync([(object)@event]);
        break;
      case Entry.CascadeFromSend:
        await dispatcher.SendAsync(new PlCascadingCommand(id));
        break;
      case Entry.LocalInvokeVoid:
        await dispatcher.LocalInvokeAsync(command);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
    }
  }

  private static ServiceProvider _buildProvider(bool withOutbox, bool withLifecycleCoordinator, bool withPerspective) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    if (withOutbox) {
      var outbox = new PolicyRecordingOutbox();
      services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    }
    if (withLifecycleCoordinator) {
      services.AddSingleton<ILifecycleCoordinator>(new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance));
    }
    if (withPerspective) {
      services.AddSingleton<IPerspectiveRunnerRegistry, PlPerspectiveRegistry>();
    }
    services.AddSingleton<IEnvelopeSerializer>(new PolicyContextKeepingEnvelopeSerializer());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
