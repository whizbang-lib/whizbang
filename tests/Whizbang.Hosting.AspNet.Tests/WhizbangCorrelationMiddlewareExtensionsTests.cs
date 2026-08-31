using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers the WhizbangCorrelationMiddlewareExtensions wrapper.
/// </summary>
public class WhizbangCorrelationMiddlewareExtensionsTests {

  [Test]
  public async Task UseWhizbangCorrelation_ReturnsSameBuilderForChainingAsync() {
    var services = new ServiceCollection();
    var builder = new ApplicationBuilder(services.BuildServiceProvider());

    var returned = builder.UseWhizbangCorrelation();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }
}
