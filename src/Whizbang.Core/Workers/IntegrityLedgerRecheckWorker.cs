// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

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
/// type and window share one request. A row with no window was compared over the whole history and is asked for again
/// over the whole history: an unwindowed answer from the origin's recompute (#2208). The answer is compared like any other (a matching or undecided bucket is removed
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

  /// <summary>
  /// How the loop waits between ticks. Internal for deterministic tests: a test replaces it to release each tick on a
  /// signal it controls instead of racing a clock against the loop reaching its wait.
  /// </summary>
  internal Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; set; }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunLoopAsync(stoppingToken);

  /// <summary>
  /// The tick loop. Internal so a test can run it directly: called directly, it runs synchronously up to its first
  /// wait, so a test knows the loop is waiting without racing the host's start.
  /// </summary>
  internal async Task RunLoopAsync(CancellationToken stoppingToken) {
    var interval = TimeSpan.FromSeconds(Math.Max(1, _options.LedgerRecheckIntervalSeconds));
    while (!stoppingToken.IsCancellationRequested) {
      try {
        await (DelayAsync ?? ((wait, token) => Task.Delay(wait, _time, token)))(interval, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
      try {
        await RecheckTickAsync(_time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogTickFailed(_logger, ex);
      }
    }
  }

  /// <summary>
  /// One re-check tick: claims the stalest ledger rows and asks their owners to answer them again. Internal for
  /// deterministic tests; the loop above supplies the clock.
  /// </summary>
  /// <param name="now">The tick's instant: the threshold is measured back from it and the claimed rows are stamped with it.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  internal async Task RecheckTickAsync(DateTimeOffset now, CancellationToken cancellationToken) {
    if (!_options.LedgerRecheckEnabled || !_options.AuditEnabled) {
      return;
    }
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    if (!MaintainerDuty.IsThisInstancesTurn(services)) {
      return;   // another instance holds the maintainer duty and re-checks the ledger.
    }
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var tracker = services.GetService<IntegrityGapTracker>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var replyTopic = RepairTopicResolver.Resolve(_options.RepairTopic, services);
    if (coordinator is null || transport is null || serializer is null || tracker is null
        || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(replyTopic)) {
      return;   // nobody to ask, or no return address: the rows wait for the audit.
    }
    // Directed or not at all: a row is claimed only for an origin whose request topic a checkpoint has taught.
    var origins = tracker.GetOrigins().Where(o => !string.IsNullOrEmpty(o.RequestTopic)).ToList();
    if (origins.Count == 0) {
      return;
    }
    await using var admission = await IntegrityAdmission.TryAdmitAsync(
      services, coordinator, nameof(IntegrityLedgerRecheckWorker), _logger, cancellationToken).ConfigureAwait(false);
    if (!admission.Granted) {
      return;
    }

    var claimed = await coordinator.IntegrityClaimLedgerRecheckAsync(
      [.. origins.Select(o => o.OriginServiceId)], now - TimeSpan.FromMinutes(_options.LedgerRecheckAfterMinutes), now,
      _options.LedgerRecheckRowsPerTick, cancellationToken).ConfigureAwait(false);
    if (claimed.Count == 0) {
      return;   // no row is older than the threshold: the re-check has caught up.
    }

    var metrics = services.GetService<StreamIntegrityMetrics>();
    var byOrigin = origins.ToDictionary(o => o.OriginServiceId, o => (o.OriginServiceName, o.RequestTopic!));
    // One stream-level answer covers every tenant and stream of a type over a window, so rows that share them share one ask.
    // #2262: a re-check's answer passes the generation guard too, so each ask names the generation recorded for its origin
    // (read once per origin: it is the origin's, not the row's).
    var knownGenerations = new Dictionary<Guid, long?>();
    foreach (var group in claimed.GroupBy(c => (c.OriginServiceId, c.EventType, c.WindowFrom, c.WindowUntil))) {
      var (originName, requestTopic) = byOrigin[group.Key.OriginServiceId];
      if (!knownGenerations.TryGetValue(group.Key.OriginServiceId, out var knownGeneration)) {
        knownGeneration = await coordinator.GetIntegritySealGenerationAsync(group.Key.OriginServiceId, cancellationToken)
          .ConfigureAwait(false);
        knownGenerations[group.Key.OriginServiceId] = knownGeneration;
      }
      // #2208: a row with no window was compared over the whole history, so it is answered over the whole history: an
      // unwindowed answer from the origin's recompute (a window opened from the start would be cut at the origin's cap and
      // prove only its slice), compared against this service's own whole-history fold of the answered streams.
      var windowed = group.Key.WindowFrom is not null;
      var envelope = new MessageEnvelope<RequestIntegrityManifest> {
        Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
        MessageId = new MessageId(TrackedGuid.New()),
        Payload = new RequestIntegrityManifest {
          RequesterService = requester,
          Topic = replyTopic,
          EventTypes = [group.Key.EventType],
          Level = ManifestLevel.Streams,
          Windowed = windowed,
          UseRecompute = !windowed,
          SinceSequence = group.Key.WindowFrom ?? 0,
          UntilSequence = group.Key.WindowUntil,
          MaxDigests = windowed ? _options.MaxDigestsPerManifest : null,
          Recheck = true,
          KnownOriginGeneration = knownGeneration,
        },
        Hops = [ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider, now)],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = originName,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      await transport.PublishAsync(serialized.JsonEnvelope,
        IntegrityControlDestination.For(requestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest), _options),
        serialized.EnvelopeType, cancellationToken: cancellationToken).ConfigureAwait(false);
      var count = group.Count();
      metrics?.LedgerRowsRechecked.Add(count, new KeyValuePair<string, object?>("origin", originName));
      LogRechecked(_logger, count, group.Key.EventType, originName);
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Idle ledger re-check tick failed; the rows stay in the ledger and the next tick retries")]
  private static partial void LogTickFailed(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Idle ledger re-check asked '{OriginServiceName}' to answer {Count} stale row(s) of {EventType} again")]
  private static partial void LogRechecked(ILogger logger, int count, string eventType, string originServiceName);
}
