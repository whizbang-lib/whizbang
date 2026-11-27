// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the anchor of the handling a receptor enters when it is invoked locally from inside another receptor's
/// handling (#2633): the id every id it derives, sends or emits anchors on, so a retry of the outer handling, which invokes
/// it again in the same order, derives the same ids.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout:</strong> the shared derived layout (see <see cref="DerivedIdentity"/>) at the outer anchor's
/// position: the outer anchor's first 80 bits, the call's ordinal among the outer handling's local invocations, then the
/// low 36 bits of SHA-256 over the UTF-8 canonical string
/// <c>whizbang.local-invoke.in-handling.v1\n{outerAnchor:N}\n{service}\n{handler}\n{outerReceptor}\n{ordinal}</c>.
/// </para>
/// <para>
/// The ordinals are the outer handling's own (<see cref="NextOrdinal"/>), not its send ordinals: a void local invoke
/// mints no message id, so taking a send ordinal for it would shift the ids of every later send in the outer body. It is
/// the rule a nested local cascade follows (a wrapper envelope carrying an anchor of its own), so one rule anchors every
/// nested handling.
/// </para>
/// <para>
/// The value is a compatibility contract: every id the inner receptor derives starts from it, so changing the layout
/// changes the ids a retry across the deploy derives.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/LocalInvocationIdentityTests.cs</tests>
public static class LocalInvocationIdentity {
  private const string PREFIX = "whizbang.local-invoke.in-handling.v1";

  private static readonly ConditionalWeakTable<IMessageEnvelope, StrongBox<int>> _ordinals = [];

  /// <summary>
  /// Derives the anchor of the <paramref name="ordinal"/>th local invocation <paramref name="outerReceptorId"/> makes
  /// while <paramref name="serviceName"/> handles the message anchored on <paramref name="outerAnchor"/>.
  /// </summary>
  /// <param name="outerAnchor">The anchor of the outer handling (<see cref="ReceptorHandling.AnchorOf"/>). Must not be empty.</param>
  /// <param name="serviceName">The service handling the message.</param>
  /// <param name="handlerName">The handler (inbox row) of the outer handling, or null when it has none.</param>
  /// <param name="outerReceptorId">The receptor whose body makes the call, or null when the handling names none.</param>
  /// <param name="ordinal">Zero-based position of the call among the outer handling's local invocations.</param>
  /// <returns>A version 7 id that is identical for identical inputs.</returns>
  /// <exception cref="ArgumentException">The outer anchor is empty.</exception>
  /// <exception cref="ArgumentNullException">The service name is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
  public static Guid Derive(Guid outerAnchor, string serviceName, string? handlerName, string? outerReceptorId, int ordinal) {
    if (outerAnchor == Guid.Empty) {
      throw new ArgumentException("A local invocation's anchor needs the anchor of the handling it is made in.", nameof(outerAnchor));
    }
    ArgumentNullException.ThrowIfNull(serviceName);
    ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

    var canonical = string.Concat(
      PREFIX, "\n",
      outerAnchor.ToString("N"), "\n",
      serviceName, "\n",
      handlerName ?? string.Empty, "\n",
      outerReceptorId ?? string.Empty, "\n",
      ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return DerivedIdentity.FromCanonical(outerAnchor, ordinal, canonical);
  }

  /// <summary>
  /// Returns the next zero-based ordinal for a local invocation made in <paramref name="handling"/>. Keyed by the
  /// handling's envelope instance, as <see cref="SentMessageIdentity.NextOrdinal"/> is, so a redelivery, which
  /// materializes a fresh envelope, starts again at zero.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="handling"/> is null.</exception>
  internal static int NextOrdinal(IMessageEnvelope handling) {
    ArgumentNullException.ThrowIfNull(handling);
    var counter = _ordinals.GetValue(handling, static _ => new StrongBox<int>());
    return Interlocked.Increment(ref counter.Value) - 1;
  }
}
