namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageFailureReasonTests.cs:MessageFailureReason_HasExpectedValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageFailureReasonTests.cs:MessageFailureReason_CanConvertToIntAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageFailureReasonTests.cs:MessageFailureReason_CanConvertFromIntAsync</tests>
/// Classifies the reason a message failed processing.
/// Enables typed filtering and handling of different failure scenarios.
/// </summary>
public enum MessageFailureReason {
  /// <summary>
  /// No failure - message processed successfully.
  /// </summary>
  None = 0,

  /// <summary>
  /// Transport is not connected/ready to publish messages.
  /// Message is buffered awaiting transport readiness.
  /// </summary>
  TransportNotReady = 1,

  /// <summary>
  /// Transport exception occurred (e.g., ServiceBusException).
  /// Indicates connectivity or service issues with the message broker.
  /// </summary>
  TransportException = 2,

  /// <summary>
  /// JSON serialization/deserialization failed.
  /// Message payload may be malformed or incompatible with schema.
  /// </summary>
  SerializationError = 3,

  /// <summary>
  /// Message validation failed.
  /// Message payload does not meet validation requirements.
  /// </summary>
  ValidationError = 4,

  /// <summary>
  /// Maximum retry attempts exceeded.
  /// Message has been tried too many times without success.
  /// </summary>
  MaxAttemptsExceeded = 5,

  /// <summary>
  /// Message held in buffer too long without successful publish.
  /// Lease could not be renewed or max hold time exceeded.
  /// </summary>
  LeaseExpired = 6,

  /// <summary>
  /// Event storage failed in wh_event_store.
  /// Event could not be persisted to the event store (non-idempotent failure).
  /// </summary>
  EventStorageFailure = 7,

  /// <summary>
  /// Broker-side throttling (e.g., Azure Service Bus ServiceBusy 50009, RabbitMQ flow-control,
  /// connection.blocked). Distinguished from <see cref="TransportException"/> so dashboards
  /// can separate "we're going faster than the namespace tier can absorb" from "the broker is
  /// down or auth is broken." Retry semantics are typically the same — back off and try again —
  /// but the operational response differs (raise the tier / upgrade Premium MUs vs. fix the
  /// outage).
  /// </summary>
  Throttled = 8,

  /// <summary>
  /// Unclassified error - reason not determined.
  /// </summary>
  Unknown = 99,

  /// <summary>
  /// Establishing the message security context (via
  /// <see cref="Whizbang.Core.Security.IMessageSecurityContextProvider"/>.EstablishContextAsync)
  /// hung past the worker's configured timeout. Distinguishes upstream
  /// extractor hangs (tenant lookup, role-resolution service timeout, etc.)
  /// from actual transport / serialization / business-logic failures.
  /// </summary>
  /// <remarks>
  /// Introduced in Slice 5a of release/v0.647.0-alpha.1 after a consumer's
  /// production stuck-row pattern was traced to the consumer's
  /// <c>IMessageSecurityContextProvider</c> implementation hanging
  /// indefinitely on a test-pattern tenant id. See
  /// operations/dead-letter-queue/internal-dlq doc page.
  /// </remarks>
  SecurityContextEstablishmentFailure = 10,

  /// <summary>
  /// Producer wrote a row whose <c>stream_id</c> is the all-zeros UUID
  /// (<c>Guid.Empty</c>, distinct from NULL). Under
  /// <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy.DeadLetter"/>
  /// or <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy.Purge"/>,
  /// the coordinator stamps this reason on the row to give operators a
  /// grep-able code distinct from generic transport / serialization failures.
  /// </summary>
  /// <remarks>
  /// Introduced in v0.657 after a production silent-stuck forensic investigation.
  /// See operations/configuration/empty-stream-id-policy.
  /// </remarks>
  EmptyStreamId = 11,

  /// <summary>
  /// The serialized envelope exceeds the destination transport's
  /// <see cref="Whizbang.Core.Transports.ITransport.MaxMessageSizeBytes"/>
  /// AND no post-serialize hook (e.g., body offload / claim-check) substituted
  /// the body with a smaller wire envelope. The publish strategy raises this
  /// pre-flight so the outbox row stays put and ops can react — register a
  /// body-offload provider, raise the transport tier, or trim the payload.
  /// </summary>
  /// <remarks>
  /// Distinct from <see cref="TransportException"/> because the wire-send
  /// never happened; the transport hasn't been reached. Distinct from
  /// <see cref="SerializationError"/> because serialization succeeded — the
  /// payload is just larger than the broker will accept.
  /// </remarks>
  MessageBodyTooLarge = 12,

  /// <summary>
  /// An incoming wire message carried a body claim (<c>whizbang.is-claim</c>
  /// header) referencing a provider name that has no
  /// <see cref="Whizbang.Core.Offloads.IMessageBodyStore"/> registered.
  /// Receiver MUST dead-letter — silently dropping the message would lose
  /// the event; processing without the body would skip the actual payload.
  /// </summary>
  /// <remarks>
  /// Indicates a misconfiguration: the sender registered a provider name the
  /// receiver doesn't know. Fix by registering the matching
  /// <c>AddWhizbang*Offload(name)</c> on the receiver service.
  /// </remarks>
  BodyClaimProviderUnknown = 13,

  /// <summary>
  /// A body claim downloaded successfully but its SHA-256 content hash did
  /// NOT match the hash on the claim ticket. Indicates either storage
  /// corruption, a man-in-the-middle modification, or a provider bug.
  /// Receiver MUST dead-letter to prevent processing a payload the sender
  /// did not write.
  /// </summary>
  BodyClaimIntegrityFailure = 14,

  /// <summary>
  /// A composite event (<see cref="Whizbang.Core.Minting.ICompositeEvent"/>)
  /// yielded more inner events than its
  /// <see cref="Whizbang.Core.Minting.ICompositeEvent.MaxInnerEventsAllowed"/>
  /// cap. The receiver refuses to expand it — likely a producer bug
  /// (runaway enumerator, accidentally-nested composites).
  /// </summary>
  CompositeInnerEventLimitExceeded = 15,

  /// <summary>
  /// A composite event failed to expand at the receiver — either an
  /// inner event raised during enumeration, or the all-or-nothing
  /// event-store append rolled back. The whole composite is rejected
  /// (no partial inner events recorded).
  /// </summary>
  CompositeExpansionFailure = 16,

  /// <summary>
  /// The message was dead-lettered at the BROKER (delivery-attempt exhaustion, session churn,
  /// a build that could not deserialize it) and imported into <c>wh_dead_letters</c> by the
  /// transport dead-letter drain — custody transferred from the broker's opaque bucket to the
  /// recovery flow. The broker's own reason/description are preserved in <c>error_text</c>.
  /// </summary>
  BrokerDeadLetter = 17,

  /// <summary>
  /// The poison detector (topology arc phase 8.5) quarantined the message: this service has
  /// durably observed the same message id past the configured bound without ever settling it.
  /// <para>
  /// Distinct from <see cref="MaxAttemptsExceeded"/>, which counts PROCESSING attempts on a
  /// claimed row. This reason counts REDELIVERIES — the broker handing the same message back
  /// because the consumer died before acking. On session-enabled entities that loop increments no
  /// broker counter at all, so nothing else bounds it.
  /// </para>
  /// </summary>
  PoisonRedeliveryLoop = 18
}
