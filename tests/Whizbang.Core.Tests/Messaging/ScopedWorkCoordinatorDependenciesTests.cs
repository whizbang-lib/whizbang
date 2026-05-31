using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the public surface of <see cref="ScopedWorkCoordinatorDependencies"/> —
/// the dependency-bag record used to inject lifecycle / tracing / scope-factory
/// extras into <see cref="ScopedWorkCoordinatorStrategy"/>. Four init properties,
/// all nullable; tests verify they round-trip via object initializer, default
/// to null, and participate in record value equality.
/// </summary>
/// <docs>fundamentals/work-coordinator/strategies</docs>
public class ScopedWorkCoordinatorDependenciesTests {

  [Test]
  public async Task Default_AllPropertiesNullAsync() {
    var deps = new ScopedWorkCoordinatorDependencies();

    await Assert.That(deps.ScopeFactory).IsNull();
    await Assert.That(deps.LifecycleMessageDeserializer).IsNull();
    await Assert.That(deps.TracingOptions).IsNull();
    await Assert.That(deps.SystemEventOptions).IsNull();
  }

  [Test]
  public async Task InitProperties_RoundTripValuesViaObjectInitializerAsync() {
    var sf = new ServiceCollection().BuildServiceProvider().GetService<IServiceScopeFactory>()!;
    var systemEventOptions = new SystemEventOptions();

    var deps = new ScopedWorkCoordinatorDependencies {
      ScopeFactory = sf,
      SystemEventOptions = systemEventOptions,
    };

    await Assert.That(deps.ScopeFactory).IsSameReferenceAs(sf);
    await Assert.That(deps.SystemEventOptions).IsSameReferenceAs(systemEventOptions);
  }

  [Test]
  public async Task RecordEquality_SamePropertiesAreEqualAsync() {
    var sf = new ServiceCollection().BuildServiceProvider().GetService<IServiceScopeFactory>()!;
    var systemEventOptions = new SystemEventOptions();

    var a = new ScopedWorkCoordinatorDependencies {
      ScopeFactory = sf,
      SystemEventOptions = systemEventOptions,
    };
    var b = new ScopedWorkCoordinatorDependencies {
      ScopeFactory = sf,
      SystemEventOptions = systemEventOptions,
    };

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task RecordEquality_DifferentPropertiesAreNotEqualAsync() {
    var sf = new ServiceCollection().BuildServiceProvider().GetService<IServiceScopeFactory>()!;

    var a = new ScopedWorkCoordinatorDependencies { ScopeFactory = sf };
    var b = new ScopedWorkCoordinatorDependencies();

    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task WithExpression_CopiesAndOverridesPropertyAsync() {
    var sf = new ServiceCollection().BuildServiceProvider().GetService<IServiceScopeFactory>()!;
    var original = new ScopedWorkCoordinatorDependencies { ScopeFactory = sf };

    // record `with` expression — shallow copy + override.
    var updated = original with { ScopeFactory = null };

    await Assert.That(original.ScopeFactory).IsSameReferenceAs(sf);
    await Assert.That(updated.ScopeFactory).IsNull();
  }
}
