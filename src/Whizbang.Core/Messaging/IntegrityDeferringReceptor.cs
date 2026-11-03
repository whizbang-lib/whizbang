// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The registration form of every integrity receptor (#2001): invoked anywhere other than the maintainer's drain
/// (inbox, outbox or local dispatch), it records the message as an integrity delivery and returns; inside the drain it
/// runs the receptor it wraps.
/// </summary>
/// <remarks>
/// <para>
/// Integrity work on the receive side (checking a received checkpoint, answering or comparing a manifest, answering a
/// redelivery request) used to run on whichever instance leased the message, alongside that instance's ordinary
/// traffic. Recorded here, it is drained by the instance holding the maintainer duty, under the housekeeping idle gate.
/// </para>
/// <para>
/// The delivery's identity is derived from its type and payload bytes (<see cref="IntegrityDeliveryId"/>), so the
/// inbox, outbox and local copies of one message, and a retried delivery, are recorded once. Three cases keep the old
/// inline behavior, because recording a delivery there would lose it: no drain registered on this host (no
/// <see cref="IntegrityDeliveryRegistry"/>), no coordinator in scope, or a coordinator that cannot record deliveries.
/// </para>
/// <para>
/// Never constructed directly: <see cref="IntegrityDeliveryRegistration.RegisterIntegrityReceptor{TMessage}"/> registers
/// it at every stage and gives the drain its handler, so a receptor cannot be deferred at one stage and inline at
/// another.
/// </para>
/// </remarks>
/// <typeparam name="TMessage">The integrity message.</typeparam>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs</tests>
public sealed class IntegrityDeferringReceptor<TMessage> : IReceptor<TMessage> where TMessage : IMessage {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IntegrityDeliveryRegistry? _deliveries;

  internal IntegrityDeferringReceptor(
      IReceptor<TMessage> inner, IServiceScopeFactory scopeFactory, IntegrityDeliveryRegistry? deliveries) {
    Inner = inner;
    _scopeFactory = scopeFactory;
    _deliveries = deliveries;
    DeliveryType = TypeNameFormatter.FormatClrTypeName(typeof(TMessage));
  }

  /// <summary>The receptor that does the work, inside the drain.</summary>
  public IReceptor<TMessage> Inner { get; }

  /// <summary>The delivery type this receptor records its messages under.</summary>
  public string DeliveryType { get; }

  /// <inheritdoc />
  public async ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    if (IntegrityDrainContext.IsActive || _deliveries is null) {
      await Inner.HandleAsync(message, cancellationToken).ConfigureAwait(false);
      return;
    }
    if (!await _tryRecordAsync(message, cancellationToken).ConfigureAwait(false)) {
      await Inner.HandleAsync(message, cancellationToken).ConfigureAwait(false);
    }
  }

  // Whether the delivery is now recorded (by this call or an earlier copy); false = run inline.
  private async Task<bool> _tryRecordAsync(TMessage message, CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return false;
    }
    var payload = JsonSerializer.SerializeToUtf8Bytes(message, RegisteredJson.TypeInfo<TMessage>());
    var result = await coordinator.EnqueueIntegrityDeliveryAsync(
      IntegrityDeliveryId.For(DeliveryType, payload), DeliveryType, Encoding.UTF8.GetString(payload), cancellationToken)
      .ConfigureAwait(false);
    if (result == IntegrityDeliveryEnqueueResult.Recorded) {
      scope.ServiceProvider.GetService<StreamIntegrityMetrics>()?.IntegrityDeliveriesQueued.Add(1,
        new KeyValuePair<string, object?>("type", DeliveryType));
    }
    return result != IntegrityDeliveryEnqueueResult.Unsupported;
  }

  /// <summary>The drain's handler: reads the recorded message back and runs the wrapped receptor.</summary>
  internal async Task HandleDeferredAsync(string payload, IServiceProvider services, CancellationToken cancellationToken) {
    var message = JsonSerializer.Deserialize(payload, RegisteredJson.TypeInfo<TMessage>())
      ?? throw new JsonException($"The recorded {DeliveryType} delivery read back as null.");
    await Inner.HandleAsync(message, cancellationToken).ConfigureAwait(false);
  }
}
