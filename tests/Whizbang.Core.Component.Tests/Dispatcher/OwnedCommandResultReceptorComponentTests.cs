// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Component.Tests.PolicyBehavior.Owned;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// An owned command stays in its service on every path (#1403), so when it arrives by a publish or a
/// cascade its result-returning receptor must run here, once, with its result cascaded (#1410). These
/// run the REAL generated dispatcher: the defect was in what the generator emits for those paths.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class OwnedCommandResultReceptorComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.PolicyBehavior.Owned";

  [Test]
  [Arguments(DispatchModes.Outbox)]
  [Arguments(DispatchModes.Local)]
  [Arguments(DispatchModes.Both)]
  public async Task Cascade_OwnedCommandWithAResultReceptor_RunsHereOnceAndCascadesTheResultAsync(DispatchModes mode) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedTypedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().CascadeMessageAsync(command, sourceEnvelope: null, mode);

    await _assertRanOnceAndCascadedAsync(outbox, nameof(PbOwnedTypedCommandReceptor), command.Id, typeof(PbOwnedTypedCommand));
    await Assert.That(outbox.QueuedOf(typeof(PbOwnedTypedSecondEvent))).Count().IsEqualTo(1)
      .Because("every element of a tuple result cascades");
  }

  [Test]
  public async Task Publish_OwnedCommandWithAResultReceptor_RunsHereOnceAndCascadesTheResultAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedTypedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(command);

    await _assertRanOnceAndCascadedAsync(outbox, nameof(PbOwnedTypedCommandReceptor), command.Id, typeof(PbOwnedTypedCommand));
  }

  [Test]
  public async Task PublishMany_OwnedCommandWithAResultReceptor_RunsHereOnceAndCascadesTheResultAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedTypedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishManyAsync(new object[] { command });

    await _assertRanOnceAndCascadedAsync(outbox, nameof(PbOwnedTypedCommandReceptor), command.Id, typeof(PbOwnedTypedCommand));
  }

  [Test]
  public async Task ReceptorResult_OwnedCommandWithAResultReceptor_RunsHereOnceAndCascadesTheResultAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new PbTypedCascadeTrigger(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync(trigger);

    await _assertRanOnceAndCascadedAsync(outbox, nameof(PbOwnedTypedCommandReceptor), trigger.Id, typeof(PbOwnedTypedCommand));
  }

  [Test]
  public async Task Cascade_OwnedCommandWithASyncResultReceptor_RunsHereOnceAndCascadesTheResultAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedSyncTypedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox);

    await _assertRanOnceAndCascadedAsync(outbox, nameof(PbOwnedSyncTypedCommandReceptor), command.Id, typeof(PbOwnedSyncTypedCommand));
  }

  [Test]
  public async Task Cascade_OwnedCommandWithAnObjectResultReceptor_RunsHereExactlyOnceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedObjectResultCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox);

    await _assertRanOnceAndCascadedAsync(outbox, nameof(PbOwnedObjectResultCommandReceptor), command.Id, typeof(PbOwnedObjectResultCommand));
  }

  private static async Task _assertRanOnceAndCascadedAsync(PolicyRecordingOutbox outbox, string receptor, Guid id, Type commandType) {
    await Assert.That(PolicyProbe.Count(receptor, id)).IsEqualTo(1)
      .Because("the service handles its own command here, exactly once");
    await Assert.That(outbox.QueuedOf(typeof(PbOwnedTypedFirstEvent))).Count().IsEqualTo(1)
      .Because("the receptor's result cascades as it does on LocalInvokeAsync");
    await Assert.That(outbox.QueuedOf(commandType)).IsEmpty()
      .Because("an owned command never leaves its service (#1403)");
  }

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
