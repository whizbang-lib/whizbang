using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Default <see cref="IMessageBodyOffloadStrategy"/>. Offloads when the
/// serialized body meets BOTH thresholds: the configured
/// <see cref="MessageBodyOffloadOptions.SizeThresholdBytes"/> AND (if non-null)
/// the transport's <see cref="MessageBodyOffloadOptions.SizeThresholdBytes"/>.
/// Reads the active provider from DI by name.
/// </summary>
/// <remarks>
/// Decision matrix:
/// <list type="bullet">
///   <item>No provider configured (<see cref="MessageBodyOffloadOptions.ProviderName"/> null/empty) → never offload; large messages publish inline (and may hit transport ceilings).</item>
///   <item>Body &lt; <see cref="MessageBodyOffloadOptions.SizeThresholdBytes"/> AND body fits within <c>transportMaxMessageSizeBytes</c> → send inline.</item>
///   <item>Body ≥ threshold OR exceeds transport ceiling → upload + return sentinel.</item>
/// </list>
/// </remarks>
/// <docs>fundamentals/offloads/offload-strategy</docs>
public sealed class MessageBodyOffloadStrategy : IMessageBodyOffloadStrategy {
  private readonly IServiceProvider _serviceProvider;
  private readonly IOptionsMonitor<MessageBodyOffloadOptions> _options;

  /// <summary>
  /// Builds the strategy. Takes <see cref="IServiceProvider"/> instead of
  /// resolving the store at construction so providers can be (re)configured
  /// at runtime via IOptionsMonitor changes without rebuilding DI.
  /// </summary>
  public MessageBodyOffloadStrategy(
      IServiceProvider serviceProvider,
      IOptionsMonitor<MessageBodyOffloadOptions> options) {
    _serviceProvider = serviceProvider;
    _options = options;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Offloads/MessageBodyOffloadStrategyTests.cs</tests>
  public async Task<OffloadDecision> MaybeOffloadAsync(
      ReadOnlyMemory<byte> originalBody,
      string contentType,
      string originalTypeName,
      long? transportMaxMessageSizeBytes,
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();

    var opts = _options.CurrentValue;

    // No provider → never offload. Caller decides what to do with
    // oversized messages (may still hit transport ceilings).
    if (string.IsNullOrWhiteSpace(opts.ProviderName)) {
      return OffloadDecision.SendInline();
    }

    var size = originalBody.Length;
    var underAppThreshold = size < opts.SizeThresholdBytes;
    var underTransportCeiling = transportMaxMessageSizeBytes is not long max || size <= max;

    if (underAppThreshold && underTransportCeiling) {
      return OffloadDecision.SendInline();
    }

    var store = _serviceProvider.GetKeyedService<IMessageBodyStore>(opts.ProviderName)
      ?? throw new InvalidOperationException(
        $"MessageBodyOffloadOptions.ProviderName = '{opts.ProviderName}' but no IMessageBodyStore was registered under that key. " +
        "Register via services.AddWhizbangMessageBodyStore<T>(name) (or a typed wrapper like AddWhizbangInMemoryOffload(name)) before the strategy executes.");

    var claim = await store.UploadAsync(originalBody, contentType, options: null, cancellationToken);
    var sentinel = new BodyClaimEnvelopePayload(claim, contentType, originalTypeName);
    return OffloadDecision.Offload(sentinel);
  }
}
