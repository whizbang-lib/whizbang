// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>How many stored events a ledger rebuild read, and how many audit records it queued from them.</summary>
/// <param name="Selected">Stored events selected.</param>
/// <param name="Rederived">Audit records queued.</param>
/// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs:RunAsync_SelectsEveryStoredEvent_AndQueuesOnlyTheEligibleAsync</tests>
public readonly record struct AuditRebuildResult(int Selected, int Rederived);

/// <summary>
/// Re-derives audit records from stored events (#1724). An audit record is a function of the event it audits and that
/// event's scope, so it can be minted again from the origin's store: this selects stored events the way a redelivery
/// does, keeps the types audit eligibility admits, and queues each record to the audit topic exactly as the live outbox
/// path does, under the id the live writer gave it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One record per origin event.</b> Every writer names a record <see cref="AuditLedger.RecordIdFor"/> of the event
/// it audits, and the audit perspective keys a record by that id, so a record derived again replaces the row it already
/// has rather than adding one, and a rebuild run twice leaves the same rows.
/// </para>
/// <para>
/// <b>What is applied again.</b> The mode, the type's <c>[AuditEvent]</c> and the ephemeral exclusion (#1730), through
/// <see cref="AuditEligibility.Decide"/>. Not the decision hook: a hook judges a payload, and the rebuild never
/// rehydrates one, for the same reasons a redelivery does not (AOT metadata and upcast fidelity). A stored type this
/// build does not know is audited under <see cref="AuditMode.OptOut"/>, where nothing could have excluded it, and not
/// under <see cref="AuditMode.OptIn"/>, where nothing could have marked it.
/// </para>
/// <para>
/// <b>What a record holds.</b> The stored event's id, type, stream and version; the tenant and user from its stored
/// scope; the time, correlation and causation of its first stored hop, so the trail keeps the time the event happened
/// rather than the time of the rebuild (the rebuild's own time stands in only when the stored metadata has no hop to
/// read); the body only when the type or the default keeps it, taken from the stored body.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs</tests>
public sealed class AuditLedgerRebuild {
  private readonly SystemEventOptions _options;
  private readonly TimeProvider _time;
  private readonly Dictionary<string, Type> _types;

  /// <summary>Creates a rebuild that resolves stored type names against <paramref name="catalog"/>.</summary>
  /// <param name="catalog">The generated message-type catalog; a stored type outside it is decided by the mode alone.</param>
  /// <param name="options">The audit options the live writers use.</param>
  /// <param name="timeProvider">The clock for a record whose stored metadata has no hop time; null is the system clock.</param>
  public AuditLedgerRebuild(IMessageTypeCatalog catalog, SystemEventOptions options, TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(catalog);
    _options = ArgumentGuard.NotNull(options);
    _time = timeProvider ?? TimeProvider.System;
    _types = EventTypeMatchingHelper.BuildTypeLookup([.. catalog.GetAll().Select(e => e.Type)]);
  }

  /// <summary>
  /// The audit record of <paramref name="storedEvent"/>, as the outbox row that carries it, or null when the event is
  /// not audited.
  /// </summary>
  /// <param name="storedEvent">A stored event, as the redelivery selection returns it.</param>
  /// <returns>The record's outbox row, or null.</returns>
  public OutboxMessage? Rederive(RedeliveryEvent storedEvent) {
    ArgumentNullException.ThrowIfNull(storedEvent);
    var isEphemeral = (storedEvent.Flags & (int)EventFlags.Ephemeral) != 0;
    EventTypeMatchingHelper.TryResolveType(_types, storedEvent.EventType, out var eventType);
    if (!_isAudited(eventType, isEphemeral)) {
      return null;
    }

    var scope = _scopeOf(storedEvent.Scope);
    var hop = _firstHop(storedEvent.Metadata);
    var attribute = eventType is null ? null : AuditEligibility.AttributeOf(eventType);
    var auditEvent = new EventAudited {
      Id = AuditLedger.RecordIdFor(storedEvent.EventId),
      OriginalEventId = storedEvent.EventId,
      OriginalEventType = TypeNameFormatter.GetFullName(storedEvent.EventType),
      OriginalStreamId = storedEvent.StreamId.ToString(),
      OriginalStreamPosition = storedEvent.Version,
      OriginalBody = AuditEligibility.IncludesBody(eventType, _options.AuditBodyDefault) ? _parse(storedEvent.EventData) : null,
      Timestamp = _hopTime(hop) ?? _time.GetUtcNow(),
      TenantId = scope?.TenantId,
      UserId = scope?.UserId,
      CorrelationId = _hopString(hop, "co"),
      CausationId = _hopString(hop, "ca"),
      AuditReason = attribute?.Reason,
      AuditLevel = attribute?.Level ?? Whizbang.Core.Audit.AuditLevel.Info,
      Scope = _recordScope(scope),
    };
    return AuditOutboxMessageBuilder.ToOutboxMessage(auditEvent, [], scope, _options);
  }

  /// <summary>
  /// Rebuilds the records of the events <paramref name="select"/> returns for <paramref name="request"/>, in keyset pages
  /// of at most <paramref name="pageSize"/>, queueing each record to <paramref name="channel"/>. Selects nothing when
  /// event audit is off: a service that writes no audit records has no ledger to rebuild.
  /// </summary>
  /// <param name="select">The redelivery selection (<see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>).</param>
  /// <param name="channel">Where each record is queued; the next work batch writes it to the outbox.</param>
  /// <param name="request">The rebuild's filters and requested cap.</param>
  /// <param name="maxEvents">The origin's cap on events selected; the request may lower it, never raise it.</param>
  /// <param name="pageSize">Events selected per page, which bounds the memory a rebuild holds at once.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>How many events were selected and how many records were queued.</returns>
  public async Task<AuditRebuildResult> RunAsync(
      Func<RedeliveryRequest, CancellationToken, Task<IReadOnlyList<RedeliveryEvent>>> select,
      IDeferredOutboxChannel channel,
      RequestAuditRebuildCommand request,
      int maxEvents,
      int pageSize,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(select);
    ArgumentNullException.ThrowIfNull(channel);
    ArgumentNullException.ThrowIfNull(request);
    if (!_options.EventAuditEnabled) {
      return default;
    }

    var cap = request.MaxEvents is { } requested ? Math.Min(requested, maxEvents) : maxEvents;
    var selected = 0;
    var rederived = 0;
    Guid? afterStream = null;
    long? afterVersion = null;
    while (selected < cap) {
      var page = await select(new RedeliveryRequest {
        TenantScope = request.TenantScope,
        StreamIds = request.StreamIds,
        FromCommitSequence = request.FromCommitSequence,
        ToCommitSequence = request.ToCommitSequence,
        MaxEvents = Math.Min(pageSize, cap - selected),
        AfterStreamId = afterStream,
        AfterVersion = afterVersion,
      }, cancellationToken).ConfigureAwait(false);
      if (page.Count == 0) {
        break;
      }

      foreach (var storedEvent in page) {
        if (Rederive(storedEvent) is { } record) {
          await channel.QueueAsync(record, cancellationToken).ConfigureAwait(false);
          rederived++;
        }
      }
      selected += page.Count;
      afterStream = page[^1].StreamId;
      afterVersion = page[^1].Version;
    }
    return new AuditRebuildResult(selected, rederived);
  }

  private bool _isAudited(Type? eventType, bool isEphemeral) {
    if (eventType is not null) {
      return AuditEligibility.Decide(payload: null, eventType, _options.AuditMode, hook: null, isEphemeral).ShouldAudit;
    }
    return !isEphemeral && _options.AuditMode == AuditMode.OptOut;
  }

  private static JsonElement _parse(string json) {
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
  }

  /// <summary>The stored scope's tenant, user, customer and organization, or null when it names none.</summary>
  private static PerspectiveScope? _scopeOf(string? scopeJson) {
    if (string.IsNullOrEmpty(scopeJson)) {
      return null;
    }
    var root = _parse(scopeJson);
    if (root.ValueKind != JsonValueKind.Object) {
      return null;
    }
    var scope = new PerspectiveScope {
      TenantId = _string(root, "t"),
      UserId = _string(root, "u"),
      CustomerId = _string(root, "c"),
      OrganizationId = _string(root, "o"),
    };
    return scope.TenantId is null && scope.UserId is null && scope.CustomerId is null && scope.OrganizationId is null
      ? null
      : scope;
  }

  /// <summary>The record's scope dictionary, the tenant and user the live outbox path writes.</summary>
  private static Dictionary<string, string?>? _recordScope(PerspectiveScope? scope) {
    if (scope is null) {
      return null;
    }
    Dictionary<string, string?> entries = [];
    if (scope.TenantId is not null) {
      entries["TenantId"] = scope.TenantId;
    }
    if (scope.UserId is not null) {
      entries["UserId"] = scope.UserId;
    }
    return entries;
  }

  /// <summary>The first hop of the stored metadata (<c>{"MessageId":…,"Hops":[…]}</c>), or null when there is none.</summary>
  private static JsonElement? _firstHop(string? metadataJson) {
    if (string.IsNullOrEmpty(metadataJson)) {
      return null;
    }
    var root = _parse(metadataJson);
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(nameof(EnvelopeMetadata.Hops), out var hops)
        && hops.ValueKind == JsonValueKind.Array
        && hops.GetArrayLength() > 0
        && hops[0].ValueKind == JsonValueKind.Object) {
      return hops[0];
    }
    return null;
  }

  private static DateTimeOffset? _hopTime(JsonElement? hop) =>
    _hopString(hop, "ts") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
      System.Globalization.DateTimeStyles.RoundtripKind, out var time)
      ? time
      : null;

  private static string? _hopString(JsonElement? hop, string key) => hop is { } element ? _string(element, key) : null;

  private static string? _string(JsonElement element, string key) =>
    element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
