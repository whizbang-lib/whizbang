// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Validation;

/// <summary>What a framework-minted id that is not a UUIDv7 is used as.</summary>
/// <docs>fundamentals/identity/whizbang-ids#framework-ids-that-are-not-time-ordered</docs>
public enum NonTimeOrderedIdUse {
  /// <summary>Neither an event or message id nor a stream id: a payload field, a key of another table, a storage key.</summary>
  NotAMessageId,

  /// <summary>A stream id: hashed and sorted for a total order, never read for its time (open item on #2528).</summary>
  StreamId,

  /// <summary>A key computed from bytes for hashing, partitioning or comparison; never stored as an identity.</summary>
  HashKey
}

/// <summary>One framework site that mints an id that is not a UUIDv7, with why that is safe.</summary>
/// <param name="Site">The source file (relative to the repository root) and member that mints it.</param>
/// <param name="Version">The id's version as minted (a raw hash's version is arbitrary, written as 0).</param>
/// <param name="Use">What the id is used as.</param>
/// <param name="Reason">Why no version assignment, apply order, replay or partitioning path reads its time.</param>
/// <docs>fundamentals/identity/whizbang-ids#framework-ids-that-are-not-time-ordered</docs>
public sealed record NonTimeOrderedId(string Site, int Version, NonTimeOrderedIdUse Use, string Reason);

/// <summary>
/// Every framework site that mints an id that is not a UUIDv7, by design (#2528). Event and message ids must be v7
/// wherever they enter the framework; each entry here shows that its id never becomes one, so nothing that reads id time
/// order (version assignment by message id, apply tie-break on event id, replay's last-resort order,
/// <c>wh_lifecycle_completions</c> partitioning) ever sees it. A test pins each entry's value, and a guard fails when a
/// new non-v7 minting site appears without an entry.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#framework-ids-that-are-not-time-ordered</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/FrameworkNonTimeOrderedIdsTests.cs</tests>
public static class FrameworkNonTimeOrderedIds {
  /// <summary>The entries, one per minting site.</summary>
  public static IReadOnlyList<NonTimeOrderedId> All { get; } = [
    new("src/Whizbang.Core/Messaging/IntegrityDeliveryId.cs:For", 8, NonTimeOrderedIdUse.NotAMessageId,
      "The id of a deferred integrity delivery in wh_integrity_deliveries, derived from its type and payload so one delivery "
      + "is recorded once. It never reaches the inbox, outbox or event store; the drain orders by queued_at and uses the id "
      + "only as a tie-break and the dedup key."),
    new("src/Whizbang.Data.Postgres/PerspectiveRebuildRequestHandler.cs:_runIdFor", 0, NonTimeOrderedIdUse.NotAMessageId,
      "A multi-stream rebuild run id inside the RebuildPerspectiveStreamsCommand payload, derived from the request and the "
      + "perspective so a redelivered command finds its run. The command's message id is minted."),
    new("src/Whizbang.Core/Temporal/SagaDeadlineScheduler.cs:DeadlineScheduleId", 0, NonTimeOrderedIdUse.NotAMessageId,
      "A schedule id (wh_schedules), derived from the saga stream and the deadline name so re-arming moves the deadline. "
      + "Occurrences are published with minted message ids."),
    new("src/Whizbang.Core/SystemEvents/AuditLedger.cs:RecordIdFor", 5, NonTimeOrderedIdUse.NotAMessageId,
      "The EventAudited.Id payload field, derived from the audited event so a record derived again is the same record. "
      + "The audit message's own message id is minted."),
    new("src/Whizbang.Core/SystemEvents/AuditLedger.cs:StreamFor", 5, NonTimeOrderedIdUse.StreamId,
      "The tenant's audit ledger stream; one stream per tenant (open item 1 on #2528)."),
    new("src/Whizbang.Core/Commands/System/SystemCommands.cs:ChunkRequestId", 5, NonTimeOrderedIdUse.NotAMessageId,
      "A payload request id of one chunk of a split rebuild request, derived so the same request splits the same way."),
    new("src/Whizbang.Core/Messaging/RedeliveryPump.cs:AnswerSessionFor", 5, NonTimeOrderedIdUse.StreamId,
      "The broker session and bundle stream of a stream-spanning repair answer; derived so every page agrees."),
    new("src/Whizbang.Core/Messaging/CollectiveOrdering.cs:StreamIdFor", 8, NonTimeOrderedIdUse.StreamId,
      "The stream of a collective ordering key; one stream per key (open item 1 on #2528)."),
    new("src/Whizbang.Sagas/SagaItemStreams.cs:Of", 5, NonTimeOrderedIdUse.StreamId,
      "A saga item's stream, derived from the saga and the item (open item 1 on #2528)."),
    new("src/Whizbang.Core/ValueObjects/NameBasedGuid.cs:V5", 5, NonTimeOrderedIdUse.NotAMessageId,
      "The RFC 9562 name-based generator itself; every caller is listed here by its own site."),
    new("src/Whizbang.Core/ValueObjects/TrackedGuid.cs:NewRandom", 4, NonTimeOrderedIdUse.NotAMessageId,
      "An explicit request for a random v4; a v4 passed to an event or message id entry point is refused there."),
    new("src/Whizbang.Core/Workers/TrackedCompletion.cs:TrackingId", 4, NonTimeOrderedIdUse.NotAMessageId,
      "An in-memory handle of a completion being tracked; never stored."),
    new("src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:_instanceId", 4, NonTimeOrderedIdUse.NotAMessageId,
      "A service instance id; not a message id."),
    new("src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs:_defaultMetadata", 4, NonTimeOrderedIdUse.NotAMessageId,
      "The placeholder last-applied event id of a row written outside a perspective run. The idempotency filter compares it "
      + "for equality only (PerspectiveIdempotencyFilter), never for order, and a random v4 equals no event."),
    new("src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs:_convertPartitionKeyToGuid", 0, NonTimeOrderedIdUse.HashKey,
      "An MD5 partition key of a perspective row; a hash, never an identity."),
    new("src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs:_convertPartitionKeyToGuid", 0, NonTimeOrderedIdUse.HashKey,
      "An MD5 partition key of a perspective row; a hash, never an identity."),
    new("src/Whizbang.Core/Observability/RouteNormalizer.cs:RouteId", 0, NonTimeOrderedIdUse.HashKey,
      "A route's identity for trace storage, a SHA-256 of the normalized route; never a message id."),
    new("src/Whizbang.Core/Messaging/IntegrityDigestMath.cs:SingleMissingEventId", 0, NonTimeOrderedIdUse.HashKey,
      "The XOR of two digests' id lanes: the id of the one missing event when exactly one is missing, which is then looked "
      + "up for presence; when the digests differ by more than one event it is a meaningless value and is only compared."),
    new("src/Whizbang.Offloads.InMemory/InMemoryMessageBodyStore.cs:UploadAsync", 4, NonTimeOrderedIdUse.NotAMessageId,
      "A storage key of an offloaded message body."),
    new("src/Whizbang.Storage.AzureBlob/AzureBlobMessageBodyStore.cs:UploadAsync", 4, NonTimeOrderedIdUse.NotAMessageId,
      "A blob name of an offloaded message body."),
  ];
}
