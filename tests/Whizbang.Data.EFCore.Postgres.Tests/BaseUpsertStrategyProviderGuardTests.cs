using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Regression lock for the order-dependent flake where MultiPerspectiveUpsertSymmetryTests
/// failed in CI but passed locally: the static `PathOnePersistenceOptionsProvider` was set
/// by a generated ModuleInitializer (Release-only generator output), then atomic UPSERT
/// triggered against InMemoryDatabase fixtures and failed on JsonTypeInfo missing for
/// nested test models. Now the atomic path additionally checks `Database.ProviderName`
/// and bails out for non-Npgsql providers.
/// </summary>
public class BaseUpsertStrategyProviderGuardTests {

  private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options) { }

  [Test]
  public async Task InMemoryProvider_DoesNotTriggerAtomicUpsertPathAsync() {
    // Set a non-null provider so the only reason to bail must be the Npgsql guard.
    var prev = BaseUpsertStrategy.PathOnePersistenceOptionsProvider;
    try {
      BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () => new JsonSerializerOptions {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
      };

      var options = new DbContextOptionsBuilder<ProbeDbContext>()
        .UseInMemoryDatabase($"probe-{Guid.NewGuid()}")
        .Options;
      await using var ctx = new ProbeDbContext(options);

      // ProviderName for the in-memory provider does not contain "Npgsql".
      var providerName = ctx.Database.ProviderName;
      await Assert.That(providerName).IsNotNull();
      await Assert.That(providerName!.Contains("Npgsql", StringComparison.Ordinal)).IsFalse();
    } finally {
      BaseUpsertStrategy.PathOnePersistenceOptionsProvider = prev;
    }
  }
}
