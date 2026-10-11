// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The outbox bulk-flush callback built by <see cref="WorkerPipelineExtensions"/> in a container that
/// offers neither a logger factory nor tracing options: lifecycle tracing falls back to off, and a
/// failing lifecycle stage on either side of the store is absorbed with nothing to log it to, without
/// costing the store.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
[Category("Workers")]
public class WorkerPipelineExtensionsBranchCoverageTests {

  /// <summary>
  /// Delegates to a real container but answers the two optional observability services with null,
  /// as a container without logging or options configured would.
  /// </summary>
  private sealed class NoObservabilityProvider(IServiceProvider inner) : IServiceProvider {
    public object? GetService(Type serviceType) =>
      serviceType == typeof(ILoggerFactory) || serviceType == typeof(IOptionsMonitor<TracingOptions>)
        ? null
        : inner.GetService(serviceType);
  }

  private sealed class ThrowingLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");
  }

  private static OutboxMessage _buildOutboxMessage() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    return new OutboxMessage {
      MessageId = messageId.Value,
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.BranchFlushTestEvent, Whizbang.Core.Component.Tests]], Whizbang.Core",
      MessageType = "Whizbang.Core.Tests.Workers.BranchFlushTestEvent",
    };
  }

  [Test]
  public async Task OutboxBulkFlushCallback_NoLoggerFactoryOrTracingOptions_LifecycleFailuresStillStoreAsync() {
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ILifecycleMessageDeserializer>(new ThrowingLifecycleMessageDeserializer());
    // The callback first awaits the schema gate; an already-ready one lets it reach the store.
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    // The inline lifecycle stages deserialize only where a receptor invoker exists, so the throwing
    // deserializer is reached only with one registered.
    services.AddSingleton<IReceptorInvoker>(new NullReceptorInvoker());
    var descriptor = services.Last(d => d.ServiceType == typeof(OutboxBulkFlushCallback));
    await using var provider = services.BuildServiceProvider();

    // The registration's own factory, run against a container with no logging or tracing options.
    var callback = (OutboxBulkFlushCallback)descriptor.ImplementationFactory!(new NoObservabilityProvider(provider));
    var messages = new[] { _buildOutboxMessage() };

    await callback(messages, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("a lifecycle failure with no logger to report it to must still be absorbed before the store, or the batch is lost");
    await Assert.That(coordinator.StoredOutboxMessages).Count().IsEqualTo(1);
  }
}
