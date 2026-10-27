// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Observability.Tests.DependencyInjection;

/// <summary>
/// A host has no trace store unless it asks for one (#1601). The in-memory store kept every
/// dispatched envelope for the life of the process and nothing in the library read it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/TraceStoreRegistration.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/ServiceCollectionExtensions.cs</code-under-test>
/// <docs>fundamentals/persistence/observability#trace-store-registration</docs>
[Category("Observability")]
public class TraceStoreRegistrationTests {
  [Test]
  public async Task AddWhizbang_Default_RegistersNoTraceStoreAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();

    _ = services.AddWhizbang();

    await Assert.That(services.Any(d => d.ServiceType == typeof(ITraceStore))).IsFalse()
      .Because("an unbounded store registered by default grows with every dispatch");
    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetService<ITraceStore>()).IsNull();
  }

  [Test]
  public async Task AddWhizbangInMemoryTraceStore_RegistersOneSingletonWithTheDefaultCapacityAsync() {
    var services = new ServiceCollection();

    var returned = services.AddWhizbangInMemoryTraceStore();

    await Assert.That(returned).IsSameReferenceAs(services);
    var descriptors = services.Where(d => d.ServiceType == typeof(ITraceStore)).ToList();
    await Assert.That(descriptors).Count().IsEqualTo(1);
    await Assert.That(descriptors[0].Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredService<ITraceStore>();
    await Assert.That(store).IsTypeOf<InMemoryTraceStore>();
    await Assert.That(((InMemoryTraceStore)store).Capacity).IsEqualTo(InMemoryTraceStore.DEFAULT_CAPACITY);
    await Assert.That(provider.GetRequiredService<ITraceStore>()).IsSameReferenceAs(store);
  }

  [Test]
  public async Task AddWhizbangInMemoryTraceStore_WithCapacity_UsesItAsync() {
    var services = new ServiceCollection();

    services.AddWhizbangInMemoryTraceStore(250);

    await using var provider = services.BuildServiceProvider();
    var store = (InMemoryTraceStore)provider.GetRequiredService<ITraceStore>();
    await Assert.That(store.Capacity).IsEqualTo(250);
  }

  [Test]
  public async Task AddWhizbangInMemoryTraceStore_CalledAfterAnotherStore_LeavesExactlyOneAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<ITraceStore>(new InMemoryTraceStore(5));

    services.AddWhizbangInMemoryTraceStore(7);
    services.AddWhizbangInMemoryTraceStore(9);

    await Assert.That(services.Count(d => d.ServiceType == typeof(ITraceStore))).IsEqualTo(1);
    await using var provider = services.BuildServiceProvider();
    await Assert.That(((InMemoryTraceStore)provider.GetRequiredService<ITraceStore>()).Capacity).IsEqualTo(9)
      .Because("the last call names the store the host asked for");
  }

  [Test]
  public async Task AddWhizbangInMemoryTraceStore_CapacityNotPositive_ThrowsAtRegistrationAsync() {
    var services = new ServiceCollection();

    var exception = await Assert.That(() => services.AddWhizbangInMemoryTraceStore(0))
      .ThrowsExactly<ArgumentOutOfRangeException>();

    await Assert.That(exception!.ParamName).IsEqualTo("capacity");
    await Assert.That(services).IsEmpty();
  }

  [Test]
  public async Task AddWhizbangInMemoryTraceStore_NullServices_ThrowsAsync() {
    IServiceCollection services = null!;

    await Assert.That(() => services.AddWhizbangInMemoryTraceStore())
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => services.AddWhizbangInMemoryTraceStore(1))
      .ThrowsExactly<ArgumentNullException>();
  }
}
