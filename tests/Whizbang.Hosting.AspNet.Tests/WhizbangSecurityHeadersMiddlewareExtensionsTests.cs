using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers the WhizbangSecurityHeadersMiddlewareExtensions wrapper — both the
/// default-options path and the caller-supplied configure delegate.
/// </summary>
public class WhizbangSecurityHeadersMiddlewareExtensionsTests {

  [Test]
  public async Task UseWhizbangSecurityHeaders_WithoutConfigure_ReturnsSameBuilderAsync() {
    var services = new ServiceCollection();
    var builder = new ApplicationBuilder(services.BuildServiceProvider());

    var returned = builder.UseWhizbangSecurityHeaders();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }

  [Test]
  public async Task UseWhizbangSecurityHeaders_WithConfigure_InvokesCallerDelegateAsync() {
    var services = new ServiceCollection();
    var builder = new ApplicationBuilder(services.BuildServiceProvider());
    WhizbangSecurityHeadersOptions? captured = null;

    builder.UseWhizbangSecurityHeaders(options => {
      captured = options;
      options.Enabled = false;
    });

    await Assert.That(captured).IsNotNull();
    await Assert.That(captured!.Enabled).IsFalse();
  }

  [Test]
  public async Task UseWhizbangSecurityHeaders_WithoutConfigure_UsesDefaultOptionsAsync() {
    var defaults = new WhizbangSecurityHeadersOptions();

    await Assert.That(defaults.Enabled).IsTrue();
    await Assert.That(defaults.XContentTypeOptions).IsEqualTo("nosniff");
  }
}
