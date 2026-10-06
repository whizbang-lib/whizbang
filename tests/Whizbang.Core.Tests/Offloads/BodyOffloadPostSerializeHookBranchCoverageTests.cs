// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Branch backfill for the claim-ledger step of <see cref="BodyOffloadPostSerializeHook.RunAsync"/>:
/// no coordinator registered, a cancellation from the ledger insert, and a failed insert on a host
/// with no logger registered.
/// </summary>
public class BodyOffloadPostSerializeHookBranchCoverageTests {

  [Test]
  public async Task RunAsync_NoCoordinatorRegistered_StillOffloadsAsync() {
    var store = new CapturingStore("memory");
    await using var sp = _buildProvider(store, coordinator: null);
    var hook = new BodyOffloadPostSerializeHook(sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());

    var result = await hook.RunAsync(_buildContext(new byte[5_000]), CancellationToken.None);

    await Assert.That(result.NewEnvelope).IsTypeOf<MessageEnvelope<BodyClaimEnvelopePayload>>()
      .Because("a host with no work coordinator skips the ledger insert and still emits the claim envelope");
    await Assert.That(store.UploadCount).IsEqualTo(1);
  }

  [Test]
  public async Task RunAsync_LedgerInsertCanceled_PropagatesCancellationAsync() {
    var store = new CapturingStore("memory");
    await using var sp = _buildProvider(store, new CancelingCoordinator());
    var hook = new BodyOffloadPostSerializeHook(sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());

    await Assert.That(async () => await hook.RunAsync(_buildContext(new byte[5_000]), CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("a cancellation is not a bookkeeping failure: it must propagate, not be logged and swallowed");
  }

  [Test]
  public async Task RunAsync_LedgerInsertThrows_NoLoggerRegistered_StillOffloadsAsync() {
    var store = new CapturingStore("memory");
    await using var sp = _buildProvider(store, new ThrowingCoordinator());
    var hook = new BodyOffloadPostSerializeHook(sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());

    var result = await hook.RunAsync(_buildContext(new byte[5_000]), CancellationToken.None);

    await Assert.That(result.NewEnvelope).IsTypeOf<MessageEnvelope<BodyClaimEnvelopePayload>>()
      .Because("a failed ledger insert on a host without a logger is skipped silently; the offload proceeds");
    var claimEnvelope = (MessageEnvelope<BodyClaimEnvelopePayload>)result.NewEnvelope!;
    await Assert.That(claimEnvelope.Payload.Claim.StorageKey).IsEqualTo(store.LastStorageKey);
  }

  private static ServiceProvider _buildProvider(CapturingStore store, IWorkCoordinator? coordinator) {
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>(store.ProviderName, (_, _) => store);
    services.AddOptions<MessageBodyOffloadOptions>().Configure(opts => {
      opts.ProviderName = store.ProviderName;
      opts.SizeThresholdBytes = 100;
    });
    if (coordinator is not null) {
      services.AddSingleton(coordinator);
    }
    return services.BuildServiceProvider();
  }

  private static PostSerializeContext _buildContext(byte[] bytes) {
    var envelope = new MessageEnvelope<TestPayload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new TestPayload("x"),
      Hops = [
        new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }
      ]
    };
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    return new PostSerializeContext(
      Envelope: envelope,
      EnvelopeType: envelope.GetType().AssemblyQualifiedName!,
      SerializedBytes: bytes,
      ContentType: "application/json",
      TransportMaxMessageSizeBytes: null,
      JsonOptions: jsonOptions,
      Destination: new TransportDestination("test")
    );
  }

  private sealed record TestPayload(string Content);

  private sealed class CapturingStore(string providerName) : IMessageBodyStore {
    public string ProviderName { get; } = providerName;
    public string? LastStorageKey { get; private set; }
    public int UploadCount { get; private set; }

    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType,
        MessageBodyUploadOptions? options = null,
        CancellationToken cancellationToken = default) {
      UploadCount++;
      var claim = new MessageBodyClaim(
        ProviderName: ProviderName,
        StorageKey: $"capture://{Guid.NewGuid():N}",
        Size: body.Length,
        ContentHash: "sha256-capture",
        ContentType: contentType,
        UploadedAt: DateTimeOffset.UtcNow);
      LastStorageKey = claim.StorageKey;
      return Task.FromResult(claim);
    }

    public Task<ReadOnlyMemory<byte>> DownloadAsync(
        MessageBodyClaim claim,
        MessageBodyDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
          => throw new NotImplementedException();

    public Task DeleteAsync(
        MessageBodyClaim claim,
        MessageBodyDeleteOptions? options = null,
        CancellationToken cancellationToken = default)
          => Task.CompletedTask;
  }

  private sealed class ThrowingCoordinator
      : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public Task RecordOffloadClaimAsync(
        string storageKey, string providerName, CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("ledger unavailable");
    }
  }

  private sealed class CancelingCoordinator
      : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public Task RecordOffloadClaimAsync(
        string storageKey, string providerName, CancellationToken cancellationToken = default) {
      throw new OperationCanceledException("ledger insert canceled");
    }
  }
}
