// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Unit.Messaging.Tests.Generated;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Receptors;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The lock on <see cref="TestHandling"/> (#2609): a unit test that enters a receptor's handling and calls its
/// <c>HandleAsync</c> directly derives the same strong ids as a real handling of the same message by the same receptor,
/// run by the receptor invoker from the host's own registrations. A compiled receptor and a generated strong id, so the
/// receptor id and the derivation are the generator's, not a test's copy of them.
/// </summary>
/// <code-under-test>src/Whizbang.Testing/Receptors/TestHandling.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Messaging")]
public class TestHandlingDispatchTests {
  private const string SERVICE = "orders-service";

  [Test]
  public async Task TestHandling_DerivesTheIdARealHandlingOfTheSameEnvelopeDerivesAsync() {
    var receptor = new TestHandlingProbeReceptor();
    await using var services = _host(receptor);
    var envelope = _inboundEnvelope();

    await _dispatchAsync(services, envelope);
    using (TestHandling.Enter(services, receptor, envelope)) {
      await receptor.HandleAsync(envelope.Payload);
    }

    await Assert.That(receptor.Derived).Count().IsEqualTo(2)
      .Because("the real handling and the test's direct call each ran the receptor body once");
    await Assert.That(receptor.Derived[1]).IsEqualTo(receptor.Derived[0])
      .Because("the test handling names the message, handler, service and receptor the invoker named");
  }

  [Test]
  public async Task TestHandling_AMessage_ThenARealHandlingOfItsEnvelope_DeriveTheSameIdAsync() {
    var receptor = new TestHandlingProbeReceptor();
    await using var services = _host(receptor);
    var message = new TestHandlingProbeCommand("order-1");

    IMessageEnvelope envelope;
    await using (var scope = TestHandling.Enter(services, receptor, message)) {
      envelope = scope.Envelope;
      await receptor.HandleAsync(message);
    }
    await _dispatchAsync(services, envelope);

    await Assert.That(receptor.Derived).Count().IsEqualTo(2);
    await Assert.That(receptor.Derived[1]).IsEqualTo(receptor.Derived[0]);
  }

  [Test]
  public async Task TestHandling_InAnotherService_DerivesAnotherId_AsARealHandlingThereDoesAsync() {
    // The positive control: the equality above holds because the inputs match, not because every handling derives one id
    var receptor = new TestHandlingProbeReceptor();
    await using var orders = _host(receptor);
    await using var billing = _host(receptor, "billing-service");
    var envelope = _inboundEnvelope();

    await _dispatchAsync(orders, envelope);
    using (TestHandling.Enter(billing, receptor, envelope)) {
      await receptor.HandleAsync(envelope.Payload);
    }

    await Assert.That(receptor.Derived).Count().IsEqualTo(2);
    await Assert.That(receptor.Derived[1]).IsNotEqualTo(receptor.Derived[0]);
  }

  // The host's own registrations: the generated registry and invoker, the service's instance, the receptor in DI
  private static ServiceProvider _host(TestHandlingProbeReceptor receptor, string serviceName = SERVICE) =>
    new ServiceCollection()
      .AddWhizbangReceptorRegistry()
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.Empty, serviceName, "test-host", 1))
      .AddSingleton<IReceptor<TestHandlingProbeCommand>>(receptor)
      .BuildServiceProvider();

  private static async Task _dispatchAsync(ServiceProvider services, IMessageEnvelope envelope) {
    await using var scope = services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IReceptorInvoker>().InvokeAsync(envelope, LifecycleStage.PostInboxInline);
  }

  private static MessageEnvelope<TestHandlingProbeCommand> _inboundEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestHandlingProbeCommand("order-1"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox, HandlerName = "TestHandlingProbeCommandHandler" },
  };
}

/// <summary>The command the test-handling probe receptor handles.</summary>
public sealed record TestHandlingProbeCommand(string Name) : ICommand;

/// <summary>The strong id the test-handling probe derives.</summary>
[WhizbangId]
[PinnedId("8e3a1c5d-2b4f-4a6e-9c7d-0f1e2d3c4b5a")]
public readonly partial struct TestHandlingProbeId;

/// <summary>
/// A compiled receptor that derives a strong id in its body and records it, so a real handling and a test's direct call
/// can be compared. Fires only at PostInboxInline, so no local dispatch in this assembly runs it.
/// </summary>
[FireAt(LifecycleStage.PostInboxInline)]
public sealed class TestHandlingProbeReceptor : IReceptor<TestHandlingProbeCommand> {
  private readonly List<Guid> _derived = [];

  /// <summary>The ids the body derived, in order.</summary>
  public IReadOnlyList<Guid> Derived => _derived;

  /// <inheritdoc />
  public ValueTask HandleAsync(TestHandlingProbeCommand message, CancellationToken cancellationToken = default) {
    _derived.Add(TestHandlingProbeId.DeriveFrom(Handling.Current, "order").Value);
    return ValueTask.CompletedTask;
  }
}
