using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;

namespace Whizbang.Sagas;

/// <summary>
/// Which saga follows which, composed across every assembly that declared one.
/// </summary>
/// <remarks>
/// <para>
/// A generator sees only its own compilation, and a saga declared in a library is commonly composed
/// by a host that the library knows nothing about. So each assembly records what it declared and the
/// records compose as assemblies load, with no reflection, the same shape the index, message-type,
/// and query-exposure registries use. Keyed by name rather than by type because that is what a saga
/// event carries on the wire, so a continuation survives a rename of the class behind it.
/// </para>
/// <para>
/// Registration is idempotent. A library and the host that composes it can both register the same
/// declaration, and a chain recorded twice would ask for the follow-on twice.
/// </para>
/// <para>
/// Each parent's set is replaced rather than mutated, so a caller walking a saga's continuations
/// holds a set that no concurrent registration changes, with no lock on the completion path.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs</tests>
public static class SagaContinuationRegistry {
  private static readonly ConcurrentDictionary<string, ImmutableArray<SagaContinuation>> _byParent =
    new(StringComparer.Ordinal);

  /// <summary>
  /// Records that a saga follows another. Called from generated registration; idempotent.
  /// </summary>
  /// <param name="parentSagaName">The name of the saga that must finish first.</param>
  /// <param name="continuation">The saga to start when it does.</param>
  /// <exception cref="ArgumentException"><paramref name="parentSagaName"/> is blank.</exception>
  /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is null.</exception>
  public static void Register(string parentSagaName, SagaContinuation continuation) {
    ArgumentException.ThrowIfNullOrWhiteSpace(parentSagaName);
    ArgumentNullException.ThrowIfNull(continuation);

    _byParent.AddOrUpdate(
      parentSagaName,
      _ => [continuation],
      (_, held) => _appended(held, continuation));
  }

  /// <summary>The sagas that follow this one, in declaration order.</summary>
  /// <param name="parentSagaName">The name of the saga that finished.</param>
  /// <returns>Its continuations, empty when nothing follows it.</returns>
  /// <exception cref="ArgumentException"><paramref name="parentSagaName"/> is blank.</exception>
  public static IReadOnlyList<SagaContinuation> For(string parentSagaName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(parentSagaName);

    return _byParent.TryGetValue(parentSagaName, out var held) ? held : [];
  }

  /// <summary>
  /// The set plus the continuation, or the set unchanged when it already names that saga.
  /// </summary>
  private static ImmutableArray<SagaContinuation> _appended(
      ImmutableArray<SagaContinuation> held, SagaContinuation continuation) =>
    held.Any(existing => string.Equals(existing.SagaName, continuation.SagaName, StringComparison.Ordinal))
      ? held
      : held.Add(continuation);
}
