// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// The paced backfill of a grown subscription (#2092). The subscription-expansion reconciler queues one backfill per
/// (owner, event type); this worker walks each in bounded WINDOWS by the owner's commit sequence: it asks for the type's
/// events after the backfill's position, up to <see cref="StreamIntegrityOptions.BackfillWindowSize"/>, and asks for
/// the next window only once the owner's <see cref="BackfillWindowAnswer"/> has moved the position to where that window
/// stopped (#2098). At most <see cref="StreamIntegrityOptions.BackfillMaxConcurrentWindows"/> windows await an answer at
/// once, and every window spends its full size from a token bucket refilled at
/// <see cref="StreamIntegrityOptions.BackfillEventsPerMinute"/>, holding at most one minute's worth (or one window). A
/// window unanswered past <see cref="StreamIntegrityOptions.RepairAnswerTimeoutMinutes"/> is asked again, up to
/// <see cref="StreamIntegrityOptions.BackfillMaxWindowAttempts"/>; then the backfill ends as
/// <see cref="SubscriptionBackfillOutcome.Unanswered"/>, which is what an owner on a release before windowed backfill
/// produces (it answers the legacy way, with bundles and no window answer). Runs on the instance holding the maintainer
/// duty, only once the housekeeping idle gate admits integrity work, and never in report-only or with
/// <see cref="StreamIntegrityOptions.BackfillOnSubscriptionGrowth"/> off.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SubscriptionBackfillWorkerTests.cs</tests>
public sealed partial class SubscriptionBackfillWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StreamIntegrityOptions> options,
    ILogger<SubscriptionBackfillWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<SubscriptionBackfillWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private double _tokens;
  // Created with the worker, not when the loop first runs: the host schedules ExecuteAsync rather than running it inside
  // StartAsync, so a tick that comes due before the loop reaches its first wait must already be on the timer.
  private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1), timeProvider ?? TimeProvider.System);
  private DateTimeOffset _lastTick = (timeProvider ?? TimeProvider.System).GetUtcNow();

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      while (await _timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
        var now = _time.GetUtcNow();
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        try {
          await TickAsync(elapsed, now, stoppingToken).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogTickFailed(_logger, ex);
        }
      }
    } catch (OperationCanceledException) {
      // stopping is the normal way out of the loop
    }
  }

  /// <inheritdoc />
  public override void Dispose() {
    _timer.Dispose();
    base.Dispose();
  }

  /// <summary>
  /// One pacing tick: refill the bucket, then, on the maintainer's turn and with the idle gate's admission, end every
  /// backfill whose window went unanswered for every attempt and request the next window of as many waiting backfills
  /// as the concurrency limit and the bucket allow, least recently asked first. Internal for deterministic tests; the
  /// loop above supplies real elapsed time.
  /// </summary>
  internal async Task TickAsync(double elapsedSeconds, DateTimeOffset now, CancellationToken cancellationToken) {
    if (!SubscriptionBackfillGate.ShouldRequestBackfill(_options.BackfillOnSubscriptionGrowth, _options.RepairMode)) {
      return;
    }
    var windowSize = _options.BackfillWindowSize;
    var perMinute = _options.BackfillEventsPerMinute;
    _tokens = Math.Min(_tokens + (perMinute / 60.0 * Math.Max(0, elapsedSeconds)), Math.Max(windowSize, perMinute));

    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    if (!MaintainerDuty.IsThisInstancesTurn(services)) {
      return;
    }
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return;
    }
    var backfills = await coordinator.GetSubscriptionBackfillsAsync(cancellationToken).ConfigureAwait(false);
    var running = backfills.Where(b => b.CompletedAt is null).ToList();
    var metrics = services.GetService<StreamIntegrityMetrics>();
    var inFlight = running.Count(b => b.AnswerDueBy > now);
    var due = running
      .Where(b => _isDue(b, now))
      .OrderBy(b => b.LastRequestedAt ?? DateTimeOffset.MinValue)
      .ToList();
    var sent = due.Count == 0 ? 0 : await _requestWindowsAsync(services, coordinator, metrics, due, inFlight, now, cancellationToken)
      .ConfigureAwait(false);
    metrics?.UpdateBackfillGauges(new BackfillGaugeSnapshot {
      WindowsInFlight = inFlight + sent,
      Remaining = [.. running.Select(b => (b.EventType, Remaining: b.EstimateRemaining()))
        .Where(r => r.Remaining is not null)
        .Select(r => new BackfillRemaining(r.EventType, r.Remaining!.Value))],
    });
  }

  /// <summary>A backfill whose next request may go now: no window awaits an answer, or the awaited one is past due.</summary>
  private static bool _isDue(SubscriptionBackfillCursor backfill, DateTimeOffset now) {
    if (backfill.AnswerDueBy is null) {
      return true;
    }
    return backfill.AnswerDueBy.Value <= now;
  }

  /// <summary>Ends the exhausted backfills and requests windows of the rest; returns the windows requested.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "One pass of the tick: the scope's services and the collaborators the tick already resolved, the due backfills, the windows already out, the clock and cancellation.")]
  private async Task<int> _requestWindowsAsync(
      IServiceProvider services, IWorkCoordinator coordinator, StreamIntegrityMetrics? metrics,
      List<SubscriptionBackfillCursor> due, int inFlight, DateTimeOffset now, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var replyTopic = RepairTopicResolver.Resolve(_options.RepairTopic, services);
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(replyTopic)) {
      return 0;   // nothing can leave: the backfills stay queued until the service can send.
    }
    using var admission = await IntegrityAdmission.TryAdmitAsync(
      services, coordinator, nameof(SubscriptionBackfillWorker), _logger, cancellationToken).ConfigureAwait(false);
    if (!admission.Granted) {
      return 0;
    }

    var claims = await coordinator.GetIntegrityDomainClaimsAsync(cancellationToken).ConfigureAwait(false);
    var sender = new BackfillSender(transport, serializer, instanceProvider, requester, replyTopic);
    var window = _options.BackfillWindowSize;
    var slots = _options.BackfillMaxConcurrentWindows - inFlight;
    var sent = 0;
    foreach (var backfill in due) {
      if (backfill.AnswerDueBy is not null && backfill.WindowAttempts >= _options.BackfillMaxWindowAttempts) {
        await _endUnansweredAsync(coordinator, metrics, backfill, now, cancellationToken).ConfigureAwait(false);
        continue;
      }
      if (sent >= slots || _tokens < window) {
        continue;   // keep walking: a later backfill may still need ending.
      }
      if (!await _trySendWindowAsync(sender, backfill, claims, services, coordinator, now, cancellationToken).ConfigureAwait(false)) {
        continue;
      }
      _tokens -= window;
      sent++;
      metrics?.BackfillWindowsRequested.Add(1, new KeyValuePair<string, object?>("event_type", backfill.EventType));
      if (backfill.WindowsRequested == 0) {
        LogStarted(_logger, backfill.EventType, backfill.OwnerServiceName, window);
      }
    }
    return sent;
  }

  /// <summary>What the tick sends windows with, resolved once per tick.</summary>
  private readonly record struct BackfillSender(
    ITransport Transport, IEnvelopeSerializer Serializer, IServiceInstanceProvider? InstanceProvider, string Requester, string ReplyTopic);

  /// <summary>Ends a backfill whose window went unanswered through every attempt.</summary>
  private async Task _endUnansweredAsync(
      IWorkCoordinator coordinator, StreamIntegrityMetrics? metrics, SubscriptionBackfillCursor backfill, DateTimeOffset now,
      CancellationToken cancellationToken) {
    await coordinator.EndSubscriptionBackfillAsync(backfill.BackfillId, SubscriptionBackfillOutcome.Unanswered, now, cancellationToken)
      .ConfigureAwait(false);
    metrics?.BackfillsEnded.Add(1, new KeyValuePair<string, object?>("outcome", "unanswered"));
    LogUnanswered(_logger, backfill.EventType, backfill.OwnerServiceName, backfill.Window, backfill.WindowAttempts);
  }

  /// <summary>
  /// Asks the owner for a backfill's next window (or the unanswered one again) and records it as requested.
  /// False when the owner has no address or the send failed; the window is asked on a later tick.
  /// </summary>
  private async Task<bool> _trySendWindowAsync(
      BackfillSender sender, SubscriptionBackfillCursor backfill, IReadOnlyList<IntegrityDomainClaim> claims, IServiceProvider services,
      IWorkCoordinator coordinator, DateTimeOffset now, CancellationToken cancellationToken) {
    var destination = _destination(backfill, claims, services);
    if (destination is null) {
      LogNoAddress(_logger, backfill.EventType, backfill.OwnerServiceName);
      return false;
    }
    // An answered (or never asked) window is followed by the next; an unanswered one is asked again as it was.
    var number = backfill.AnswerDueBy is null ? backfill.Window + 1 : backfill.Window;
    var command = new RequestRedeliveryCommand {
      EventTypes = [backfill.EventType],
      RequesterService = sender.Requester,
      Topic = sender.ReplyTopic,
      StateOnly = true,
      FromCommitSequence = backfill.CursorCommitSequence,
      MaxEvents = _options.BackfillWindowSize,
      BackfillId = backfill.BackfillId,
      BackfillWindow = number,
    };
    try {
      await RedeliveryRequestDispatch.PublishAsync(
        sender.Transport, sender.Serializer, sender.InstanceProvider, command, destination.Value.Target, destination.Value.Destination, now,
        cancellationToken)
        .ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      // A transient broker failure costs this window only; it is asked again on a later tick.
      LogSendFailed(_logger, backfill.EventType, backfill.OwnerServiceName, ex);
      return false;
    }
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(
      backfill.BackfillId, number, now, now.AddMinutes(_options.RepairAnswerTimeoutMinutes), cancellationToken).ConfigureAwait(false);
    return true;
  }

  /// <summary>
  /// Where a backfill's window goes: the owner's announced request address, directed at it; for a backfill with no known
  /// owner, the shared topic every service reads, broadcast. Null when the owner has no address to send to.
  /// </summary>
  private (string? Target, TransportDestination Destination)? _destination(
      SubscriptionBackfillCursor backfill, IReadOnlyList<IntegrityDomainClaim> claims, IServiceProvider services) {
    if (backfill.OwnerServiceId == Guid.Empty) {
      // Never null: the reply topic resolved, so the service consumes a destination to broadcast on.
      var broadcast = RepairTopicResolver.ResolveBroadcast(_options.RepairTopic, services)!;
      return (null, IntegrityControlDestination.For(broadcast, backfill.BackfillId, typeof(RequestRedeliveryCommand), _options));
    }
    var claim = claims.FirstOrDefault(c => c.OwnerServiceId == backfill.OwnerServiceId && !string.IsNullOrEmpty(c.RequestTopic));
    if (claim is null) {
      return null;
    }
    return (backfill.OwnerServiceName,
      IntegrityControlDestination.For(claim.RequestTopic!, backfill.BackfillId, typeof(RequestRedeliveryCommand), _options));
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Backfill of {EventType} from '{OwnerServiceName}' started: windows of {WindowSize} events, paced")]
  private static partial void LogStarted(ILogger logger, string eventType, string ownerServiceName, int windowSize);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Backfill of {EventType} from '{OwnerServiceName}' ended unanswered: window {Window} had no answer after {Attempts} request(s). An owner on a release before windowed backfill answers without a window answer; the integrity audit reports anything still missing")]
  private static partial void LogUnanswered(ILogger logger, string eventType, string ownerServiceName, long window, int attempts);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Backfill of {EventType} waits: its owner '{OwnerServiceName}' has announced no request address")]
  private static partial void LogNoAddress(ILogger logger, string eventType, string ownerServiceName);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Backfill window of {EventType} to '{OwnerServiceName}' could not be sent; it is asked again on a later tick")]
  private static partial void LogSendFailed(ILogger logger, string eventType, string ownerServiceName, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Paced backfill tick failed; the backfills' progress is durable and the next tick retries")]
  private static partial void LogTickFailed(ILogger logger, Exception exception);
}
