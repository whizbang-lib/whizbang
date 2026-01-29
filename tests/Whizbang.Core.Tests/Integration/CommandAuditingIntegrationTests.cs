using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Integration;

/// <summary>
/// Integration tests for command auditing flow.
/// Verifies that commands processed by receptors are audited correctly.
/// </summary>
[Category("Integration")]
public class CommandAuditingIntegrationTests {
  // Test commands (present tense - imperative)
  public sealed record CreateOrder(Guid CustomerId, decimal Amount);
  public sealed record ProcessPayment(Guid OrderId, string PaymentMethod);
  public sealed record CancelOrder(Guid OrderId, string Reason);

  // Test responses (past tense - events) - unique names to avoid generator conflicts
  public sealed record CmdAuditTestOrderCreated([property: StreamKey] Guid OrderId, Guid CustomerId) : IEvent;
  public sealed record CmdAuditTestPaymentProcessed([property: StreamKey] Guid PaymentId, Guid OrderId) : IEvent;
  public sealed record CmdAuditTestOrderCancelled([property: StreamKey] Guid OrderId, DateTimeOffset CancelledAt) : IEvent;

  [Test]
  public async Task CommandAuditing_WhenEnabled_EmitsCommandAudited_Async() {
    // Arrange
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var command = new CreateOrder(Guid.NewGuid(), 99.99m);
    var response = new CmdAuditTestOrderCreated(Guid.NewGuid(), command.CustomerId);

    // Act
    await emitter.EmitCommandAuditedAsync(
        command,
        response,
        receptorName: "OrderReceptor",
        context: null,
        cancellationToken: default);

    // Assert
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(1);

    var audited = capturedEvents.Events[0] as CommandAudited;
    await Assert.That(audited).IsNotNull();
    await Assert.That(audited!.CommandType).IsEqualTo("CreateOrder");
    await Assert.That(audited.ReceptorName).IsEqualTo("OrderReceptor");
    await Assert.That(audited.ResponseType).IsEqualTo("CmdAuditTestOrderCreated");
  }

  [Test]
  public async Task CommandAuditing_WhenDisabled_DoesNotEmitCommandAudited_Async() {
    // Arrange - command audit NOT enabled
    var services = _createServices(_ => { });
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var command = new CreateOrder(Guid.NewGuid(), 99.99m);
    var response = new CmdAuditTestOrderCreated(Guid.NewGuid(), command.CustomerId);

    // Act
    await emitter.EmitCommandAuditedAsync(command, response, "OrderReceptor", null, default);

    // Assert - no events captured
    await Assert.That(capturedEvents.Events).IsEmpty();
  }

  [Test]
  public async Task CommandAuditing_OnlyCommandAuditEnabled_DoesNotAffectEventAudit_Async() {
    // Arrange - only command audit enabled, not event audit
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Try to emit event audit (should be skipped)
    var envelope = new MessageEnvelope<CmdAuditTestOrderCreated> {
      MessageId = MessageId.New(),
      Payload = new CmdAuditTestOrderCreated(Guid.NewGuid(), Guid.NewGuid()),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, default);

    // Assert - no events because event audit is disabled
    await Assert.That(capturedEvents.Events).IsEmpty();
  }

  [Test]
  public async Task CommandAuditing_MultipleCommands_EmitsAuditForEach_Async() {
    // Arrange
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Act - process 3 different commands
    await emitter.EmitCommandAuditedAsync(
        new CreateOrder(Guid.NewGuid(), 100m),
        new CmdAuditTestOrderCreated(Guid.NewGuid(), Guid.NewGuid()),
        "OrderReceptor", null, default);

    await emitter.EmitCommandAuditedAsync(
        new ProcessPayment(Guid.NewGuid(), "CreditCard"),
        new CmdAuditTestPaymentProcessed(Guid.NewGuid(), Guid.NewGuid()),
        "PaymentReceptor", null, default);

    await emitter.EmitCommandAuditedAsync(
        new CancelOrder(Guid.NewGuid(), "Customer request"),
        new CmdAuditTestOrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow),
        "OrderReceptor", null, default);

    // Assert - 3 audit events
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(3);

    var commandTypes = capturedEvents.Events
        .Cast<CommandAudited>()
        .Select(e => e.CommandType)
        .ToList();

    await Assert.That(commandTypes).Contains("CreateOrder");
    await Assert.That(commandTypes).Contains("ProcessPayment");
    await Assert.That(commandTypes).Contains("CancelOrder");
  }

  [Test]
  public async Task CommandAuditing_SerializesCommandBody_Async() {
    // Arrange
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var customerId = Guid.NewGuid();
    var command = new CreateOrder(customerId, 149.99m);
    var response = new CmdAuditTestOrderCreated(Guid.NewGuid(), customerId);

    // Act
    await emitter.EmitCommandAuditedAsync(command, response, "OrderReceptor", null, default);

    // Assert - verify command body is serialized
    var audited = capturedEvents.Events[0] as CommandAudited;
    await Assert.That(audited).IsNotNull();

    var bodyJson = audited!.CommandBody.GetRawText();
    await Assert.That(bodyJson).Contains(customerId.ToString());
    await Assert.That(bodyJson).Contains("149.99");
  }

  [Test]
  public async Task CommandAuditing_CapturesReceptorName_Async() {
    // Arrange
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Act - different receptors
    await emitter.EmitCommandAuditedAsync(
        new CreateOrder(Guid.NewGuid(), 100m),
        new CmdAuditTestOrderCreated(Guid.NewGuid(), Guid.NewGuid()),
        "MyApp.Receptors.OrderReceptor", null, default);

    await emitter.EmitCommandAuditedAsync(
        new ProcessPayment(Guid.NewGuid(), "PayPal"),
        new CmdAuditTestPaymentProcessed(Guid.NewGuid(), Guid.NewGuid()),
        "MyApp.Receptors.PaymentReceptor", null, default);

    // Assert
    var receptorNames = capturedEvents.Events
        .Cast<CommandAudited>()
        .Select(e => e.ReceptorName)
        .ToList();

    await Assert.That(receptorNames).Contains("MyApp.Receptors.OrderReceptor");
    await Assert.That(receptorNames).Contains("MyApp.Receptors.PaymentReceptor");
  }

  [Test]
  public async Task CommandAuditing_CapturesResponseType_Async() {
    // Arrange
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Act
    await emitter.EmitCommandAuditedAsync(
        new CancelOrder(Guid.NewGuid(), "Out of stock"),
        new CmdAuditTestOrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow),
        "OrderReceptor", null, default);

    // Assert
    var audited = capturedEvents.Events[0] as CommandAudited;
    await Assert.That(audited!.ResponseType).IsEqualTo("CmdAuditTestOrderCancelled");
  }

  [Test]
  public async Task CommandAuditing_EnableAudit_EnablesBothEventAndCommand_Async() {
    // Arrange - EnableAudit() should enable both
    var services = _createServices(options => options.EnableAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Act - emit both command and event audits
    await emitter.EmitCommandAuditedAsync(
        new CreateOrder(Guid.NewGuid(), 100m),
        new CmdAuditTestOrderCreated(Guid.NewGuid(), Guid.NewGuid()),
        "OrderReceptor", null, default);

    var envelope = new MessageEnvelope<CmdAuditTestOrderCreated> {
      MessageId = MessageId.New(),
      Payload = new CmdAuditTestOrderCreated(Guid.NewGuid(), Guid.NewGuid()),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, default);

    // Assert - both should be captured
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(2);
    await Assert.That(capturedEvents.Events.OfType<CommandAudited>().Count()).IsEqualTo(1);
    await Assert.That(capturedEvents.Events.OfType<EventAudited>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task CommandAuditing_VoidResponse_HandlesGracefully_Async() {
    // Arrange - Some commands return void/Unit
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Act - command with no meaningful response
    await emitter.EmitCommandAuditedAsync(
        new CancelOrder(Guid.NewGuid(), "Test"),
        (object?)null!,  // Void response
        "OrderReceptor", null, default);

    // Assert - should still audit
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(1);
  }

  [Test]
  public async Task CommandAuditing_SetsTimestamp_Async() {
    // Arrange
    var services = _createServices(options => options.EnableCommandAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var before = DateTimeOffset.UtcNow;

    // Act
    await emitter.EmitCommandAuditedAsync(
        new CreateOrder(Guid.NewGuid(), 100m),
        new CmdAuditTestOrderCreated(Guid.NewGuid(), Guid.NewGuid()),
        "OrderReceptor", null, default);

    var after = DateTimeOffset.UtcNow;

    // Assert
    var audited = capturedEvents.Events[0] as CommandAudited;
    await Assert.That(audited!.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(audited.Timestamp).IsLessThanOrEqualTo(after);
  }

  // Helper methods

  private static ServiceProvider _createServices(Action<SystemEventOptions> configureOptions) {
    var services = new ServiceCollection();

    // Configure options
    var options = new SystemEventOptions();
    configureOptions(options);
    services.AddSingleton(Options.Create(options));

    // Add captured events for testing
    services.AddSingleton<CapturedSystemEvents>();

    // Add the system event emitter
    services.AddSingleton<ISystemEventEmitter, TestableSystemEventEmitter>();

    return services.BuildServiceProvider();
  }
}
