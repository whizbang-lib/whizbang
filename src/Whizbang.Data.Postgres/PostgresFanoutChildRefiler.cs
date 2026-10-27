// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>Red-step skeleton (#1798).</summary>
public sealed class PostgresFanoutChildRefiler(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  IStreamIdExtractor streamIds,
  JsonSerializerOptions? jsonOptions = null,
  string? schema = null,
  ILogger<PostgresFanoutChildRefiler>? logger = null) : IFanoutChildRefiler {
  /// <inheritdoc />
  public Task<FanoutChildRefileReport> RefileAsync(FanoutChildRefileRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    request.Validate();
    _ = (openConnection, streamIds, jsonOptions, schema, logger);
    return Task.FromResult(new FanoutChildRefileReport(request.RepairId, request.DryRun, 0, 0, [], [], new Dictionary<string, long>()));
  }
}
