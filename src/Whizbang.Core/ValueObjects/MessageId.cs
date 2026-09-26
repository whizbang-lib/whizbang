using Whizbang.Core;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies a message within the system.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses the framework's UUIDv7 generator for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
/// <docs>fundamentals/messages/message-context</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/IdentityValueObjectTests.cs</tests>
[WhizbangId]
public readonly partial struct MessageId;
