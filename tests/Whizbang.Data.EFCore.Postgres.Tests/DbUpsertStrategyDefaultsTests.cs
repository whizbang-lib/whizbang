using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Covers the default interface method bodies on <see cref="IDbUpsertStrategy"/>.
/// Both forceUpdateScope overloads ship a default that forwards to the shorter
/// overload, so a provider that does not implement scope forcing still works.
/// </summary>
[Category("Shard1")]
public class DbUpsertStrategyDefaultsTests {

  private sealed class StubDbContext : DbContext {
    public StubDbContext() : base(new DbContextOptions<StubDbContext>()) { }
  }

  private sealed class TestModel {
    public string Name { get; init; } = string.Empty;
  }

  /// <summary>
  /// Implements only the abstract overloads so the forceUpdateScope defaults stay
  /// inherited, recording what the defaults forward down to them.
  /// </summary>
  private sealed class RecordingUpsertStrategy : IDbUpsertStrategy {
    public int PlainCallCount { get; private set; }
    public int PhysicalFieldsCallCount { get; private set; }
    public string? LastTableName { get; private set; }
    public Guid LastId { get; private set; }
    public IDictionary<string, object?>? LastPhysicalFieldValues { get; private set; }

    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context,
        string tableName,
        Guid id,
        TModel model,
        PerspectiveMetadata metadata,
        PerspectiveScope scope,
        CancellationToken cancellationToken = default)
        where TModel : class {
      PlainCallCount++;
      LastTableName = tableName;
      LastId = id;
      return Task.CompletedTask;
    }

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context,
        string tableName,
        Guid id,
        TModel model,
        PerspectiveMetadata metadata,
        PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues,
        CancellationToken cancellationToken = default)
        where TModel : class {
      PhysicalFieldsCallCount++;
      LastTableName = tableName;
      LastId = id;
      LastPhysicalFieldValues = physicalFieldValues;
      return Task.CompletedTask;
    }
  }

  [Test]
  public async Task UpsertPerspectiveRowAsync_ForceUpdateScopeDefault_ForwardsToBaseOverloadAsync() {
    var strategy = new RecordingUpsertStrategy();
    IDbUpsertStrategy sut = strategy;
    using var context = new StubDbContext();
    var id = Guid.CreateVersion7();

    await sut.UpsertPerspectiveRowAsync(
        context,
        "order_summary",
        id,
        new TestModel { Name = "test" },
        new PerspectiveMetadata(),
        new PerspectiveScope(),
        forceUpdateScope: true);

    await Assert.That(strategy.PlainCallCount).IsEqualTo(1);
    await Assert.That(strategy.LastTableName).IsEqualTo("order_summary");
    await Assert.That(strategy.LastId).IsEqualTo(id);
  }

  [Test]
  public async Task UpsertPerspectiveRowWithPhysicalFieldsAsync_ForceUpdateScopeDefault_ForwardsToBaseOverloadAsync() {
    var strategy = new RecordingUpsertStrategy();
    IDbUpsertStrategy sut = strategy;
    using var context = new StubDbContext();
    var id = Guid.CreateVersion7();
    var physicalFields = new Dictionary<string, object?> { ["total"] = 42 };

    await sut.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "order_summary",
        id,
        new TestModel { Name = "test" },
        new PerspectiveMetadata(),
        new PerspectiveScope(),
        physicalFields,
        forceUpdateScope: true);

    await Assert.That(strategy.PhysicalFieldsCallCount).IsEqualTo(1);
    await Assert.That(strategy.LastPhysicalFieldValues).IsSameReferenceAs(physicalFields);
    await Assert.That(strategy.LastId).IsEqualTo(id);
  }

  [Test]
  public async Task ForceUpdateScopeDefaults_DoNotCrossWireTheTwoOverloadsAsync() {
    var strategy = new RecordingUpsertStrategy();
    IDbUpsertStrategy sut = strategy;
    using var context = new StubDbContext();

    await sut.UpsertPerspectiveRowAsync(
        context,
        "t",
        Guid.CreateVersion7(),
        new TestModel(),
        new PerspectiveMetadata(),
        new PerspectiveScope(),
        forceUpdateScope: false);

    await Assert.That(strategy.PlainCallCount).IsEqualTo(1);
    await Assert.That(strategy.PhysicalFieldsCallCount).IsEqualTo(0);
  }
}
