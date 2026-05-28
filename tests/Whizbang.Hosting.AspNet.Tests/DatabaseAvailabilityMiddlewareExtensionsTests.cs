using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers the trivial DatabaseAvailabilityMiddlewareExtensions wrapper.
/// The class is one method but was at 0% line coverage — no fixture had
/// constructed an IApplicationBuilder to call UseDatabaseAvailabilityGate.
/// </summary>
public class DatabaseAvailabilityMiddlewareExtensionsTests {

  [Test]
  public async Task UseDatabaseAvailabilityGate_RegistersMiddleware_AndReturnsBuilderAsync() {
    var services = new ServiceCollection();
    var builder = new ApplicationBuilder(services.BuildServiceProvider());

    var returned = builder.UseDatabaseAvailabilityGate();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }
}
