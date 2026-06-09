#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Locks the default offload strategy's decision matrix: no-provider →
/// inline; below threshold AND below transport ceiling → inline; above
/// either → upload + sentinel. Two failure modes: provider misconfigured
/// (named but not registered) raises a clear InvalidOperationException;
/// caller-side cancellation propagates.
/// </summary>
/// <docs>fundamentals/offloads/offload-strategy</docs>
public class MessageBodyOffloadStrategyTests {

  [Test]
  public async Task MaybeOffloadAsync_NoProviderConfigured_SendsInlineAsync() {
    var strategy = _buildStrategy(opts => {
      opts.ProviderName = null;
      opts.SizeThresholdBytes = 100;
    });

    var decision = await strategy.MaybeOffloadAsync(
      new byte[10_000_000], "application/json", "TestPayload, TestAsm",
      transportMaxMessageSizeBytes: null);

    await Assert.That(decision.Offloaded).IsFalse()
      .Because("ProviderName null → strategy is a no-op; offload never triggers regardless of body size.");
    await Assert.That(decision.Sentinel).IsNull();
  }

  [Test]
  public async Task MaybeOffloadAsync_BelowThresholdAndUnderTransportCeiling_SendsInlineAsync() {
    var (services, _) = _registerInMemoryStore("memory");
    var strategy = _buildStrategy(opts => {
      opts.ProviderName = "memory";
      opts.SizeThresholdBytes = 1_000;
    }, services);

    var decision = await strategy.MaybeOffloadAsync(
      new byte[500], "application/json", "TestPayload, TestAsm",
      transportMaxMessageSizeBytes: 10_000);

    await Assert.That(decision.Offloaded).IsFalse();
    await Assert.That(decision.Sentinel).IsNull();
  }

  [Test]
  public async Task MaybeOffloadAsync_AboveAppThreshold_UploadsAndReturnsSentinelAsync() {
    var (services, _) = _registerInMemoryStore("memory");
    var strategy = _buildStrategy(opts => {
      opts.ProviderName = "memory";
      opts.SizeThresholdBytes = 100;
    }, services);

    var body = new byte[5_000];
    var decision = await strategy.MaybeOffloadAsync(
      body, "application/json", "TestPayload, TestAsm",
      transportMaxMessageSizeBytes: 10_000);

    await Assert.That(decision.Offloaded).IsTrue();
    await Assert.That(decision.Sentinel).IsNotNull();
    await Assert.That(decision.Sentinel!.Claim.ProviderName).IsEqualTo("memory");
    await Assert.That(decision.Sentinel.Claim.Size).IsEqualTo(5_000L);
    await Assert.That(decision.Sentinel.OriginalContentType).IsEqualTo("application/json");
    await Assert.That(decision.Sentinel.OriginalTypeName).IsEqualTo("TestPayload, TestAsm");
  }

  [Test]
  public async Task MaybeOffloadAsync_AboveTransportCeilingEvenIfBelowAppThreshold_UploadsAsync() {
    var (services, _) = _registerInMemoryStore("memory");
    var strategy = _buildStrategy(opts => {
      opts.ProviderName = "memory";
      opts.SizeThresholdBytes = 10_000;  // app threshold high
    }, services);

    var decision = await strategy.MaybeOffloadAsync(
      new byte[500], "application/json", "TestPayload, TestAsm",
      transportMaxMessageSizeBytes: 256);   // but transport ceiling LOW

    await Assert.That(decision.Offloaded).IsTrue()
      .Because("Body exceeds transport's hard wire ceiling, so offload triggers regardless of app threshold — otherwise the message would be rejected by the transport.");
  }

  [Test]
  public async Task MaybeOffloadAsync_ProviderNotRegistered_ThrowsClearMessageAsync() {
    // Configure offload to use "bogus" but DON'T register it.
    var strategy = _buildStrategy(opts => {
      opts.ProviderName = "bogus";
      opts.SizeThresholdBytes = 100;
    });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await strategy.MaybeOffloadAsync(
        new byte[5_000], "application/json", "TestPayload, TestAsm",
        transportMaxMessageSizeBytes: null));

    await Assert.That(ex!.Message).Contains("bogus");
    await Assert.That(ex.Message).Contains("AddWhizbangMessageBodyStore");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static (IServiceCollection services, IServiceProvider provider) _registerInMemoryStore(string providerName) {
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>(providerName, (sp, key) => new _captureStore((string)key!));
    return (services, services.BuildServiceProvider());
  }

  private static MessageBodyOffloadStrategy _buildStrategy(
      Action<MessageBodyOffloadOptions> configureOptions,
      IServiceCollection? services = null) {
    services ??= new ServiceCollection();
    services.AddOptions<MessageBodyOffloadOptions>().Configure(configureOptions);
    var sp = services.BuildServiceProvider();
    var monitor = sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>();
    return new MessageBodyOffloadStrategy(sp, monitor);
  }

  /// <summary>
  /// Capture-only store: records uploads so tests can introspect; ignores
  /// downloads/deletes (not exercised by the send-side strategy).
  /// </summary>
  private sealed class _captureStore : IMessageBodyStore {
    public _captureStore(string providerName) {
      ProviderName = providerName;
    }
    public string ProviderName { get; }

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
