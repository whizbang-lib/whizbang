// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The sequence integrity an outgoing event is held under, and for how long.
/// </summary>
/// <param name="Mode">The mode that applies.</param>
/// <param name="HoldTime">For <see cref="SequenceIntegrity.Bounded"/>, how long the event is held at most.</param>
/// <docs>fundamentals/messaging/sequence-integrity#precedence</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityPolicyTests.cs</tests>
public readonly record struct SequenceHold(SequenceIntegrity Mode, TimeSpan HoldTime) {
  /// <summary>The stored form of <see cref="SequenceIntegrity.UntilStamped"/>: hold with no deadline.</summary>
  public const int UNTIL_STAMPED_MILLISECONDS = -1;

  /// <summary>
  /// The hold as the outbox row stores it: zero for no hold, <see cref="UNTIL_STAMPED_MILLISECONDS"/> for a hold with
  /// no deadline, and otherwise the bounded hold in whole milliseconds, rounded up. A bounded hold of zero or less is no
  /// hold.
  /// </summary>
  public int HoldMilliseconds => (int)Mode;
}

/// <summary>
/// Decides the sequence integrity of an outgoing event: the dispatch's own setting
/// (<c>DispatchOptions.WithSequenceIntegrity</c>), else the event type's <c>[SequenceIntegrity]</c> from the generated
/// catalog, else <see cref="WhizbangCoreOptions.SequenceIntegrity"/>. The hold time of a bounded hold comes from the
/// most specific level that names one, else <see cref="WhizbangCoreOptions.SequenceHoldTime"/>.
/// </summary>
/// <remarks>
/// The catalog is indexed once at construction, so a decision is one dictionary probe. No reflection is involved.
/// </remarks>
/// <docs>fundamentals/messaging/sequence-integrity#precedence</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityPolicyTests.cs</tests>
public sealed class SequenceIntegrityPolicy {
  private readonly WhizbangCoreOptions _options;

  /// <summary>Indexes every catalog entry that declares its own sequence integrity.</summary>
  public SequenceIntegrityPolicy(WhizbangCoreOptions options, IMessageTypeCatalog catalog) {
    ArgumentNullException.ThrowIfNull(catalog);
    _options = options;
  }

  /// <summary>
  /// The registered instance, or one built from the host's options and catalog when the host did not register it.
  /// </summary>
  public static SequenceIntegrityPolicy Resolve(IServiceProvider services) => throw new NotImplementedException();

  /// <summary>Builds an instance from what the container holds, with the framework defaults for anything it lacks.</summary>
  public static SequenceIntegrityPolicy Create(IServiceProvider services) => throw new NotImplementedException();

  /// <summary>The hold for an event of <paramref name="messageType"/>, given the dispatch's own settings.</summary>
  /// <param name="messageType">The event's type.</param>
  /// <param name="callMode">The dispatch's own mode, or null to defer to the type and the configured default.</param>
  /// <param name="callHoldTime">The dispatch's own hold time, or null.</param>
  public SequenceHold For(Type messageType, SequenceIntegrity? callMode = null, TimeSpan? callHoldTime = null) {
    ArgumentNullException.ThrowIfNull(messageType);
    return new SequenceHold(callMode ?? _options.SequenceIntegrity, callHoldTime ?? _options.SequenceHoldTime);
  }
}
