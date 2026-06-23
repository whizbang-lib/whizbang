using Whizbang.Core;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Default <see cref="ISagaEventEmitter"/> implementation that adapts
/// <see cref="IDispatcher"/>. Registered automatically by
/// <see cref="SagaServiceCollectionExtensions.AddWhizbangSagas"/>.
/// </summary>
public sealed class DispatcherSagaEventEmitter(IDispatcher dispatcher) : ISagaEventEmitter {

  private readonly IDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

  public async Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
    await _dispatcher.PublishAsync(eventData).ConfigureAwait(false);
  }

  public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
    return _dispatcher.PublishOnceAsync(claimKey, eventData, cancellationToken);
  }
}
