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
  public int HoldMilliseconds => Mode switch {
    SequenceIntegrity.UntilStamped => UNTIL_STAMPED_MILLISECONDS,
    SequenceIntegrity.Bounded when HoldTime > TimeSpan.Zero =>
      (int)Math.Min(Math.Ceiling(HoldTime.TotalMilliseconds), int.MaxValue),
    _ => 0,
  };
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
  private readonly Dictionary<Type, (SequenceIntegrity Mode, int HoldMilliseconds)> _byType = [];

  /// <summary>Indexes every catalog entry that declares its own sequence integrity.</summary>
  /// <param name="options">The configured default mode and hold time.</param>
  /// <param name="catalog">The generated catalog, carrying each type's <c>[SequenceIntegrity]</c>.</param>
  public SequenceIntegrityPolicy(WhizbangCoreOptions options, IMessageTypeCatalog catalog) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(catalog);
    _options = options;
    foreach (var entry in catalog.GetAll()) {
      if (entry.SequenceIntegrity is { } mode) {
        _byType[entry.Type] = (mode, entry.SequenceHoldMilliseconds);
      }
    }
  }

  /// <summary>
  /// The registered instance, or one built from the host's options and catalog when the host did not register it.
  /// </summary>
  public static SequenceIntegrityPolicy Resolve(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<SequenceIntegrityPolicy>() ?? Create(services);
  }

  /// <summary>Builds an instance from what the container holds, with the framework defaults for anything it lacks.</summary>
  public static SequenceIntegrityPolicy Create(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new SequenceIntegrityPolicy(
      services.GetService<WhizbangCoreOptions>() ?? new WhizbangCoreOptions(),
      services.GetService<IMessageTypeCatalog>() ?? NullMessageTypeCatalog.Instance);
  }

  /// <summary>The hold for an event of <paramref name="messageType"/>, given the dispatch's own settings.</summary>
  /// <param name="messageType">The event's type.</param>
  /// <param name="callMode">The dispatch's own mode, or null to defer to the type and the configured default.</param>
  /// <param name="callHoldTime">The dispatch's own hold time, or null.</param>
  public SequenceHold For(Type messageType, SequenceIntegrity? callMode = null, TimeSpan? callHoldTime = null) {
    ArgumentNullException.ThrowIfNull(messageType);
    var declared = _byType.TryGetValue(messageType, out var type) ? type : ((SequenceIntegrity Mode, int HoldMilliseconds)?)null;
    var mode = callMode ?? declared?.Mode ?? _options.SequenceIntegrity;
    var holdTime = callHoldTime
      ?? (declared is { HoldMilliseconds: > 0 } named ? TimeSpan.FromMilliseconds(named.HoldMilliseconds) : _options.SequenceHoldTime);
    return new SequenceHold(mode, holdTime);
  }
}
