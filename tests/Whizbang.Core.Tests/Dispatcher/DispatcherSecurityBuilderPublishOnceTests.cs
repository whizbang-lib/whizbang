using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// <see cref="DispatcherSecurityBuilder.PublishOnceAsync{TEvent}"/>: background work publishes in a
/// chosen tenant's context, and at most once however many instances attempt it.
/// </summary>
/// <remarks>
/// The saga sweep runs on every replica and publishes from a worker with no request context. Without
/// the tenant the event is handled in no tenant at all; without the claim every replica publishes it.
/// </remarks>
[NotInParallel("PublishOnce")]
public class DispatcherSecurityBuilderPublishOnceTests {

  public record SecuredOnceEvent([property: StreamId] Guid StreamId) : IEvent;

  private static int _fired;
  private static IScopeContext? _captured;

  private static void _reset() {
    Interlocked.Exchange(ref _fired, 0);
    _captured = null;
  }

  public class SecuredOnceEventReceptor(IScopeContextAccessor accessor) : IReceptor<SecuredOnceEvent> {
    public ValueTask HandleAsync(SecuredOnceEvent message, CancellationToken cancellationToken = default) {
      _record(accessor.Current);
      return ValueTask.CompletedTask;
    }

    private static void _record(IScopeContext? scope) {
      Interlocked.Increment(ref _fired);
      _captured = scope;
    }
  }

  private sealed class InMemoryClaimStore : IClaimedEmissionStore {
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      lock (_lock) {
        return Task.FromResult(_claimed.Add(claimKey));
      }
    }
  }

  [Before(Test)]
  public Task ResetAsync() {
    _reset();
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    return Task.CompletedTask;
  }

  private static IDispatcher _dispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddSingleton<IClaimedEmissionStore>(new InMemoryClaimStore());
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  [Test]
  public async Task ForTenant_FirstClaim_PublishesInThatTenantAsSystemAsync() {
    var dispatcher = _dispatcher();

    var won = await dispatcher.AsSystem().ForTenant("tenant-a")
      .PublishOnceAsync("sweep:1", new SecuredOnceEvent((Guid)TrackedGuid.New()));

    await Assert.That(won).IsTrue();
    await Assert.That(_fired).IsEqualTo(1);
    await Assert.That(_captured?.Scope?.TenantId).IsEqualTo("tenant-a")
      .Because("the event must be handled in the tenant it belongs to, not in whatever the worker happened to have");
    await Assert.That(_captured?.Scope?.UserId).IsEqualTo("SYSTEM");
  }

  [Test]
  public async Task SameKey_SecondAttempt_PublishesNothingAsync() {
    var dispatcher = _dispatcher();

    var first = await dispatcher.AsSystem().ForTenant("tenant-a").PublishOnceAsync("sweep:2", new SecuredOnceEvent((Guid)TrackedGuid.New()));
    var second = await dispatcher.AsSystem().ForTenant("tenant-a").PublishOnceAsync("sweep:2", new SecuredOnceEvent((Guid)TrackedGuid.New()));

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse();
    await Assert.That(_fired).IsEqualTo(1)
      .Because("every replica runs the sweep; only one of them may publish");
  }

  [Test]
  public async Task AfterPublishing_TheCallersContextIsRestoredAsync() {
    var dispatcher = _dispatcher();
    ScopeContextAccessor.CurrentContext = null;

    await dispatcher.AsSystem().ForTenant("tenant-a").PublishOnceAsync("sweep:3", new SecuredOnceEvent((Guid)TrackedGuid.New()));

    await Assert.That(ScopeContextAccessor.CurrentContext).IsNull()
      .Because("the explicit context is for this publish only; leaking it would run the worker's next item as that tenant");
  }
}
