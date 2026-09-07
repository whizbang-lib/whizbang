using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// Coverage-round-23 targets for <see cref="BodyOffloadPostSerializeHook"/>: the <see cref="BodyOffloadPostSerializeHook.Order"/>
/// property, the ledger-insert-failure warning log, and the hand-rolled JSON string escaping in
/// <c>_jsonQuote</c>.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public class BodyOffloadPostSerializeHookCoverageTests {

  // Hooks run in Order-ascending sequence: offload must run at 1000 so simpler hooks (size
  // measurement, compression) still see the ORIGINAL bytes, and encryption/signing (2000+) runs
  // AFTER offload so the small claim envelope is covered too. A wrong Order silently reorders the
  // whole publish pipeline — e.g. compression would measure/compress the claim ticket instead of
  // the real payload.
  [Test]
  public async Task Order_ReturnsClaimCheckPriorityAsync() {
    var services = new ServiceCollection();
    services.AddOptions<MessageBodyOffloadOptions>();
    var sp = services.BuildServiceProvider();
    var hook = new BodyOffloadPostSerializeHook(
      sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());

    await Assert.That(hook.Order).IsEqualTo(1000)
      .Because("1000 is the documented claim-check priority — simpler hooks run before it, "
        + "encryption/signing (2000+) runs after it so the claim envelope is protected too");
  }

  // Bookkeeping must never block dispatch, but a failed ledger insert still has to be visible to
  // an operator: without the warning, an orphaned blob (uploaded, but with no ledger row) leaves
  // no trail pointing anyone at the provider-side backstop that is supposed to reclaim it.
  [Test]
  public async Task RunAsync_LedgerInsertThrows_LogsWarningWithStorageKeyAndProviderAsync() {
    var services = new ServiceCollection();
    var store = new _capturingStore("memory");
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => store);
    services.AddOptions<MessageBodyOffloadOptions>().Configure(opts => {
      opts.ProviderName = "memory";
      opts.SizeThresholdBytes = 100;
    });
    services.AddSingleton<Whizbang.Core.Messaging.IWorkCoordinator>(new _throwingCoordinator());
    var logger = new _capturingLogger();
    services.AddSingleton<ILogger<BodyOffloadPostSerializeHook>>(logger);
    var sp = services.BuildServiceProvider();
    var hook = new BodyOffloadPostSerializeHook(
      sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());
    var ctx = _buildContext(new byte[5_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    await Assert.That(result.NewSerializedBytes).IsNotNull()
      .Because("the offload itself must still succeed even though bookkeeping failed");
    await Assert.That(logger.Warnings.Count).IsEqualTo(1)
      .Because("exactly one warning per failed insert — not silent, not a retry storm");
    await Assert.That(logger.Warnings[0]).Contains(store.LastStorageKey!)
      .Because("an operator needs the storage key to find the orphaned blob without a ledger row");
    await Assert.That(logger.Warnings[0]).Contains("memory")
      .Because("the provider name is required to resolve which IMessageBodyStore holds the blob");
  }

  // _jsonQuote hand-rolls JSON string escaping (to avoid a reflection-based
  // JsonSerializer.Serialize<string> call, which trips IL2026 under AOT). A wrong escape corrupts
  // the whizbang.body-store metadata header, and the receiver either fails to parse it or resolves
  // the wrong IMessageBodyStore — silently rehydrating from (or failing to find) the wrong blob.
  [Test]
  public async Task RunAsync_ProviderNameWithEscapableCharacters_RoundTripsThroughMetadataAsync() {
    var weirdProviderName = "back\\slash\"quote\nnewline\rcr\ttab\u0001ctrl";
    var services = new ServiceCollection();
    var store = new _capturingStore(weirdProviderName);
    services.AddKeyedSingleton<IMessageBodyStore>(weirdProviderName, (_, _) => store);
    services.AddOptions<MessageBodyOffloadOptions>().Configure(opts => {
      opts.ProviderName = weirdProviderName;
      opts.SizeThresholdBytes = 100;
    });
    var sp = services.BuildServiceProvider();
    var hook = new BodyOffloadPostSerializeHook(
      sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>());
    var ctx = _buildContext(new byte[5_000]);

    var result = await hook.RunAsync(ctx, CancellationToken.None);

    var meta = result.AdditionalDestinationMetadata!;
    await Assert.That(meta[BodyOffloadPostSerializeHook.BODY_STORE_METADATA_KEY].GetString())
      .IsEqualTo(weirdProviderName)
      .Because("every escapable character (backslash, quote, newline, CR, tab, and a raw "
        + "control character) must decode back to the exact original string — a missing case "
        + "either breaks JSON parsing or silently truncates/corrupts the provider name header");
  }

  private static PostSerializeContext _buildContext(byte[] bytes) {
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
      TransportMaxMessageSizeBytes: null,
      JsonOptions: jsonOptions,
      Destination: new TransportDestination("test")
    );
  }

  private sealed record _testPayload(string Content);

  /// <summary>Records the storage key minted for the last upload, for warning-message assertions.</summary>
  private sealed class _capturingStore(string providerName) : IMessageBodyStore {
    public string ProviderName { get; } = providerName;
    public string? LastStorageKey { get; private set; }

    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType,
        MessageBodyUploadOptions? options = null,
        CancellationToken cancellationToken = default) {
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

  /// <summary>Every other member is the NoOp base — only the ledger insert is overridden, to fail.</summary>
  private sealed class _throwingCoordinator
      : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, Whizbang.Core.Messaging.IWorkCoordinator {
    public Task RecordOffloadClaimAsync(
        string storageKey, string providerName, CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("ledger unavailable");
    }
  }

  private sealed class _capturingLogger : ILogger<BodyOffloadPostSerializeHook> {
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }
}
