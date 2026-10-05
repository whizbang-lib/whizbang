// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Default <see cref="ISagaEventEmitter"/> implementation that adapts
/// <see cref="IDispatcher"/>. Registered automatically by
/// <see cref="SagaServiceCollectionExtensions.AddWhizbangSagas"/>.
/// </summary>
/// <remarks>
/// The primary constructor reads and releases claims in <c>claims</c>, the store <c>PublishOnceAsync</c> claims in;
/// the container chooses it whenever a claim store is registered. <c>claims</c> may be <see langword="null"/>.
/// </remarks>
/// <param name="dispatcher">The dispatcher to publish through.</param>
/// <param name="claims">The claim store, or <see langword="null"/> for none.</param>
public sealed class DispatcherSagaEventEmitter(IDispatcher dispatcher, IClaimedEmissionStore? claims) : ISagaEventEmitter {

  private readonly IDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
  private readonly IClaimedEmissionStore? _claims = claims;

  /// <summary>An emitter over <paramref name="dispatcher"/> with no claim store to read or release claims in.</summary>
  /// <param name="dispatcher">The dispatcher to publish through.</param>
  public DispatcherSagaEventEmitter(IDispatcher dispatcher) : this(dispatcher, claims: null) {
  }

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

  /// <inheritdoc />
  /// <remarks>
  /// Runs through the dispatcher's explicit security context, the same one
  /// <see cref="PublishOnceInTenantAsync{TEvent}"/> publishes under, so the reads inside the work and
  /// the tick published after them are made as one identity in one tenant. With no tenant the work runs
  /// in the caller's context, exactly as it did before the sweep read in the saga's tenant.
  /// </remarks>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:RunInTenantAsync_RunsTheWorkAsTheSystemInThatTenantAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:RunInTenantAsync_NoTenant_RunsTheWorkInTheCallersContextAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:RunInTenantAsync_NullWork_ThrowsAsync</tests>
  public Task<TResult> RunInTenantAsync<TResult>(string? tenantId, Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(work);
    return string.IsNullOrWhiteSpace(tenantId)
      ? work(cancellationToken)
      : _dispatcher.AsSystem().ForTenant(tenantId).RunAsync(work, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Reads the claim store. A store that cannot tell, or no store at all, answers that none is held,
  /// which leaves the sweep arming as it did before the abandonment claim existed.
  /// </remarks>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:FindClaimedAndRelease_GoToTheClaimStoreAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:FindClaimed_StoreCannotTellOrNoStore_ReadsAsNoneHeldAsync</tests>
  public async Task<IReadOnlySet<string>> FindClaimedAsync(IReadOnlyCollection<string> claimKeys, CancellationToken cancellationToken) {
    var held = _claims is null ? null : await _claims.FindClaimedAsync(claimKeys, cancellationToken).ConfigureAwait(false);
    return held ?? new HashSet<string>(StringComparer.Ordinal);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs:FindClaimedAndRelease_GoToTheClaimStoreAsync</tests>
  public Task<bool> ReleaseClaimAsync(string claimKey, CancellationToken cancellationToken)
    => _claims is null ? Task.FromResult(false) : _claims.ReleaseAsync(claimKey, cancellationToken);
}
