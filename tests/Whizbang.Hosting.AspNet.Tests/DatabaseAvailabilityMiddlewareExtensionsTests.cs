using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers the DatabaseAvailabilityMiddlewareExtensions wrapper — both the default and the
/// explicit-exempt-paths overloads.
/// </summary>
public class DatabaseAvailabilityMiddlewareExtensionsTests {

  [Test]
  public async Task UseDatabaseAvailabilityGate_RegistersMiddleware_AndReturnsBuilderAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaReadyGate, SchemaReadyGate>();
    var builder = new ApplicationBuilder(services.BuildServiceProvider());

    var returned = builder.UseDatabaseAvailabilityGate();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }

  [Test]
  public async Task UseDatabaseAvailabilityGate_WithExemptPaths_ReturnsBuilderAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaReadyGate, SchemaReadyGate>();
    var builder = new ApplicationBuilder(services.BuildServiceProvider());

    var returned = builder.UseDatabaseAvailabilityGate(["/probe"]);

    await Assert.That(returned).IsSameReferenceAs(builder);
  }
}
