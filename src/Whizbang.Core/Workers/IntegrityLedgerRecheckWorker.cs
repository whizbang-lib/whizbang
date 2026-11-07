// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The idle ledger re-check (#2115): when the service is idle, re-compares the buckets of ledger rows whose report has
/// gone stale, oldest report first, instead of leaving them for the daily audit.
/// </summary>
/// <remarks>
/// <para>
/// A ledger row clears only when its bucket is compared again, and the routine audit compares a bucket at most once a
/// day. Rows written under an older compare rule (an upgrade that changed the rule) therefore lingered for days, and
/// with automatic repair on, the paced drain turned them into repair requests.
/// </para>
/// <para>
/// Each tick, on the maintainer instance and only once the housekeeping idle gate admits integrity work, claims up to
/// <see cref="StreamIntegrityOptions.LedgerRecheckRowsPerTick"/> rows last reported more than
/// <see cref="StreamIntegrityOptions.LedgerRecheckAfterMinutes"/> ago (oldest first), and asks each row's owner for a
/// stream-level answer over the row's window, marked <see cref="RequestIntegrityManifest.Recheck"/>: rows of one origin,
/// type and window share one request. The answer is compared like any other (a matching or undecided bucket is removed
/// exactly as the audit removes it), but a re-check never asks for a repair. When no row is that old, a tick claims
/// nothing and sends nothing.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/LedgerRecheckWorkerTests.cs</tests>
public sealed partial class IntegrityLedgerRecheckWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StreamIntegrityOptions> options,
    ILogger<IntegrityLedgerRecheckWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<IntegrityLedgerRecheckWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) => RecheckTickAsync(_time.GetUtcNow(), stoppingToken);

  /// <summary>One re-check tick (not yet implemented).</summary>
  internal Task RecheckTickAsync(DateTimeOffset now, CancellationToken cancellationToken) {
    _ = (_scopeFactory, _options, _logger, now, cancellationToken);
    return Task.CompletedTask;
  }
}
