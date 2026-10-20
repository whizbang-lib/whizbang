// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Configuration;

/// <summary>Runs <see cref="UnknownConfigurationKeyCheck"/> once when the host starts.</summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Configuration/UnknownConfigurationKeyStartupValidatorTests.cs</tests>
internal sealed class UnknownConfigurationKeyStartupValidator(
    IOptions<ConfigurationCheckOptions> options,
    ILogger<UnknownConfigurationKeyStartupValidator> logger,
    IConfiguration? configuration) : IHostedService {
  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    _ = (options, logger, configuration);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
