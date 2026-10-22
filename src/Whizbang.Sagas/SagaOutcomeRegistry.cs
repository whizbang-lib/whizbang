// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Sagas;

/// <summary>
/// Which sagas signal a commit-or-rollback outcome, and with which policy, composed across every
/// assembly that declared one.
/// </summary>
/// <remarks>
/// <para>
/// Composed per assembly for the reason <see cref="SagaContinuationRegistry"/> is: a generator sees only
/// its own compilation, and a saga declared in a library is commonly composed by a host the library
/// knows nothing about. Keyed by name, which is what a saga event carries on the wire.
/// </para>
/// <para>
/// Registration is idempotent and the first declaration stands. A library and the host that composes
/// it can both register the same saga, and a second registration that changed the policy mid-flight
/// would give runs of one saga different outcomes for the same status.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback#declaring-it</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
public static class SagaOutcomeRegistry {
  private static readonly ConcurrentDictionary<string, SagaOutcomePolicy> _bySaga = new(StringComparer.Ordinal);

  /// <summary>
  /// Records that a saga signals its outcome. Called from generated registration; idempotent.
  /// </summary>
  /// <param name="sagaName">The saga's name, as passed to its <c>[Saga("Name")]</c>.</param>
  /// <param name="policy">How its final status maps to commit or rollback.</param>
  /// <exception cref="ArgumentException"><paramref name="sagaName"/> is blank.</exception>
  /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
  public static void Register(string sagaName, SagaOutcomePolicy policy) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    ArgumentNullException.ThrowIfNull(policy);
    _bySaga.TryAdd(sagaName, policy);
  }

  /// <summary>The saga's outcome policy, or <see langword="null"/> when it does not signal one.</summary>
  /// <param name="sagaName">The saga's name.</param>
  /// <exception cref="ArgumentException"><paramref name="sagaName"/> is blank.</exception>
  public static SagaOutcomePolicy? For(string sagaName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    return _bySaga.TryGetValue(sagaName, out var policy) ? policy : null;
  }
}
