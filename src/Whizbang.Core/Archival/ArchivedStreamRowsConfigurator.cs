// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Archival;

/// <summary>
/// Applies <see cref="ArchivedStreamRowsOptions"/> to <see cref="ArchivedStreamRowPolicies"/> when the host starts (#1739),
/// so the generated runner's probe for a missing row sees a configured policy as well as a declared one. Registered by
/// the worker pipeline; it does no database work and is inert when nothing is configured.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Archival/ArchivedStreamRowsConfiguratorTests.cs</tests>
public sealed class ArchivedStreamRowsConfigurator(IOptions<ArchivedStreamRowsOptions> options) : IHostedService {
  private readonly IOptions<ArchivedStreamRowsOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    ArchivedStreamRowPolicies.ApplyRuntimeConfiguration(_options.Value.Perspectives);
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
