// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.TopLevelRouted;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1405, against the REAL generated dispatcher: a receptor that returns its follow-up wrapped directly
/// (<c>return Route.Local(evt);</c>), not inside a tuple, keeps that route on every entry. The generated invokers
/// used to strip the wrapper before the cascade read it, so the follow-up cascaded with the default route: a
/// message the receptor kept local went out, and one it sent out also ran here.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/DispatcherSnippets.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/message-cascade#dispatch-mode</docs>
public class TopLevelRoutedResultComponentTests {
  public enum Entry {
    SendGeneric,
    SendObject,
    LocalInvokeVoid,
    LocalInvokeObject,
    Publish,
    CascadeLocal,
  }

  [Test]
  [MatrixDataSource]
  public async Task ATopLevelRouteLocalResult_RunsHereAndNeverGoesOutAsync([Matrix] Entry entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, id, keepLocal: true);

    await Assert.That(RoutedProbe.HandledHere(id)).IsEqualTo(1)
      .Because("Route.Local runs the follow-up in this service");
    // Route.Local is LocalDispatch | EventStore: the follow-up is stored (a row with no destination) and never sent
    await Assert.That(outbox.QueuedOf(typeof(TrFollowUp)).Count(m => m.Destination is not null)).IsEqualTo(0)
      .Because("Route.Local never sends the follow-up to another service; the wrapper must reach the cascade");
  }

  [Test]
  [MatrixDataSource]
  public async Task ATopLevelRouteOutboxResult_GoesOutAndDoesNotRunHereAsync([Matrix] Entry entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, id, keepLocal: false);

    await Assert.That(outbox.QueuedOf(typeof(TrFollowUp)).Count(m => m.Destination is not null)).IsEqualTo(1)
      .Because("Route.Outbox sends the follow-up out");
    await Assert.That(RoutedProbe.HandledHere(id)).IsEqualTo(0)
      .Because("Route.Outbox does not run the follow-up in this service");
  }

  private static async Task _dispatchAsync(IDispatcher dispatcher, Entry entry, Guid id, bool keepLocal) {
    object command = keepLocal ? new TrKeepLocal(id) : new TrSendOut(id);
    switch (entry) {
      case Entry.SendGeneric when keepLocal:
        await dispatcher.SendAsync(new TrKeepLocal(id));
        break;
      case Entry.SendGeneric:
        await dispatcher.SendAsync(new TrSendOut(id));
        break;
      case Entry.SendObject:
        await dispatcher.SendAsync(command);
        break;
      case Entry.LocalInvokeVoid when keepLocal:
        await dispatcher.LocalInvokeAsync(new TrKeepLocal(id));
        break;
      case Entry.LocalInvokeVoid:
        await dispatcher.LocalInvokeAsync(new TrSendOut(id));
        break;
      case Entry.LocalInvokeObject:
        await dispatcher.LocalInvokeAsync(command);
        break;
      case Entry.Publish when keepLocal:
        await dispatcher.PublishAsync(new TrAnnounceLocal(id));
        break;
      case Entry.Publish:
        await dispatcher.PublishAsync(new TrAnnounceOut(id));
        break;
      case Entry.CascadeLocal:
        await dispatcher.CascadeMessageAsync(keepLocal ? new TrAnnounceLocal(id) : new TrAnnounceOut(id), sourceEnvelope: null, DispatchModes.Local);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
    }
  }

  private static ServiceProvider _buildProvider(PolicyRecordingOutbox outbox) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer>(new PolicyContextKeepingEnvelopeSerializer());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
