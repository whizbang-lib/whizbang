using Whizbang.Core;
using Whizbang.Core.Dispatch;

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

  /// <summary>
  /// Routes the saga emission through <see cref="IDispatcher.PublishAsync{TEvent}(TEvent, DispatchOptions)"/>
  /// with <see cref="DispatchOptions.ScheduledFor"/> set so the resulting <c>wh_outbox</c> row carries
  /// <c>scheduled_for</c> and is held by the pickup query (mig 040) until the time elapses.
  /// </summary>
  public async Task PublishAsync<TEvent>(TEvent eventData, DateTimeOffset? scheduledFor) where TEvent : IEvent {
    if (scheduledFor is null) {
      await _dispatcher.PublishAsync(eventData).ConfigureAwait(false);
      return;
    }
    var options = new DispatchOptions().WithScheduledFor(scheduledFor.Value);
    await _dispatcher.PublishAsync(eventData, options).ConfigureAwait(false);
  }

  public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
    return _dispatcher.PublishOnceAsync(claimKey, eventData, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Published as the system through the dispatcher's explicit security context: for the saga's tenant
  /// when it has one, for all tenants when sagas are not tenant-scoped.
  /// </remarks>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:PublishOnceInTenantAsync_PublishesAsTheSystemInThatTenantAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:PublishOnceInTenantAsync_NoTenant_PublishesForAllTenantsAsync</tests>
  public Task<bool> PublishOnceInTenantAsync<TEvent>(string? tenantId, string claimKey, TEvent eventData, CancellationToken cancellationToken)
      where TEvent : IEvent {
    var system = _dispatcher.AsSystem();
    var scoped = string.IsNullOrWhiteSpace(tenantId) ? system.ForAllTenants() : system.ForTenant(tenantId);
    return scoped.PublishOnceAsync(claimKey, eventData, cancellationToken);
  }
}
