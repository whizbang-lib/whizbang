using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Routing;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// That a lifecycle stage swallowed as "(continuing)" is logged at Error when the cause is
/// deserialization, and stays a Warning otherwise.
/// </summary>
/// <remarks>
/// The swallow already logged the exception and routed it to the failure channel. A receptor that
/// threw is a Warning: the message is retried and the receptor is the consumer's. A payload that
/// could not be deserialized is not going to deserialize on the retry either, and a Warning is
/// what nobody reads.
/// </remarks>
public partial class LifecycleExceptionInvariantTests {
  private static (InboxDispatchWorker Worker, FakeLogger<InboxDispatchWorker> Logger, ServiceProvider Provider) _inboxWorkerWithLogger() {
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new FakeLogger<InboxDispatchWorker>();
    var worker = new InboxDispatchWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new _FakeServiceInstanceProvider(),
      inboxChannelWriter: new _FakeInboxChannelWriter(),
      handlerCommitChannel: new _FakeHandlerCommitChannel(),
      failureChannel: new _FakeFailureChannel(),
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions { Enabled = true }),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: logger,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider());
    return (worker, logger, sp);
  }

  private static async Task<IReadOnlyList<FakeLogRecord>> _invokeThrowingStageAsync(Exception thrown) {
    var (worker, logger, sp) = _inboxWorkerWithLogger();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "TestMessage",
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };
    await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

    await worker.InvokeInboxLifecycleStageAsync(
      work, envelope, scope,
      new _ThrowingReceptorInvoker(thrown),
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      "PreInbox", CancellationToken.None);

    return [.. logger.Collector.GetSnapshot().Where(r => r.Message.Contains("lifecycle", StringComparison.Ordinal))];
  }

  private static JsonException _deserializationFailure() {
    try {
      JsonSerializer.Deserialize("{\"At\":true}", HolderContext.Default.Holder);
    } catch (JsonException ex) {
      return ex;
    }
    throw new InvalidOperationException("the fixture did not fail");
  }

  [Test]
  public async Task InboxDispatchWorker_LifecycleThrowsDeserialization_LogsErrorAsync() {
    var logged = await _invokeThrowingStageAsync(_deserializationFailure());

    await Assert.That(logged).Count().IsEqualTo(1);
    await Assert.That(logged[0].Level).IsEqualTo(LogLevel.Error)
      .Because("a payload that did not deserialize will not deserialize on the retry either");
    await Assert.That(logged[0].Message).Contains("PreInbox", StringComparison.Ordinal);
    await Assert.That(logged[0].Message).Contains("(continuing)", StringComparison.Ordinal);
    await Assert.That(logged[0].Exception).IsNotNull();
  }

  [Test]
  public async Task InboxDispatchWorker_LifecycleThrowsOtherwise_LogsWarningAsync() {
    var logged = await _invokeThrowingStageAsync(new InvalidOperationException("a receptor threw"));

    await Assert.That(logged).Count().IsEqualTo(1);
    await Assert.That(logged[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("a receptor that threw is the consumer's, and the message is retried");
  }
}
