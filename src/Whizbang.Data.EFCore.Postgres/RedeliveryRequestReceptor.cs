// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Stream-integrity R1b — the built-in origin-side bridge that turns a received
/// <see cref="RequestRedeliveryCommand"/> into a coordinator selection
/// (<see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>) pumped back to the wire as
/// targeted <see cref="RedeliveryComposite"/> bundles (<see cref="RedeliveryPump"/>). Lives in the
/// driver assembly (not <c>Whizbang.Core</c>) because a receptor in Core would make the
/// receptor-discovery generator emit dispatcher registrations that collide with every consumer's;
/// and the selection needs the Postgres store anyway. The requester's <c>MaxEvents</c> is clamped
/// by this origin's <see cref="RedeliveryPumpOptions.MaxEventsPerRequest"/> — a requester can
/// never raise the origin's storm cap. Inert (logged) if the host lacks the transport, event
/// store, coordinator, or event-type provider.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
public sealed partial class RedeliveryRequestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<RedeliveryRequestReceptor> logger) : IReceptor<RequestRedeliveryCommand> {

  /// <summary>
  /// One repair build at a time per process. Redelivery requests arrive in bursts — per-bucket
  /// auto-repair and subscription-expansion broadcasts land together — and each build holds a
  /// page of event bodies plus its serialized composite; unbounded concurrency multiplies that
  /// footprint and has OOM-killed origins under a first full audit. Requests queue here; inbox
  /// redelivery semantics make waiting safe.
  /// </summary>
  private static readonly SemaphoreSlim _buildGate = new(1, 1);

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestRedeliveryCommand message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var envelopeSerializer = services.GetService<IEnvelopeSerializer>();
    if (coordinator is null || transport is null || envelopeSerializer is null) {
      LogMissingInfrastructure(logger, coordinator is null, transport is null, envelopeSerializer is null);
      return;
    }

    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    metrics?.RedeliveryRequestsReceived.Add(1);

    // #2032: a backfill broadcast on a shared topic reaches its sender too, and answering it re-ships this store to
    // itself. Service names compare without case, as the transport consumer compares them.
    var instanceProvider = services.GetService<IServiceInstanceProvider>()
      ?? Whizbang.Core.Observability.UnknownServiceInstanceProvider.Instance;
    if (string.Equals(message.RequesterService, instanceProvider.ServiceName, StringComparison.OrdinalIgnoreCase)) {
      metrics?.RedeliveryRequestsIgnored.Add(1, new KeyValuePair<string, object?>("reason", "own_request"));
      LogOwnRequestIgnored(logger, message.RequesterService);
      return;
    }

    // Report-only is bilateral: serving a re-delivery request is the repair act on the origin side (and
    // the memory-heavy one), so an origin that opted down declines. Returning completes the inbox row; a
    // declined request is discarded, never retried. Report-only opts out of AUTOMATIC repair, so a request
    // an operator made for named streams is served (#1028).
    var integrity = services.GetService<Microsoft.Extensions.Options.IOptions<StreamIntegrityOptions>>()?.Value;
    if (!message.OperatorRequested && !RepairTraffic.IsRepairEnabled(integrity)) {
      metrics?.RepairTrafficDiscarded.Add(1, new KeyValuePair<string, object?>("role", "origin_request"));
      LogRepairRequestDeclined(logger, message.RequesterService, message.Topic);
      return;
    }
    var buildTimer = System.Diagnostics.Stopwatch.StartNew();
    var options = services.GetService<RedeliveryPumpOptions>() ?? new RedeliveryPumpOptions();
    var cap = options.MaxEventsPerRequest;
    var maxEvents = message.MaxEvents is { } requested ? Math.Min(requested, cap) : cap;
    var pageSize = Math.Max(1, options.SelectPageSize);

    await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      // Phase B: the bundle names THIS origin so fanned-out children carry their original source
      // identity and repaired windows recount correctly at the consumer.
      var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
      var pump = new RedeliveryPump(
        transport, envelopeSerializer,
        // A host with no telemetry identity still redelivers; the bundle names an explicitly
        // unknown origin, which is what passing null used to produce. Requiring the provider
        // here turned an absent identity into a failed redelivery.
        instanceProvider,
        // A host that never registered the event mint still redelivers with the default grouping,
        // which is what the pump's own fallback used to supply.
        compositeFactory: services.GetService<Whizbang.Core.Minting.ICompositeFactory>()
          ?? new Whizbang.Core.Minting.CompositeFactory(),
        options: options) {
        OperatorRequested = message.OperatorRequested,
        // #2078: the answer is an integrity control message, so it expires at the broker like the request did.
        AnswerTimeToLive = TimeSpan.FromMinutes((integrity ?? new StreamIntegrityOptions()).ControlMessageTimeToLiveMinutes),
        // #2092: a backfill window ships across streams, every bundle on the backfill's session.
        BundleStreamId = message.BackfillId,
        // #2268: a requester that files each child under its own stream is answered across streams by scope.
        BundleAcrossStreams = message.AcceptsStreamSpanningBundles,
      };

      // #2008: only events of types this service owns are its own; the rest were received from their authors.
      var ownedDomains = services.GetService<Microsoft.Extensions.Options.IOptions<Whizbang.Core.Routing.RoutingOptions>>()?
        .Value.OwnedDomains ?? new HashSet<string>();
      var serve = new Serve(coordinator, pump, metrics, originServiceId, instanceProvider.ServiceName, ownedDomains, maxEvents, pageSize);
      if (message.BackfillId is { } backfillId) {
        await _answerWindowAsync(message, backfillId, serve, cancellationToken).ConfigureAwait(false);
        return;
      }
      var (totalShipped, totalComposites) = await _serveOrdinaryAsync(message, serve, cancellationToken).ConfigureAwait(false);

      if (totalShipped == 0) {
        LogNothingSelected(logger, message.RequesterService);
        return;
      }
      metrics?.RedeliveryEventsShipped.Add(totalShipped);
      metrics?.RedeliveryBuildDuration.Record(buildTimer.Elapsed.TotalSeconds);
      LogRedeliveryPublished(logger, totalShipped, totalComposites, message.RequesterService, message.Topic);
    } finally {
      _buildGate.Release();
    }
  }

  /// <summary>What one serve needs, held together so each path takes it whole.</summary>
  private sealed record Serve(
    IWorkCoordinator Coordinator, RedeliveryPump Pump, Whizbang.Core.Observability.StreamIntegrityMetrics? Metrics,
    Guid OriginServiceId, string OriginServiceName, IReadOnlySet<string> OwnedDomains, int MaxEvents, int PageSize);

  /// <summary>
  /// An ordinary request: select-and-publish in keyset pages so memory is bounded by ONE page of bodies no matter how
  /// wide the request is (materializing the whole cap at once has OOM-killed origins). An answer that stops at the bound
  /// with more selected is counted and logged (#2098): this request has no window to continue from.
  /// </summary>
  private async Task<(int Shipped, int Composites)> _serveOrdinaryAsync(
      RequestRedeliveryCommand message, Serve serve, CancellationToken cancellationToken) {
    var tally = new OrdinaryTally();
    if (message.EventIds is { Count: > 0 } named) {
      // #2103: the requester named the one event each stream is missing. Ship exactly those; a named stream whose event
      // this store does not hold (the requester's id lanes named no event it wrote) is answered over the window instead.
      var reached = await _shipSelectionAsync(message, serve, message.StreamIds, named, tally, cancellationToken)
        .ConfigureAwait(false);
      var unreached = message.StreamIds?.Where(s => !reached.Contains(s)).ToList();
      if (unreached is { Count: > 0 }) {
        _ = await _shipSelectionAsync(message, serve, unreached, null, tally, cancellationToken).ConfigureAwait(false);
      }
    } else {
      _ = await _shipSelectionAsync(message, serve, message.StreamIds, null, tally, cancellationToken).ConfigureAwait(false);
    }
    return (tally.Shipped, tally.Composites);
  }

  /// <summary>What an ordinary request has selected and shipped so far, across its selections.</summary>
  private sealed class OrdinaryTally {
    public int Selected { get; set; }
    public int Shipped { get; set; }
    public int Composites { get; set; }
    public bool Capped { get; set; }
  }

  /// <summary>
  /// Selects and publishes one selection in keyset pages, within what is left of the request's bound, and returns the
  /// streams it reached. An answer that stops at the bound with more selected is counted and logged once (#2098).
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "One selection of an ordinary request: the request, the serve, the selection's streams and named events, the running tally and cancellation.")]
  private async Task<HashSet<Guid>> _shipSelectionAsync(
      RequestRedeliveryCommand message, Serve serve, IReadOnlyList<Guid>? streams, IReadOnlyList<Guid>? eventIds,
      OrdinaryTally tally, CancellationToken cancellationToken) {
    var reached = new HashSet<Guid>();
    Guid? afterStream = null;
    long? afterVersion = null;
    while (tally.Selected < serve.MaxEvents) {
      var want = Math.Min(serve.PageSize, serve.MaxEvents - tally.Selected);
      var page = await serve.Coordinator.SelectRedeliveryEventsAsync(
        _ordinaryRequest(message, serve, streams, eventIds, want, afterStream, afterVersion),
        cancellationToken).ConfigureAwait(false);
      if (page.Count == 0) {
        break;
      }
      var owned = _owned(page, serve);
      if (owned.Count > 0) {
        // #2143: every bundle says the window it answers, so the receiver marks only the buckets it covered. #2149: and the
        // streams this page carries only in part, which the receiver must not mark from it.
        var window = new RedeliveryAnswerWindow(message.FromCommitSequence, message.ToCommitSequence) {
          PartialStreamIds = _streamsRunningPastThePage(page, want, afterStream),
        };
        tally.Composites += await serve.Pump
          .PublishAnswerAsync(owned, window,
            message.Topic, message.RequesterService, serve.OriginServiceId, message.StateOnly, cancellationToken)
          .ConfigureAwait(false);
      }
      tally.Shipped += owned.Count;
      tally.Selected += page.Count;
      reached.UnionWith(page.Select(e => e.StreamId));
      var last = page[page.Count - 1];
      afterStream = last.StreamId;
      afterVersion = last.Version;
    }
    if (tally.Selected >= serve.MaxEvents && !tally.Capped) {
      var beyond = await serve.Coordinator.SelectRedeliveryEventsAsync(
        _ordinaryRequest(message, serve, streams, eventIds, 1, afterStream, afterVersion), cancellationToken).ConfigureAwait(false);
      if (beyond.Count > 0) {
        tally.Capped = true;
        serve.Metrics?.RedeliveryAnswersCapped.Add(1, new KeyValuePair<string, object?>("kind", "ordinary"));
        LogOrdinaryAnswerCapped(logger, serve.MaxEvents, message.RequesterService);
      }
    }
    return reached;
  }

  /// <summary>
  /// #2149: the streams of one (stream, version)-ordered page whose answer continues past it: the first, when it is the
  /// stream the previous page ended on, and the last, when the page is full (the next page, or the request's bound, may
  /// hold more of it). A full page whose last stream happens to end there names it too; that only leaves its bucket
  /// repairable for another try. Null when the page carries every stream whole.
  /// </summary>
  private static List<Guid>? _streamsRunningPastThePage(IReadOnlyList<RedeliveryEvent> page, int want, Guid? previousStream) {
    var running = new List<Guid>(2);
    if (page[0].StreamId == previousStream) {
      running.Add(page[0].StreamId);
    }
    if (page.Count >= want) {
      running.Add(page[page.Count - 1].StreamId);
    }
    return running.Count == 0 ? null : [.. running.Distinct()];
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "Builds one page's selection: the request, the serve, the selection's streams and named events, the page bound and keyset position.")]
  private static RedeliveryRequest _ordinaryRequest(
      RequestRedeliveryCommand message, Serve serve, IReadOnlyList<Guid>? streams, IReadOnlyList<Guid>? eventIds, int maxEvents,
      Guid? afterStream, long? afterVersion) => new() {
        TenantScope = message.TenantScope,
        EventTypes = message.EventTypes,
        StreamIds = streams,
        EventIds = eventIds,
        FromCommitSequence = message.FromCommitSequence,
        ToCommitSequence = message.ToCommitSequence,
        MaxEvents = maxEvents,
        AfterStreamId = afterStream,
        AfterVersion = afterVersion,
        // #2032: the bundle names this service as origin, so it carries only events this service wrote.
        AuthoredBy = serve.OriginServiceName,
      };

  /// <summary>
  /// One window of a paced backfill (#2092): the type's events after the requester's position in commit-sequence order,
  /// up to the window's bound, in pages, each page shipped across streams in (stream, version) order; then the answer,
  /// saying where the window stopped and whether it stopped at its bound with more possibly left (#2098).
  /// </summary>
  private async Task _answerWindowAsync(
      RequestRedeliveryCommand message, Guid backfillId, Serve serve, CancellationToken cancellationToken) {
    if (message.EventTypes is not { Count: > 0 } eventTypes) {
      LogWindowWithoutTypes(logger, message.RequesterService);
      return;
    }
    var floor = message.FromCommitSequence;
    long? first = null;
    var selected = 0;
    var shipped = 0;
    var composites = 0;
    var exhausted = false;
    while (selected < serve.MaxEvents) {
      var want = Math.Min(serve.PageSize, serve.MaxEvents - selected);
      var page = await serve.Coordinator.SelectRedeliveryEventsAsync(new RedeliveryRequest {
        TenantScope = message.TenantScope,
        EventTypes = eventTypes,
        StreamIds = message.StreamIds,
        FromCommitSequence = floor,
        ToCommitSequence = message.ToCommitSequence,
        MaxEvents = want,
        OrderByCommitSequence = true,
        AuthoredBy = serve.OriginServiceName,
      }, cancellationToken).ConfigureAwait(false);
      if (page.Count > 0) {
        first ??= page[0].CommitSequence;
        // #2143: the page covers the type's events after the previous floor through its last event, or through the
        // window's ceiling when it ran out.
        var covered = new RedeliveryAnswerWindow(floor, page.Count < want ? message.ToCommitSequence : page[page.Count - 1].CommitSequence);
        floor = page[page.Count - 1].CommitSequence;
        // The receiver's fan-out takes each stream's events in version order.
        var owned = _owned(page, serve).OrderBy(e => e.StreamId).ThenBy(e => e.Version).ToList();
        if (owned.Count > 0) {
          composites += await serve.Pump
            .PublishAnswerAsync(owned, covered, message.Topic, message.RequesterService, serve.OriginServiceId, message.StateOnly,
              cancellationToken)
            .ConfigureAwait(false);
        }
        shipped += owned.Count;
        selected += page.Count;
      }
      if (page.Count < want) {
        exhausted = true;
        break;
      }
    }

    var head = await serve.Coordinator.GetRedeliveryHeadCommitSequenceAsync(eventTypes, cancellationToken).ConfigureAwait(false);
    await serve.Pump.PublishWindowAnswerAsync(new BackfillWindowAnswer {
      BackfillId = backfillId,
      Window = message.BackfillWindow,
      OriginServiceId = serve.OriginServiceId,
      OriginServiceName = serve.OriginServiceName,
      EventCount = selected,
      FirstCommitSequence = first,
      StoppedAtCommitSequence = selected > 0 ? floor : null,
      HasMore = !exhausted,
      HeadCommitSequence = head,
    }, message.Topic, message.RequesterService, cancellationToken).ConfigureAwait(false);
    if (!exhausted) {
      serve.Metrics?.RedeliveryAnswersCapped.Add(1, new KeyValuePair<string, object?>("kind", "window"));
    }
    serve.Metrics?.RedeliveryEventsShipped.Add(shipped);
    LogWindowAnswered(logger, message.BackfillWindow, selected, composites, message.RequesterService, !exhausted);
  }

  private static List<RedeliveryEvent> _owned(IReadOnlyList<RedeliveryEvent> page, Serve serve) =>
    [.. page.Where(e => Whizbang.Core.Routing.OwnedNamespaceMatcher.IsLocalEventType(e.EventType, serve.OwnedDomains))];

  [LoggerMessage(EventId = 47, Level = LogLevel.Warning,
    Message = "RequestRedeliveryCommand received but required infrastructure is missing " +
              "(coordinator={CoordinatorMissing}, transport={TransportMissing}, envelopeSerializer={EnvelopeSerializerMissing}); ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, bool coordinatorMissing, bool transportMissing, bool envelopeSerializerMissing);

  [LoggerMessage(EventId = 48, Level = LogLevel.Information,
    Message = "Re-delivery request from {RequesterService} selected no events; nothing published")]
  static partial void LogNothingSelected(ILogger logger, string requesterService);

  [LoggerMessage(EventId = 49, Level = LogLevel.Information,
    Message = "Re-delivered {EventCount} events as {CompositeCount} composites to {RequesterService} on {Topic}")]
  static partial void LogRedeliveryPublished(ILogger logger, int eventCount, int compositeCount, string requesterService, string topic);

  [LoggerMessage(EventId = 51, Level = LogLevel.Debug,
    Message = "Re-delivery request from {RequesterService} ignored: it is this service's own request, come back on a shared topic")]
  static partial void LogOwnRequestIgnored(ILogger logger, string requesterService);

  [LoggerMessage(EventId = 50, Level = LogLevel.Information,
    Message = "Re-delivery request from {RequesterService} on {Topic} declined: RepairMode is ReportOnly, so this origin serves no repair (the request is discarded, not retried)")]
  static partial void LogRepairRequestDeclined(ILogger logger, string requesterService, string topic);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Re-delivery answer to {RequesterService} stopped at this origin's bound of {MaxEvents} events with more selected; an ordinary request is not continued, so the rest stays for the integrity audit (#2098)")]
  static partial void LogOrdinaryAnswerCapped(ILogger logger, int maxEvents, string requesterService);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Backfill window {Window} for {RequesterService} answered: {EventCount} events in {CompositeCount} bundles, more remaining: {HasMore}")]
  static partial void LogWindowAnswered(ILogger logger, long window, int eventCount, int compositeCount, string requesterService, bool hasMore);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Backfill window from {RequesterService} names no event type; it is not answered")]
  static partial void LogWindowWithoutTypes(ILogger logger, string requesterService);
}
