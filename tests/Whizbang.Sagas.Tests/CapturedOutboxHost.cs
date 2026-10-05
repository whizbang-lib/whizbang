// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Tests.Generators;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// A host built on this test assembly's generated dispatcher whose outbox is kept in memory, so a test
/// can read the rows a publish wrote exactly as the work coordinator would store them.
/// </summary>
/// <remarks>
/// Each stored row keeps what the envelope carried at the moment it was serialized, receptor
/// invocation records included, because that is all the stages after the outbox ever see.
/// </remarks>
internal sealed class CapturedOutboxHost : IAsyncDisposable {

  private CapturedOutboxHost(ServiceProvider provider, CapturingStrategy outbox, InMemoryClaims claims) {
    Provider = provider;
    Outbox = outbox;
    Claims = claims;
  }

  public ServiceProvider Provider { get; }

  /// <summary>The rows every publish queued, in order.</summary>
  public CapturingStrategy Outbox { get; }

  /// <summary>The claim store <c>PublishOnceAsync</c> claims through.</summary>
  public InMemoryClaims Claims { get; }

  public IDispatcher Dispatcher => Provider.GetRequiredService<IDispatcher>();

  public static CapturedOutboxHost Create(Action<IServiceCollection>? configure = null) {
    var outbox = new CapturingStrategy();
    var claims = new InMemoryClaims();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer>(outbox);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IClaimedEmissionStore>(claims);
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddReceptors(services);
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddWhizbangReceptorRegistry(services);
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddWhizbangDispatcher(services);
    // The assembly's [Saga]-declared sagas have generated receivers the dispatcher resolves for every
    // tick it publishes locally; each filters on its saga name, so a tick for another saga is left alone.
    services.AddScoped<Whizbang.Sagas.Services.ISagaEventEmitter, Whizbang.Sagas.Services.DispatcherSagaEventEmitter>();
    services.AddGeneratorTestDefaultSaga();
    services.AddGeneratorTestCustomBaseSaga();
    services.AddGeneratorTestChainedSaga();
    configure?.Invoke(services);
    return new CapturedOutboxHost(services.BuildServiceProvider(), outbox, claims);
  }

  public ValueTask DisposeAsync() => Provider.DisposeAsync();

  /// <summary>One queued row, with what its envelope carried when it was serialized.</summary>
  internal sealed record StoredRow(
    OutboxMessage Message,
    object Payload,
    IReadOnlyList<ReceptorInvocationRecord> ReceptorInvocations,
    IReadOnlyList<MessageHop> Hops) {

    /// <summary>
    /// The envelope a later stage handles for this row: the payload and the records the stored row
    /// carries, and nothing added to the original envelope after it was stored.
    /// </summary>
    public MessageEnvelope<TMessage> Rehydrate<TMessage>() where TMessage : notnull => new() {
      MessageId = Whizbang.Core.ValueObjects.MessageId.From(Message.MessageId),
      Payload = (TMessage)Payload,
      Hops = [.. Hops],
      ReceptorInvocations = ReceptorInvocations.Count == 0 ? null : [.. ReceptorInvocations],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
  }

  /// <summary>Queues rows in memory and serializes envelopes by keeping a snapshot of them.</summary>
  internal sealed class CapturingStrategy : IWorkCoordinatorStrategy, IEnvelopeSerializer {
    private readonly Lock _lock = new();
    private readonly List<StoredRow> _rows = [];
    private readonly Dictionary<Guid, (object Payload, IReadOnlyList<ReceptorInvocationRecord> Invocations, IReadOnlyList<MessageHop> Hops)> _snapshots = [];

    public IReadOnlyList<StoredRow> Rows {
      get {
        lock (_lock) {
          return [.. _rows];
        }
      }
    }

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      lock (_lock) {
        _snapshots[envelope.MessageId.Value] = (
          envelope.Payload!,
          envelope.ReceptorInvocations is null ? [] : [.. envelope.ReceptorInvocations],
          [.. envelope.Hops]);
      }
      var json = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = envelope.DispatchContext,
      };
      return new SerializedEnvelope(
        json,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException("Rows are read back through StoredRow.Rehydrate.");

    public void QueueOutboxMessage(OutboxMessage message) {
      lock (_lock) {
        var (payload, invocations, hops) = _snapshots[message.MessageId];
        _rows.Add(new StoredRow(message, payload, invocations, hops));
      }
    }

    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  /// <summary>Claims keys as the claim table does: the first caller of a key wins until it is released.</summary>
  internal sealed class InMemoryClaims : IClaimedEmissionStore {
    private readonly Lock _lock = new();
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      lock (_lock) {
        return Task.FromResult(_claimed.Add(claimKey));
      }
    }
  }
}
