// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Unit.Dispatcher.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Inside a handling, a command the body sends or invokes takes an id derived from the handling
/// (<see cref="SentMessageIdentity"/>), on ordinals of its own, and an event it creates on any path takes the handling's
/// emission ordinals (#2543). The exact values are pinned here; every dispatch method is probed in
/// <c>DispatchSurfaceDerivedIdentityComponentTests</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Messaging")]
public class DispatcherSendPathsDerivedIdentityTests {

  /// <summary>A command handled here.</summary>
  public record DerivedSendProbeCommand(string Data) : ICommand;

  /// <summary>An event a handling publishes, on a stream it names.</summary>
  public record DerivedSendProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>Handles the command.</summary>
  public class DerivedSendProbeCommandReceptor : IReceptor<DerivedSendProbeCommand> {
    /// <inheritdoc />
    public ValueTask HandleAsync(DerivedSendProbeCommand message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>The handled message.</summary>
  public record DerivedSendSource(string Data);

  private const string HANDLER = "ShipOrderHandler";

  [Test]
  public async Task SendAsync_InsideAHandling_TakesTheIdDerivedAtTheHandlingsFirstSendOrdinalAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    Guid[] ids;
    using (ReceptorHandling.Enter(_handling(source))) {
      ids = [
        (await dispatcher.SendAsync(new DerivedSendProbeCommand("a"))).MessageId.Value,
        (await dispatcher.SendAsync(new DerivedSendProbeCommand("b"))).MessageId.Value,
      ];
    }

    var type = TypeNameFormatter.Format(typeof(DerivedSendProbeCommand));
    await Assert.That(ids[0]).IsEqualTo(SentMessageIdentity.Derive(source, serviceName, HANDLER, type, ordinal: 0))
      .Because("a command sent inside a handling derives its id from the handled message, the service, the handler and its type");
    await Assert.That(ids[1]).IsEqualTo(SentMessageIdentity.Derive(source, serviceName, HANDLER, type, ordinal: 1))
      .Because("each send takes the handling's next send ordinal, so sends sort in the order they were made");
  }

  [Test]
  public async Task SendAsync_InsideAHandling_DoesNotShiftTheHandlingsEventIdsAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    Guid published;
    using (ReceptorHandling.Enter(_handling(source))) {
      await dispatcher.SendAsync(new DerivedSendProbeCommand("a"));
      published = (await dispatcher.PublishAsync(new DerivedSendProbeEvent((Guid)TrackedGuid.New()))).MessageId.Value;
    }

    await Assert.That(published).IsEqualTo(
      EmissionIdentity.Derive(source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DerivedSendProbeEvent)), ordinal: 0))
      .Because("a command's id takes ordinals of its own, so the events a handling publishes keep the ids they had before it sent");
  }

  [Test]
  public async Task CascadeMessageAsync_NoSourceEnvelope_InsideAHandling_DerivesFromTheHandlingAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();

    Guid published;
    using (ReceptorHandling.Enter(_handling(source))) {
      await dispatcher.CascadeMessageAsync(new DerivedSendProbeEvent(stream), sourceEnvelope: null, DispatchModes.Local);
      published = (await dispatcher.PublishAsync(new DerivedSendProbeEvent(stream))).MessageId.Value;
    }

    await Assert.That(published).IsEqualTo(
      EmissionIdentity.Derive(source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DerivedSendProbeEvent)), ordinal: 1))
      .Because("a cascade with no source envelope, made inside a handling, is the handling's emission and takes ordinal zero");
  }

  [Test]
  public async Task SendAsync_AHandlingWithNoMessageId_MintsAsync() {
    var (dispatcher, _) = _createDispatcher();

    Guid first;
    using (ReceptorHandling.Enter(_handling(Guid.Empty))) {
      first = (await dispatcher.SendAsync(new DerivedSendProbeCommand("a"))).MessageId.Value;
    }
    Guid second;
    using (ReceptorHandling.Enter(_handling(Guid.Empty))) {
      second = (await dispatcher.SendAsync(new DerivedSendProbeCommand("a"))).MessageId.Value;
    }

    await Assert.That(second).IsNotEqualTo(first)
      .Because("a handling with no message id has no cause to derive from, so its sends mint as they did");
    await Assert.That(first.Version).IsEqualTo(7);
  }

  [Test]
  public async Task SendAsync_OutsideAHandling_MintsAFreshIdEachTimeAsync() {
    var (dispatcher, _) = _createDispatcher();

    var first = (await dispatcher.SendAsync(new DerivedSendProbeCommand("a"))).MessageId.Value;
    var second = (await dispatcher.SendAsync(new DerivedSendProbeCommand("a"))).MessageId.Value;

    await Assert.That(second).IsNotEqualTo(first);
    await Assert.That(first.Version).IsEqualTo(7);
  }

  private static MessageEnvelope<object> _handling(Guid sourceMessageId) => new() {
    MessageId = sourceMessageId == Guid.Empty ? default : MessageId.From(sourceMessageId),
    Payload = new DerivedSendSource("origin"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = HANDLER },
  };

  private static (IDispatcher Dispatcher, string ServiceName) _createDispatcher() {
    var services = new ServiceCollection();
    var instance = new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build());
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    return (provider.GetRequiredService<IDispatcher>(), instance.ServiceName);
  }
}
