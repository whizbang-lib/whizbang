// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Archival;

/// <summary>Stub (RED, #1739).</summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
public sealed class ArchivedStreamRowsConfigurator(IOptions<ArchivedStreamRowsOptions> options) : IHostedService {
  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  internal IOptions<ArchivedStreamRowsOptions> Options => options;
}
