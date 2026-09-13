namespace Whizbang.Sagas.Helpers;

/// <summary>
/// The claim-key convention for asking a follow-on saga to start, so concurrent terminal callers
/// collapse to one request.
/// </summary>
/// <remarks>
/// <para>
/// Convention: <c>"saga-continuation:{parentSagaName}:{parentSagaId}:{continuationSagaName}"</c>.
/// </para>
/// <para>
/// Deliberately not the completion claim key. The obvious placement for a continuation is "publish it
/// when this caller wins the completion claim", but then a process that dies between winning that
/// claim and publishing the request loses the chain permanently: the completion claim is taken, so no
/// retry and no watchdog tick will drive it again. A separate key lets every caller that reaches
/// terminal attempt the request, including the ones that lost the completion claim, while the claim
/// still collapses them to a single emission.
/// </para>
/// <para>
/// The continuation's name is in the key because a saga can be followed by several, and one key
/// across all of them would start only the first.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs:TheContinuationClaimKeyIsItsOwnAsync</tests>
public static class SagaContinuationGuard {

  /// <summary>Builds the continuation claim key for the dispatcher.</summary>
  /// <param name="parentSagaName">The name of the saga that finished.</param>
  /// <param name="parentSagaId">The finished saga's stream id.</param>
  /// <param name="continuationSagaName">The name of the saga being asked to start.</param>
  /// <exception cref="ArgumentException">Either name is blank.</exception>
  public static string ClaimKey(string parentSagaName, Guid parentSagaId, string continuationSagaName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(parentSagaName);
    ArgumentException.ThrowIfNullOrWhiteSpace(continuationSagaName);

    return $"saga-continuation:{parentSagaName}:{parentSagaId}:{continuationSagaName}";
  }
}
