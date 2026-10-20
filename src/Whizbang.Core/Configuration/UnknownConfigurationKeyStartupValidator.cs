// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Runs <see cref="UnknownConfigurationKeyCheck"/> once when the host starts, against the keys in the
/// embedded configuration key manifest. Registered by <c>AddWhizbang</c>, so every host gets it.
/// </summary>
/// <remarks>
/// A hosted service rather than a startup-pipeline step: <see cref="UnknownConfigurationKeyMode.Throw"/>
/// must be able to stop the host, and a throw from <c>StartAsync</c> does, where the startup pipeline by
/// design never stops it. The default, <see cref="UnknownConfigurationKeyMode.Warn"/>, never fails a start.
/// </remarks>
/// <param name="options">The bound <c>Whizbang:Configuration</c> settings.</param>
/// <param name="logger">Where the warnings go.</param>
/// <param name="configuration">The host's configuration; <see langword="null"/> in a container without one.</param>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Configuration/UnknownConfigurationKeyStartupValidatorTests.cs</tests>
internal sealed class UnknownConfigurationKeyStartupValidator(
    IOptions<ConfigurationCheckOptions> options,
    ILogger<UnknownConfigurationKeyStartupValidator> logger,
    IConfiguration? configuration) : IHostedService {
  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    UnknownConfigurationKeyCheck.Run(configuration, KnownConfigurationKeys.Library, options.Value.UnknownKeys, logger);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
