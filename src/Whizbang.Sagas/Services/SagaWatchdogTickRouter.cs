using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Delivers each completion-watchdog tick to the registered saga service that armed it.
/// </summary>
/// <remarks>
/// <para>
/// The tick is framework-owned and shared by every saga, so it carries the saga's name and the
/// router matches on it. Each delivery opens its own scope, since saga services are scoped and a tick
/// arrives on a worker with no ambient scope of its own.
/// </para>
/// <para>
/// A tick with no matching participant is left alone rather than treated as an error: a saga
/// declared with <c>[Saga]</c> receives its ticks through its own generated receptor and is
/// deliberately not a participant here.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#hand-written-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Router_DeliversATickToTheSagaThatArmedItAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Router_WithNoMatchingSaga_DoesNothingAsync</tests>
public sealed class SagaWatchdogTickRouter(IServiceScopeFactory scopeFactory) : IReceptor<SagaCompletionWatchdogTickEvent> {

  /// <inheritdoc/>
  public async ValueTask HandleAsync(SagaCompletionWatchdogTickEvent message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    await using var scope = scopeFactory.CreateAsyncScope();
    var participant = scope.ServiceProvider.GetServices<ISagaWatchdogParticipant>()
      .FirstOrDefault(p => string.Equals(p.SagaName, message.SagaName, StringComparison.Ordinal));
    if (participant is not null) {
      await participant.TryRecoverViaWatchdogTickAsync(message, cancellationToken).ConfigureAwait(false);
    }
  }
}
