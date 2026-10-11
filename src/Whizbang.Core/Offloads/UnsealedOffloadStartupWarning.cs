// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Warns once at start when body offload names a store but no cipher, so the bodies it uploads
/// are stored in the clear.
/// </summary>
/// <remarks>
/// Encryption of offloaded bodies is opt-in (<see cref="MessageBodyOffloadOptions.CipherName"/>):
/// whoever can read the store reads every offloaded message. The warning makes that a decision a
/// deployment sees rather than a default it inherits. It reads the options as bound at start; it
/// never blocks startup.
/// </remarks>
/// <param name="options">The bound offload options.</param>
/// <param name="logger">The logger.</param>
/// <docs>fundamentals/offloads/message-body-store#sealing-bodies</docs>
/// <tests>tests/Whizbang.Core.Tests/Offloads/UnsealedOffloadStartupWarningTests.cs</tests>
public sealed partial class UnsealedOffloadStartupWarning(
    IOptions<MessageBodyOffloadOptions> options,
    ILogger<UnsealedOffloadStartupWarning> logger) : IHostedService {

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    var opts = options.Value;
    if (!string.IsNullOrWhiteSpace(opts.ProviderName) && string.IsNullOrWhiteSpace(opts.CipherName)) {
      LogUnsealedOffload(logger, opts.ProviderName);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Message bodies offloaded to store '{ProviderName}' are stored unencrypted: no Whizbang:BodyOffload:CipherName is set. Anyone who can read the store can read those messages. Register a cipher and name it to seal bodies before upload")]
  private static partial void LogUnsealedOffload(ILogger logger, string providerName);
}
