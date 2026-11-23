// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares an event type's sequence integrity: whether the outbox publishes it at once, holds it briefly for its
/// commit-order stamp, or holds it until it is stamped.
/// </summary>
/// <remarks>
/// Read at compile time into the message type catalog, so no reflection is involved at run time. A dispatch can still
/// override it for one call with <c>DispatchOptions.WithSequenceIntegrity</c>; a type without it takes the configured
/// default (<c>Whizbang:Core:SequenceIntegrity</c>).
/// </remarks>
/// <param name="mode">The type's sequence integrity.</param>
/// <param name="holdMilliseconds">
/// For <see cref="SequenceIntegrity.Bounded"/>, how long to hold an unstamped event before publishing it anyway, in
/// milliseconds. Zero or less (the default) takes the configured hold time.
/// </param>
/// <docs>fundamentals/messaging/sequence-integrity#on-the-event-type</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityPolicyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Attributes/SequenceIntegrityAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class SequenceIntegrityAttribute(SequenceIntegrity mode, int holdMilliseconds = 0) : Attribute {
  /// <summary>The type's sequence integrity.</summary>
  public SequenceIntegrity Mode { get; } = mode;

  /// <summary>The hold time for a bounded hold, in milliseconds; zero or less takes the configured hold time.</summary>
  public int HoldMilliseconds { get; } = holdMilliseconds;
}
