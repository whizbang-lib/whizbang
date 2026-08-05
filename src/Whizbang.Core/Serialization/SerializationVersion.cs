namespace Whizbang.Core.Serialization;

/// <summary>
/// The version of Whizbang's JSON <b>serialization logic/implementation</b> — a framework-owned,
/// hard-coded constant, bumped whenever the way Whizbang serializes a persisted JSON payload
/// changes in a way that prior blobs can't be read by the new code.
/// </summary>
/// <remarks>
/// <para>
/// This is a general, shared concept: <i>any</i> serialization path that persists a JSON blob can
/// stamp this version (via <see cref="VersionedJsonEnvelope"/>) so a reader knows which
/// serialization implementation produced the blob and can detect — and, in future, route to a
/// versioned deserializer for — a stale format. Snapshots are the first consumer.
/// </para>
/// <para>
/// It is NOT a domain model's schema version; it versions the serialization <i>code path</i>.
/// Version 1 is the first versioned format; blobs written before versioning carry no stamp and
/// read as legacy (version 0).
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/VersionedJsonEnvelopeTests.cs</tests>
public static class SerializationVersion {
  /// <summary>The current serialization-logic version. Bump when the serialization implementation
  /// changes such that prior blobs are no longer readable by the new code.</summary>
  public const int CURRENT = 1;
}
