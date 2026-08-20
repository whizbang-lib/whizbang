using Microsoft.Extensions.Options;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Feeds this transport's OWN lock/delivery knobs into the Core poison detector's threshold
/// derivation (topology arc phase 8.5), so the age default is derived rather than guessed:
/// <c>MaxAutoLockRenewalDuration x MaxDeliveryAttempts</c>, clamped to the documented floor.
/// <para>
/// Only fills BLANKS. An operator who set <c>Whizbang:Routing:PoisonMessages:LockRenewalDuration</c>
/// or <c>…:MaxDeliveryAttempts</c> explicitly keeps that value regardless of registration order —
/// configuration is the operator's word, and the transport only supplies what nobody stated.
/// </para>
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbPoisonQuarantineTests.cs:PostConfigure_FillsThePoisonThresholdFromLockAndDeliveryOptionsAsync</tests>
internal sealed class AsbPoisonOptionsPostConfigure(IOptions<AzureServiceBusOptions> transportOptions)
  : IPostConfigureOptions<PoisonMessageOptions> {

  public void PostConfigure(string? name, PoisonMessageOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var transport = transportOptions.Value;
    options.LockRenewalDuration ??= transport.MaxAutoLockRenewalDuration;
    options.MaxDeliveryAttempts ??= transport.MaxDeliveryAttempts;
  }
}
