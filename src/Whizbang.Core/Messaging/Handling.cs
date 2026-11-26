// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The receptor handling the current code runs in: the handled message, the service handling it and the receptor whose
/// body runs. A strong id derives from it (<c>OrderId.DeriveFrom(Handling.Current, "order")</c>, #2604), so a retry of
/// the handling, which handles the same message again, derives the same id instead of minting a new one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is the handling of the receptor body the calling code runs in, flowing with the execution
/// context. Outside a receptor body it is a handling that is not <see cref="IsActive"/>, and deriving from it throws: there
/// is no handled message to derive from, so a retry could not derive the same id again.
/// </para>
/// <para>
/// A handling ends when its receptor body returns. Deriving from a handling that has ended throws too, so work the body
/// started and did not await cannot derive ids that no retry would reproduce.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#derived-ids</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StrongIdDerivationTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/ReceptorHandlingTests.cs</tests>
public sealed class Handling {
  private static readonly Handling _none = new(null, string.Empty, null);

  private readonly object _gate = new();
  private HashSet<(Guid PinnedId, string Purpose)>? _purposes;
  private volatile bool _ended;

  internal Handling(IMessageEnvelope? envelope, string serviceName, string? receptorId) {
    Envelope = envelope;
    ServiceName = serviceName;
    ReceptorId = receptorId;
  }

  /// <summary>The handling the calling code runs in; one that is not <see cref="IsActive"/> outside a receptor body.</summary>
  public static Handling Current => ReceptorHandling.CurrentHandling ?? _none;

  /// <summary>True inside a receptor body whose handling has not ended: a strong id can derive from it.</summary>
  public bool IsActive => Envelope is not null && !_ended;

  /// <summary>The envelope of the handled message, or null for the handling outside any receptor body.</summary>
  internal IMessageEnvelope? Envelope { get; }

  /// <summary>The name of the service handling the message.</summary>
  internal string ServiceName { get; }

  /// <summary>The receptor whose body runs in the handling, or null when the handling names none.</summary>
  internal string? ReceptorId { get; }

  /// <summary>True once the receptor body that ran in this handling has returned.</summary>
  internal bool HasEnded => _ended;

  /// <summary>Ends the handling: its receptor body has returned.</summary>
  internal void End() => _ended = true;

  /// <summary>
  /// Records that the strong id pinned as <paramref name="pinnedId"/> derived its id for <paramref name="purpose"/> in this
  /// handling. Returns false when it already has: one id per purpose, so a loop that derives the same purpose for every
  /// item fails loudly instead of giving every item the same id.
  /// </summary>
  internal bool TryClaim(Guid pinnedId, string purpose) {
    lock (_gate) {
      _purposes ??= [];
      return _purposes.Add((pinnedId, purpose));
    }
  }
}
