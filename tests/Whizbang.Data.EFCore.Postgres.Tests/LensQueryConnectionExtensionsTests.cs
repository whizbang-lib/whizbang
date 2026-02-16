using System.Data.Common;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for LensQueryConnectionExtensions - raw SQL and connection access.
/// Provides escape hatch for queries not expressible in LINQ.
/// </summary>
[Category("RawSql")]
public class LensQueryConnectionExtensionsTests {
  [Test]
  public async Task LensQueryConnectionExtensions_HasExecuteSqlAsyncMethodAsync() {
    // Assert - ExecuteSqlAsync allows parameterized raw SQL queries
    var method = typeof(LensQueryConnectionExtensions).GetMethod("ExecuteSqlAsync");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.IsStatic).IsTrue();
    await Assert.That(method.IsPublic).IsTrue();
  }

  [Test]
  public async Task LensQueryConnectionExtensions_ExecuteSqlAsync_IsExtensionMethodAsync() {
    // Assert - ExecuteSqlAsync extends ILensQuery
    var method = typeof(LensQueryConnectionExtensions).GetMethod("ExecuteSqlAsync");
    await Assert.That(method).IsNotNull();

    // Extension methods have "this" parameter first
    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsGreaterThanOrEqualTo(2);
    // First param should be the extended type (ILensQuery<>)
    await Assert.That(parameters[0].ParameterType.Name).Contains("ILensQuery");
  }

  [Test]
  public async Task LensQueryConnectionExtensions_ExecuteSqlAsync_TakesFormattableStringAsync() {
    // Assert - Uses FormattableString for parameterized queries (SQL injection safe)
    var method = typeof(LensQueryConnectionExtensions).GetMethod("ExecuteSqlAsync");
    var parameters = method!.GetParameters();

    // Second parameter should be FormattableString for interpolation-based parameters
    await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(FormattableString));
  }

  [Test]
  public async Task LensQueryConnectionExtensions_HasGetConnectionMethodAsync() {
    // Assert - GetConnection returns synchronously-accessed connection
    var method = typeof(LensQueryConnectionExtensions).GetMethod("GetConnection");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.IsStatic).IsTrue();
    await Assert.That(method.IsPublic).IsTrue();
  }

  [Test]
  public async Task LensQueryConnectionExtensions_GetConnection_ReturnsDbConnectionAsync() {
    // Assert - Returns DbConnection for direct access
    var method = typeof(LensQueryConnectionExtensions).GetMethod("GetConnection");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(DbConnection));
  }

  [Test]
  public async Task LensQueryConnectionExtensions_HasGetConnectionAsyncMethodAsync() {
    // Assert - GetConnectionAsync opens connection asynchronously
    var method = typeof(LensQueryConnectionExtensions).GetMethod("GetConnectionAsync");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.IsStatic).IsTrue();
    await Assert.That(method.IsPublic).IsTrue();
  }

  [Test]
  public async Task LensQueryConnectionExtensions_GetConnectionAsync_ReturnsTaskDbConnectionAsync() {
    // Assert - Returns Task<DbConnection> for async access
    var method = typeof(LensQueryConnectionExtensions).GetMethod("GetConnectionAsync");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task<DbConnection>));
  }

  [Test]
  public async Task LensQueryConnectionExtensions_GetConnectionAsync_HasCancellationTokenAsync() {
    // Assert - GetConnectionAsync accepts cancellation token
    var method = typeof(LensQueryConnectionExtensions).GetMethod("GetConnectionAsync");
    var parameters = method!.GetParameters();

    // Should have cancellation token as optional parameter
    var ctParam = parameters.FirstOrDefault(p => p.Name == "cancellationToken");
    await Assert.That(ctParam).IsNotNull();
    await Assert.That(ctParam!.HasDefaultValue).IsTrue();
  }

  [Test]
  public async Task LensQueryConnectionExtensions_AllMethodsAreGenericAsync() {
    // Assert - All methods are generic over TModel
    var executeMethod = typeof(LensQueryConnectionExtensions).GetMethod("ExecuteSqlAsync");
    var getMethod = typeof(LensQueryConnectionExtensions).GetMethod("GetConnection");
    var getAsyncMethod = typeof(LensQueryConnectionExtensions).GetMethod("GetConnectionAsync");

    await Assert.That(executeMethod!.IsGenericMethod).IsTrue();
    await Assert.That(getMethod!.IsGenericMethod).IsTrue();
    await Assert.That(getAsyncMethod!.IsGenericMethod).IsTrue();
  }
}
