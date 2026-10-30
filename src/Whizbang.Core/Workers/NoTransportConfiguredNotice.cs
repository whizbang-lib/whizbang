// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Takes the transport consumer's place when no <see cref="ITransport"/> is configured (#1876). A host with no
/// message broker starts: this logs once, at startup, that cross-service messaging is off, and contributes no
/// wait to readiness because there are no subscriptions to wait for. Local dispatch needs no transport and is
/// unaffected. Registering a transport turns the consumer on in its place.
/// </summary>
/// <docs>messaging/transports/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWithoutATransportStartupTests.cs:Startup_WithNoTransport_LogsOnceThatCrossServiceMessagingIsOffAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWithoutATransportTests.cs:AddTransportConsumer_WithNoTransport_ReadinessDoesNotWaitForAConsumerAsync</tests>
internal sealed partial class NoTransportConfiguredNotice(ILogger<NoTransportConfiguredNotice> logger)
    : IHostedService, IStartupReadinessContributor {

  /// <summary>The readiness name the consumer it stands in for reports under.</summary>
  /// <docs>messaging/transports/transport-consumer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWithoutATransportTests.cs:AddTransportConsumer_WithNoTransport_ReadinessDoesNotWaitForAConsumerAsync</tests>
  public string ContributorName => "transport-consumer";

  /// <summary>Logs that cross-service messaging is off because no transport is configured.</summary>
  /// <docs>messaging/transports/transport-consumer</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWithoutATransportStartupTests.cs:Startup_WithNoTransport_LogsOnceThatCrossServiceMessagingIsOffAsync</tests>
  public Task StartAsync(CancellationToken cancellationToken) {
    LogCrossServiceMessagingOff(logger);
    return Task.CompletedTask;
  }

  /// <summary>Nothing to stop: no subscription was made.</summary>
  /// <docs>messaging/transports/transport-consumer</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWithoutATransportStartupTests.cs:Startup_WithNoTransport_LogsOnceThatCrossServiceMessagingIsOffAsync</tests>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <summary>Ready at once: with no transport there are no subscriptions to wait for.</summary>
  /// <docs>messaging/transports/transport-consumer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWithoutATransportTests.cs:AddTransportConsumer_WithNoTransport_ReadinessDoesNotWaitForAConsumerAsync</tests>
  public Task WaitForContributorReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Cross-service messaging is off: no transport is configured, so the transport consumer does not start "
            + "and this service receives no messages from other services. Local dispatch is unaffected. Register a "
            + "transport to turn cross-service messaging on.")]
  private static partial void LogCrossServiceMessagingOff(ILogger logger);
}
