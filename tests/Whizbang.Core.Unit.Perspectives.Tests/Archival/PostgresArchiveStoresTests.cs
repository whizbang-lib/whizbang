// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The archive store both Postgres drivers build (#2503): the coordinator over the selected object store, measuring ages on
/// the registered clock and counting sweep refusals when the host has metrics, or the table store when nothing is selected.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresArchiveStores.cs</code-under-test>
public class PostgresArchiveStoresTests {
  private static ServiceCollection _services(bool selected) {
    var services = new ServiceCollection();
    services.AddSingleton(new PostgresArchiveConnection(_ => throw new InvalidOperationException("no database in a unit test"), Schema: null));
    if (selected) {
      services.AddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(new NoObjects(), "code"));
    }
    return services;
  }

  [Test]
  public async Task Create_WithAnObjectStore_IsTheCoordinator_OnTheRegisteredClock_WithMetricsOnlyWhenTheHostHasThemAsync() {
    var clock = new FixedClock();
    var withMetrics = _services(selected: true);
    withMetrics.AddSingleton<TimeProvider>(clock);
    withMetrics.AddSingleton(new WhizbangMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider()
      .GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    await using var first = withMetrics.BuildServiceProvider();
    await using var second = _services(selected: true).BuildServiceProvider();

    var coordinator = (PostgresObjectArchiveStore)PostgresArchiveStores.Create(first);
    var plain = (PostgresObjectArchiveStore)PostgresArchiveStores.Create(second);

    await Assert.That(coordinator.Clock).IsSameReferenceAs(clock);
    await Assert.That(coordinator.Metrics).IsNotNull();
    await Assert.That(plain.Metrics).IsNull().Because("a host without the metrics registration still archives; it counts nothing");
    await Assert.That(coordinator.LocatorFor(Guid.Parse("0199a1b2-0000-7000-8000-000000000001")).ToString())
      .IsEqualTo("test://0199a1b2-0000-7000-8000-000000000001?codec=brotli&format=1");
  }

  [Test]
  public async Task Create_WithNoObjectStore_IsTheTableStoreAsync() {
    await using var provider = _services(selected: false).BuildServiceProvider();

    await Assert.That(PostgresArchiveStores.Create(provider)).IsTypeOf<PostgresTableArchiveStore>();
  }

  private sealed class FixedClock : TimeProvider {
    public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
  }

  private sealed class NoObjects : IArchiveObjectStore {
    public ArchiveLocation Location { get; } = new("test", "https://account.example", "default", "archive");
    public ArchiveObjectStoreSettings Settings { get; } = new();
    public Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public IAsyncEnumerable<ArchiveObjectInfo> ListAsync(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ArchiveObjectInfo>();
  }
}
