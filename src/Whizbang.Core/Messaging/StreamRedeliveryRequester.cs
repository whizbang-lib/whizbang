using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Messaging;

/// <summary>
/// An operator's request to an origin service: republish the stored events of these streams to this service.
/// </summary>
/// <remarks>
/// The repair for events lost in transport that the integrity ledger cannot drive: events lost before the ledger
/// was enabled, and traffic that carries no origin service id. The origin selects the streams' stored events and
/// publishes them back with their original ids; this service stores the ones it lacks, skips the ones it has, and
/// its perspectives apply what was missing.
/// </remarks>
/// <docs>resilience/stream-integrity#operator-redelivery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamRedeliveryRequesterTests.cs</tests>
public sealed record StreamRedeliveryRequest {
  /// <summary>The origin service's logical name: the service that stored the events.</summary>
  public required string OriginService { get; init; }

  /// <summary>The streams whose stored events are republished; at least one.</summary>
  public required IReadOnlyList<Guid> StreamIds { get; init; }

  /// <summary>
  /// The topic the origin takes requests on. Null uses the one this service learned from the origin's integrity
  /// checkpoints; an origin that sends none (integrity off) needs it named here.
  /// </summary>
  public string? OriginRequestTopic { get; init; }

  /// <summary>
  /// The topic the republished events are sent on, which this service consumes. Null uses
  /// <see cref="StreamIntegrityOptions.RepairTopic"/>, then this service's first consumer destination.
  /// </summary>
  public string? ReplyTopic { get; init; }

  /// <summary>Optional tenant filter (the scope's <c>t</c>).</summary>
  public string? TenantScope { get; init; }

  /// <summary>Optional stored event-type names; null republishes every type in the streams.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }

  /// <summary>
  /// True to rebuild state only: the events are stored and projected, and trigger receptors do not run again.
  /// False (the default) delivers them as the live delivery would have.
  /// </summary>
  public bool StateOnly { get; init; }
}

/// <summary>What an operator redelivery request sent.</summary>
/// <param name="OriginService">The origin service asked.</param>
/// <param name="RequestTopic">The topic the requests were sent on.</param>
/// <param name="ReplyTopic">The topic the origin republishes on.</param>
/// <param name="Streams">How many distinct streams were requested.</param>
/// <param name="Requests">How many requests were sent: one per <see cref="StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST"/> streams.</param>
public sealed record StreamRedeliveryReceipt(string OriginService, string RequestTopic, string ReplyTopic, int Streams, int Requests);

/// <summary>
/// Asks an origin service to republish the stored events of an explicit list of streams to this service: the
/// operator's repair path for events lost in transport.
/// </summary>
/// <docs>resilience/stream-integrity#operator-redelivery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamRedeliveryRequesterTests.cs</tests>
public interface IStreamRedeliveryRequester {
  /// <summary>
  /// Sends the origin a redelivery request for <paramref name="request"/>'s streams. The request is the operator's
  /// act, so it is served and applied whatever either service's <see cref="StreamIntegrityOptions.RepairMode"/>.
  /// </summary>
  /// <exception cref="ArgumentException">No origin, or no streams.</exception>
  /// <exception cref="InvalidOperationException">
  /// This service cannot send it: no transport, no service name, no topic it consumes, or no known request topic for
  /// the origin. The message says which.
  /// </exception>
  Task<StreamRedeliveryReceipt> RequestAsync(StreamRedeliveryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IStreamRedeliveryRequester"/>: sends <see cref="RequestRedeliveryCommand"/>s directed at
/// the origin, the same request the integrity ledger's repair drain sends, marked
/// <see cref="RequestRedeliveryCommand.OperatorRequested"/>.
/// </summary>
/// <remarks>
/// <para>
/// The origin's built-in receptor selects the streams' stored events and publishes them back as
/// <see cref="Minting.RedeliveryComposite"/> bundles with their original event ids. This service's inbox expands
/// each bundle, stores the events it lacks and skips the ones it already has by id, so its perspectives apply
/// exactly what was missing and nothing twice.
/// </para>
/// <para>
/// Streams are sent <see cref="MAX_STREAMS_PER_REQUEST"/> to a request, so a long list stays within a broker's
/// message size; the origin still caps how many events each request republishes.
/// </para>
/// </remarks>
/// <param name="scopeFactory">For the transport, serializer and integrity services, which may be scoped.</param>
/// <param name="logger">Logger.</param>
/// <param name="timeProvider">The clock the request's hop is stamped with.</param>
/// <docs>resilience/stream-integrity#operator-redelivery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamRedeliveryRequesterTests.cs</tests>
public sealed partial class StreamRedeliveryRequester(
    IServiceScopeFactory scopeFactory,
    ILogger<StreamRedeliveryRequester> logger,
    TimeProvider? timeProvider = null) : IStreamRedeliveryRequester {

  /// <summary>The most streams one request names.</summary>
  public const int MAX_STREAMS_PER_REQUEST = 500;

  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public async Task<StreamRedeliveryReceipt> RequestAsync(StreamRedeliveryRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    if (string.IsNullOrWhiteSpace(request.OriginService)) {
      throw new ArgumentException("A redelivery request names the origin service.", nameof(request));
    }
    var streams = (request.StreamIds ?? []).Distinct().ToList();
    if (streams.Count == 0) {
      throw new ArgumentException("A redelivery request names at least one stream.", nameof(request));
    }

    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var transport = services.GetService<ITransport>()
      ?? throw new InvalidOperationException("This service has no transport to send a redelivery request on.");
    var serializer = services.GetService<IEnvelopeSerializer>()
      ?? throw new InvalidOperationException("This service has no envelope serializer to send a redelivery request with.");
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    if (string.IsNullOrWhiteSpace(requester)) {
      throw new InvalidOperationException("This service has no service name, so the origin cannot address the redelivery to it.");
    }
    var replyTopic = request.ReplyTopic
      ?? services.GetService<IOptions<StreamIntegrityOptions>>()?.Value.RepairTopic
      ?? services.GetService<TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address
      ?? throw new InvalidOperationException("This service consumes no topic to receive the redelivery on; name one as the reply topic.");
    var requestTopic = request.OriginRequestTopic
      ?? services.GetService<IntegrityGapTracker>()?.GetOrigins()
        .FirstOrDefault(o => string.Equals(o.OriginServiceName, request.OriginService, StringComparison.Ordinal)
          && !string.IsNullOrEmpty(o.RequestTopic)).RequestTopic
      ?? throw new InvalidOperationException(
        $"The topic '{request.OriginService}' takes requests on is not known here: it is learned from that service's integrity " +
        "checkpoints, and none has arrived. Name it as the origin request topic.");

    var now = _time.GetUtcNow();
    var metrics = services.GetService<StreamIntegrityMetrics>();
    var requests = 0;
    foreach (var chunk in streams.Chunk(MAX_STREAMS_PER_REQUEST)) {
      var command = new RequestRedeliveryCommand {
        TenantScope = request.TenantScope,
        EventTypes = request.EventTypes,
        StreamIds = chunk,
        RequesterService = requester,
        Topic = replyTopic,
        StateOnly = request.StateOnly,
        OperatorRequested = true,
      };
      await RedeliveryRequestDispatch.PublishAsync(
        transport, serializer, instanceProvider, command, request.OriginService, requestTopic, chunk[0], now,
        cancellationToken).ConfigureAwait(false);
      requests++;
      metrics?.RepairsRequested.Add(chunk.Length,
        new KeyValuePair<string, object?>("source", "operator"),
        new KeyValuePair<string, object?>("origin", request.OriginService));
    }
    LogRequested(logger, streams.Count, request.OriginService, requests, requestTopic, replyTopic);
    return new StreamRedeliveryReceipt(request.OriginService, requestTopic, replyTopic, streams.Count, requests);
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Asked {OriginService} to redeliver {StreamCount} stream(s) in {Requests} request(s) on {RequestTopic}, replying on {ReplyTopic}")]
  private static partial void LogRequested(ILogger logger, int streamCount, string originService, int requests, string requestTopic, string replyTopic);
}
