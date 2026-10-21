// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Observability;

/// <summary>Samples the event store by event type, on one instance of the fleet per window.</summary>
public sealed partial class EventTypeBytesSampler(
    IServiceScopeFactory scopeFactory,
    TableStatisticsMetrics metrics,
    IOptions<TableStatisticsOptions> options,
    Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate,
    ILogger<EventTypeBytesSampler>? logger,
    TimeProvider? timeProvider = null) : BackgroundService {

  /// <summary>Raised after each sample, with whether this instance took the window.</summary>
  public event Action<bool>? SampleCompleted;

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
