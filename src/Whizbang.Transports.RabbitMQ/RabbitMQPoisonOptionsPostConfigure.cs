using Microsoft.Extensions.Options;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Feeds this transport's OWN delivery cap into the Core poison detector's threshold derivation
/// (topology arc phase 8.5), so the age default moves with the transport's configuration instead
/// of being a magic number.
/// <para>
/// RabbitMQ deliberately supplies NO lock-renewal term: a classic queue has no per-delivery lock
/// to renew, so there is no honest transport-side value for it and the framework default carries
/// that half of the derivation. Only blanks are filled — an explicitly configured value wins
/// regardless of registration order.
/// </para>
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQPoisonQuarantineTests.cs:PostConfigure_FillsTheDeliveryCapFromTheTransportOptionsAsync</tests>
internal sealed class RabbitMQPoisonOptionsPostConfigure(IOptions<RabbitMQOptions> transportOptions)
  : IPostConfigureOptions<PoisonMessageOptions> {

  public void PostConfigure(string? name, PoisonMessageOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    options.MaxDeliveryAttempts ??= transportOptions.Value.MaxDeliveryAttempts;
  }
}
