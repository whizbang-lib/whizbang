using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests.Discovery;

/// <summary>
/// Tests for DbContextDiscovery helper class.
/// NOTE: DbContextDiscovery is currently not used by any generators.
/// These are stub tests for future use when the helper is integrated.
/// </summary>
public class DbContextDiscoveryTests {

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task IsPotentialDbContext_ClassWithBaseList_ReturnsTrueAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task IsPotentialDbContext_ClassWithoutBaseList_ReturnsFalseAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task Extract_DbContextClass_ReturnsDbContextInfoAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task Extract_NonDbContextClass_ReturnsNullAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task Extract_DbContextWithPerspectives_ExtractsExistingPerspectivesAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task ExtractExistingPerspectives_WithPerspectiveDbSets_ExtractsModelTypesAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("TODO: Implement test when DbContextDiscovery is used by a generator")]
  public async Task ExtractExistingPerspectives_WithoutPerspectives_ReturnsEmptyAsync() {
    await Task.CompletedTask;
  }
}
