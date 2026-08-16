#pragma warning disable CA1707

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Locks the body-offload hook's decision matrix and side effects:
/// pass-through when below thresholds; on offload — upload + sentinel +
/// metadata headers (whizbang.is-claim/body-store/original-type) +
/// claim envelope preserves original identity/journey.
/// </summary>
/// <docs>fundamentals/offloads/body-offload-hook</docs>
public class BodyOffloadPostSerializeHookTests {

  [Test]
  public async Task RunAsync_NoProvider_PassesThroughAsync() {
    var (hook, _) = _build(opts => { opts.ProviderName = null; opts.SizeThresholdBytes = 100; });
    var ctx = _buildContext(new byte[10_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNull();
    await Assert.That(result.AdditionalDestinationMetadata).IsNull();
  }

  [Test]
  public async Task RunAsync_BelowThresholdAndUnderCeiling_PassesThroughAsync() {
    var (hook, _) = _build(opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 10_000; });
    var ctx = _buildContext(new byte[100], transportMaxBytes: 10_000);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNull();
    await Assert.That(result.AdditionalDestinationMetadata).IsNull();
  }

  [Test]
  public async Task RunAsync_AboveThreshold_UploadsAndReplacesAsync() {
    var (hook, store) = _build(opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100; });
    var body = new byte[5_000];
    for (var i = 0; i < body.Length; i++) {
      body[i] = (byte)(i % 256);
    }
    var ctx = _buildContext(body);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNotNull();
    await Assert.That(result.NewEnvelope).IsNotNull()
      .Because("Offload replaces the envelope with a MessageEnvelope<BodyClaimEnvelopePayload>; downstream code (transports) need the typed envelope, not just bytes.");
    await Assert.That(result.NewEnvelope).IsTypeOf<MessageEnvelope<BodyClaimEnvelopePayload>>();
    await Assert.That(store.UploadCount).IsEqualTo(1)
      .Because("Exactly one upload per offload — the strategy doesn't re-upload, doesn't speculatively pre-warm.");
    await Assert.That(store.LastUploadedBody).IsEquivalentTo(body)
      .Because("The original bytes (not the claim envelope) get uploaded; receivers must rehydrate to those exact bytes.");
  }

  [Test]
  public async Task RunAsync_AboveThreshold_AdditionalMetadataHasAllThreeKeysAsync() {
    var (hook, _) = _build(opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100; });
    var ctx = _buildContext(new byte[5_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.AdditionalDestinationMetadata).IsNotNull();
    var meta = result.AdditionalDestinationMetadata!;
    await Assert.That(meta.ContainsKey(BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY)).IsTrue();
    await Assert.That(meta[BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY].GetBoolean()).IsTrue();
    await Assert.That(meta.ContainsKey(BodyOffloadPostSerializeHook.BODY_STORE_METADATA_KEY)).IsTrue();
    await Assert.That(meta[BodyOffloadPostSerializeHook.BODY_STORE_METADATA_KEY].GetString()).IsEqualTo("memory");
    await Assert.That(meta.ContainsKey(BodyOffloadPostSerializeHook.ORIGINAL_TYPE_METADATA_KEY)).IsTrue()
      .Because("Receivers need whizbang.original-type to know what envelope shape to deserialize the rehydrated bytes as.");
  }

  [Test]
  public async Task RunAsync_AboveTransportCeilingEvenIfBelowThreshold_UploadsAsync() {
    var (hook, store) = _build(opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100_000; });   // high threshold
    var ctx = _buildContext(new byte[2_000], transportMaxBytes: 1_024);                                          // but low transport ceiling

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNotNull()
      .Because("Transport ceiling forces offload regardless of app threshold — otherwise the wire-send would fail.");
    await Assert.That(store.UploadCount).IsEqualTo(1);
  }

  [Test]
  public async Task RunAsync_ProviderConfiguredButNotRegistered_ThrowsClearMessageAsync() {
    var (hook, _) = _build(opts => { opts.ProviderName = "bogus-not-registered"; opts.SizeThresholdBytes = 100; });
    var ctx = _buildContext(new byte[5_000]);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await hook.RunAsync(ctx, CancellationToken.None));

    await Assert.That(ex!.Message).Contains("bogus-not-registered");
    await Assert.That(ex.Message).Contains("AddWhizbangMessageBodyStore");
  }

  [Test]
  public async Task RunAsync_ClaimEnvelope_PreservesOriginalIdentityAsync() {
    var (hook, _) = _build(opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100; });
    var ctx = _buildContext(new byte[5_000]);
    var originalId = ctx.Envelope.MessageId;
    var originalHops = ctx.Envelope.Hops;

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    var claim = (MessageEnvelope<BodyClaimEnvelopePayload>)result.NewEnvelope!;
    await Assert.That(claim.MessageId).IsEqualTo(originalId)
      .Because("Same MessageId so dedup, audit, and tracing still work — the claim envelope IS the original message, just with the body offloaded.");
    await Assert.That(claim.Hops).IsSameReferenceAs(originalHops)
      .Because("Hops are append-only; claim envelope shares the list reference so any later hop addition is observed by both views.");
    await Assert.That(claim.DispatchContext).IsEqualTo(ctx.Envelope.DispatchContext);
  }

  [Test]
  public async Task RunAsync_AboveThreshold_EmitsOffloadMetricsWithBoundedTagsAsync() {
    // Isolated meter via TestMeterFactory — parallel-safe (no shared-meter pollution from other
    // tests' offloads, the cause of the earlier CI flake on this assertion).
    using var factory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(factory));
    var (hook, _) = _build(opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100; }, metrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var ctx = _buildContext(new byte[5_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);
    await Assert.That(result.NewSerializedBytes).IsNotNull(); // confirms offload path ran

    var counts = helper.GetByName("whizbang.transport.body_offload.count");
    await Assert.That(counts.Count).IsEqualTo(1);
    await Assert.That(counts[0].Value).IsEqualTo(1d);

    var bytes = helper.GetByName("whizbang.transport.body_offload.bytes");
    await Assert.That(bytes.Count).IsEqualTo(1);
    await Assert.That(bytes[0].Value).IsEqualTo(5_000d)
      .Because("Records the original serialized size that tripped the claim-check.");

    // Bounded dimensions only — message type + namespace, never message IDs.
    await Assert.That(counts[0].Tags.ContainsKey("message.type")).IsTrue();
    await Assert.That(counts[0].Tags.ContainsKey("message.namespace")).IsTrue();
  }

  // ============================================================
  // Helpers
  // ============================================================

  [Test]
  public async Task RunAsync_AboveThreshold_RecordsTheClaimInTheLedgerAsync() {
    // The ledger row is the database's ONLY record of the blob once the message completes (the
    // claim envelope rides wh_outbox/wh_inbox, which are deleted on completion). Without this
    // insert the passive expiry sweep has nothing to sweep and the blob lives forever unless a
    // provider-side lifecycle rule happens to exist.
    var coordinator = new _ledgerCoordinator();
    var (hook, store) = _build(
      opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100; },
      coordinator: coordinator);
    var ctx = _buildContext(new byte[5_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNotNull();
    await Assert.That(coordinator.Recorded).Count().IsEqualTo(1);
    await Assert.That(coordinator.Recorded[0].ProviderName).IsEqualTo("memory");
    await Assert.That(coordinator.Recorded[0].StorageKey).IsNotEmpty()
      .Because("the sweep resolves the keyed store per claim and deletes by storage key — a row "
        + "without the key is a row that can never be swept");
  }

  [Test]
  public async Task RunAsync_LedgerInsertThrows_OffloadStillProceedsAsync() {
    // Bookkeeping must never block dispatch: a failed ledger insert orphans one blob into the
    // provider-side backstop's territory, which is recoverable; a failed dispatch is not.
    var coordinator = new _ledgerCoordinator { ThrowOnRecord = true };
    var (hook, store) = _build(
      opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100; },
      coordinator: coordinator);
    var ctx = _buildContext(new byte[5_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNotNull();
    await Assert.That(store.UploadCount).IsEqualTo(1)
      .Because("the offload itself completed; only the ledger write failed");
  }

  [Test]
  public async Task RunAsync_PassThrough_RecordsNothingAsync() {
    var coordinator = new _ledgerCoordinator();
    var (hook, _) = _build(
      opts => { opts.ProviderName = "memory"; opts.SizeThresholdBytes = 100_000; },
      coordinator: coordinator);
    var ctx = _buildContext(new byte[100], transportMaxBytes: 100_000);

    await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(coordinator.Recorded).IsEmpty()
      .Because("a message that was never offloaded has no blob — a ledger row for it would make "
        + "the sweep issue deletes for keys that never existed");
  }

  /// <summary>Every other member is the NoOp base — only the ledger insert is observed.</summary>
  private sealed class _ledgerCoordinator : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, Whizbang.Core.Messaging.IWorkCoordinator {
    public List<(string StorageKey, string ProviderName)> Recorded { get; } = [];
    public bool ThrowOnRecord { get; init; }

    public Task RecordOffloadClaimAsync(
        string storageKey, string providerName, CancellationToken cancellationToken = default) {
      if (ThrowOnRecord) {
        throw new InvalidOperationException("ledger unavailable");
      }
      Recorded.Add((storageKey, providerName));
      return Task.CompletedTask;
    }
  }

  private static (BodyOffloadPostSerializeHook hook, _captureStore store) _build(
      Action<MessageBodyOffloadOptions> configure, TransportMetrics? metrics = null,
      Whizbang.Core.Messaging.IWorkCoordinator? coordinator = null) {
    var services = new ServiceCollection();
    var captureStore = new _captureStore("memory");
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => captureStore);
    services.AddOptions<MessageBodyOffloadOptions>().Configure(configure);
    if (coordinator is not null) {
      services.AddSingleton(coordinator);
    }
    // Only register metrics when a test supplies an isolated instance — keeps non-metric tests
    // from emitting to a shared meter (which would pollute a parallel metric test's capture).
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var sp = services.BuildServiceProvider();
    var hook = new BodyOffloadPostSerializeHook(sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());
    return (hook, captureStore);
  }

  private static PostSerializeContext _buildContext(byte[] bytes, long? transportMaxBytes = null) {
    var envelope = new MessageEnvelope<_testPayload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new _testPayload("x"),
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
      TransportMaxMessageSizeBytes: transportMaxBytes,
      JsonOptions: jsonOptions,
      Destination: new TransportDestination("test")
    );
  }

  private sealed record _testPayload(string Content);

  /// <summary>
  /// Capture-only store: records uploads so tests can introspect.
  /// </summary>
  private sealed class _captureStore : IMessageBodyStore {
    public _captureStore(string providerName) {
      ProviderName = providerName;
    }
    public string ProviderName { get; }
    public int UploadCount { get; private set; }
    public byte[] LastUploadedBody { get; private set; } = [];

    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType,
        MessageBodyUploadOptions? options = null,
        CancellationToken cancellationToken = default) {
      UploadCount++;
      LastUploadedBody = body.ToArray();
      var claim = new MessageBodyClaim(
        ProviderName: ProviderName,
        StorageKey: $"capture://{Guid.NewGuid():N}",
        Size: body.Length,
        ContentHash: "sha256-capture",
        ContentType: contentType,
        UploadedAt: DateTimeOffset.UtcNow);
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
}
