using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for ITemporalLensQuery interface definition.
/// Aligned with EF Core SQL Server temporal table query patterns.
/// See: https://learn.microsoft.com/en-us/ef/core/providers/sql-server/temporal-tables
/// </summary>
[Category("TemporalPerspectives")]
public class ITemporalLensQueryTests {
  [Test]
  public async Task ITemporalLensQuery_ExtendsILensQueryAsync() {
    // Assert - ITemporalLensQuery should inherit from marker ILensQuery
    await Assert.That(typeof(ITemporalLensQuery<>).GetInterfaces())
        .Contains(typeof(ILensQuery));
  }

  [Test]
  public async Task ITemporalLensQuery_HasTemporalAllMethodAsync() {
    // Assert - TemporalAll returns all history including Insert/Update/Delete
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("TemporalAll");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(IQueryable<TemporalPerspectiveRow<TestActivityModel>>));
  }

  [Test]
  public async Task ITemporalLensQuery_HasLatestPerStreamMethodAsync() {
    // Assert - LatestPerStream returns most recent row per StreamId
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("LatestPerStream");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(IQueryable<TemporalPerspectiveRow<TestActivityModel>>));
  }

  [Test]
  public async Task ITemporalLensQuery_HasTemporalAsOfMethodAsync() {
    // Assert - TemporalAsOf(DateTime) returns state at a specific point in time
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("TemporalAsOf");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(1);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(DateTimeOffset));
  }

  [Test]
  public async Task ITemporalLensQuery_HasTemporalFromToMethodAsync() {
    // Assert - TemporalFromTo(start, end) returns rows active between two times
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("TemporalFromTo");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(2);
    await Assert.That(parameters[0].Name).IsEqualTo("startTime");
    await Assert.That(parameters[1].Name).IsEqualTo("endTime");
  }

  [Test]
  public async Task ITemporalLensQuery_HasTemporalContainedInMethodAsync() {
    // Assert - TemporalContainedIn(start, end) returns rows contained within range
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("TemporalContainedIn");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(2);
  }

  [Test]
  public async Task ITemporalLensQuery_HasRecentActivityForStreamMethodAsync() {
    // Assert - RecentActivityForStream is a convenience method
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("RecentActivityForStream");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(2);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(Guid));
    await Assert.That(parameters[1].Name).IsEqualTo("limit");
  }

  [Test]
  public async Task ITemporalLensQuery_HasRecentActivityForUserMethodAsync() {
    // Assert - RecentActivityForUser is a convenience method
    var method = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("RecentActivityForUser");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(2);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(string));
    await Assert.That(parameters[1].Name).IsEqualTo("limit");
  }

  [Test]
  public async Task ITemporalLensQuery_ConvenienceMethodsHaveDefaultLimitsAsync() {
    // Assert - Default limit should be 50 for convenience methods
    var streamMethod = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("RecentActivityForStream");
    var userMethod = typeof(ITemporalLensQuery<TestActivityModel>).GetMethod("RecentActivityForUser");

    await Assert.That(streamMethod!.GetParameters()[1].HasDefaultValue).IsTrue();
    await Assert.That(userMethod!.GetParameters()[1].HasDefaultValue).IsTrue();
    await Assert.That(streamMethod!.GetParameters()[1].DefaultValue).IsEqualTo(50);
    await Assert.That(userMethod!.GetParameters()[1].DefaultValue).IsEqualTo(50);
  }
}

// Test model for ITemporalLensQuery tests
internal sealed record TestActivityModel {
  public required string Action { get; init; }
  public required string Description { get; init; }
}
