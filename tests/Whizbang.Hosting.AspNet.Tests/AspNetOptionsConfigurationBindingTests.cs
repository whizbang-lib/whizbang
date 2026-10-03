using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// #1014: the ASP.NET hosting options bind from <c>Whizbang:AspNet:*</c>, so an operator can change
/// them at deploy time without a code change. Before this, every one of them needed
/// <c>services.Configure&lt;T&gt;(…)</c> and a redeploy.
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/ServiceCollectionExtensions.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference</docs>
public class AspNetOptionsConfigurationBindingTests {

  private static ServiceProvider _build(Dictionary<string, string?> values) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangAspNet();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AvailabilityGate_EveryKeyBindsAsync() {
    using var provider = _build(new() {
      ["Whizbang:AspNet:Availability:Enabled"] = "false",
      ["Whizbang:AspNet:Availability:Mode"] = "AllNonExempt",
      ["Whizbang:AspNet:Availability:ExemptPaths:0"] = "/healthz",
      ["Whizbang:AspNet:Availability:ExemptPaths:1"] = "/status",
    });

    var options = provider.GetRequiredService<IOptions<WhizbangAvailabilityOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.Mode).IsEqualTo(AvailabilityGateMode.AllNonExempt);
    await Assert.That(options.ExemptPaths).IsNotNull();
    await Assert.That(options.ExemptPaths!).IsEquivalentTo(["/healthz", "/status"]);
  }

  [Test]
  public async Task SecurityHeaders_EveryKeyBindsAsync() {
    using var provider = _build(new() {
      ["Whizbang:AspNet:SecurityHeaders:Enabled"] = "false",
      ["Whizbang:AspNet:SecurityHeaders:StrictTransportSecurity"] = "max-age=60",
      ["Whizbang:AspNet:SecurityHeaders:XContentTypeOptions"] = "nosniff-custom",
      ["Whizbang:AspNet:SecurityHeaders:XFrameOptions"] = "SAMEORIGIN",
      ["Whizbang:AspNet:SecurityHeaders:ContentSecurityPolicy"] = "default-src 'self'",
      ["Whizbang:AspNet:SecurityHeaders:ReferrerPolicy"] = "no-referrer",
      ["Whizbang:AspNet:SecurityHeaders:PermissionsPolicy"] = "camera=()",
      ["Whizbang:AspNet:SecurityHeaders:AllowedMethods:0"] = "GET",
      ["Whizbang:AspNet:SecurityHeaders:AllowedMethods:1"] = "POST",
    });

    var options = provider.GetRequiredService<IOptions<WhizbangSecurityHeadersOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.StrictTransportSecurity).IsEqualTo("max-age=60");
    await Assert.That(options.XContentTypeOptions).IsEqualTo("nosniff-custom");
    await Assert.That(options.XFrameOptions).IsEqualTo("SAMEORIGIN");
    await Assert.That(options.ContentSecurityPolicy).IsEqualTo("default-src 'self'");
    await Assert.That(options.ReferrerPolicy).IsEqualTo("no-referrer");
    await Assert.That(options.PermissionsPolicy).IsEqualTo("camera=()");
    await Assert.That(options.AllowedMethods).IsEquivalentTo(["GET", "POST"]);
  }

  [Test]
  public async Task SecurityHeaders_AnEmptyValueBindsAsEmpty_WhichSuppressesTheHeaderAsync() {
    using var provider = _build(new() {
      ["Whizbang:AspNet:SecurityHeaders:PermissionsPolicy"] = "",
    });

    var options = provider.GetRequiredService<IOptions<WhizbangSecurityHeadersOptions>>().Value;

    await Assert.That(options.PermissionsPolicy).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task Correlation_HeaderNamesAppendToTheDefaultAsync() {
    // HeaderNames is a get-only IList with a default entry, so the binder adds to it rather than
    // replacing it. A service configuring one extra header keeps X-Correlation-ID as well, and that
    // is worth pinning: an operator reading only the key would expect a replacement.
    using var provider = _build(new() {
      ["Whizbang:AspNet:Correlation:HeaderNames:0"] = "X-Request-Id",
    });

    var options = provider.GetRequiredService<IOptions<WhizbangCorrelationOptions>>().Value;

    await Assert.That(options.HeaderNames).IsEquivalentTo(["X-Correlation-ID", "X-Request-Id"]);
  }

  [Test]
  public async Task CalledTwice_BindsOnce_SoListsDoNotDoubleAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:AspNet:Correlation:HeaderNames:0"] = "X-Request-Id",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangAspNet();
    services.AddWhizbangAspNet();
    using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<WhizbangCorrelationOptions>>().Value;

    await Assert.That(options.HeaderNames).IsEquivalentTo(["X-Correlation-ID", "X-Request-Id"]);
  }

  [Test]
  public async Task DefaultsSurviveWhenNoSectionIsPresentAsync() {
    using var provider = _build([]);

    var availability = provider.GetRequiredService<IOptions<WhizbangAvailabilityOptions>>().Value;
    var headers = provider.GetRequiredService<IOptions<WhizbangSecurityHeadersOptions>>().Value;
    var correlation = provider.GetRequiredService<IOptions<WhizbangCorrelationOptions>>().Value;

    await Assert.That(availability.Enabled).IsTrue();
    await Assert.That(availability.Mode).IsEqualTo(AvailabilityGateMode.MutationsOnly);
    await Assert.That(headers.XFrameOptions).IsEqualTo("DENY");
    await Assert.That(correlation.HeaderNames).IsEquivalentTo(["X-Correlation-ID"]);
  }

  [Test]
  public async Task NoConfigurationRegistered_StillResolvesDefaultsAsync() {
    // A host that never registers IConfiguration must still get working options rather than a
    // resolution failure, since the binder is registered unconditionally.
    var services = new ServiceCollection();
    services.AddWhizbangAspNet();
    using var provider = services.BuildServiceProvider();

    var headers = provider.GetRequiredService<IOptions<WhizbangSecurityHeadersOptions>>().Value;
    var availability = provider.GetRequiredService<IOptions<WhizbangAvailabilityOptions>>().Value;
    var correlation = provider.GetRequiredService<IOptions<WhizbangCorrelationOptions>>().Value;

    await Assert.That(headers.Enabled).IsTrue();
    await Assert.That(headers.XContentTypeOptions).IsEqualTo("nosniff");
    await Assert.That(availability.Enabled).IsTrue();
    await Assert.That(correlation.HeaderNames).IsEquivalentTo(["X-Correlation-ID"]);
  }

  [Test]
  public async Task CodeConfiguredBeforeRegistration_AConfigurationKeyOverridesItAsync() {
    // Registration order decides, as for every other bound section: a services.Configure<T> made
    // before AddWhizbangAspNet runs first, so a key in appsettings or the environment overrides it.
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:AspNet:SecurityHeaders:XFrameOptions"] = "SAMEORIGIN",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.Configure<WhizbangSecurityHeadersOptions>(o => {
      o.XFrameOptions = "DENY";
      o.ReferrerPolicy = "same-origin";
    });
    services.AddWhizbangAspNet();
    using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<WhizbangSecurityHeadersOptions>>().Value;

    await Assert.That(options.XFrameOptions).IsEqualTo("SAMEORIGIN");
    await Assert.That(options.ReferrerPolicy).IsEqualTo("same-origin")
      .Because("a key that is absent leaves the code value alone");
  }

  [Test]
  public async Task CodeConfiguredAfterRegistration_WinsOverConfigurationAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:AspNet:Availability:Mode"] = "AllNonExempt",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangAspNet();
    services.Configure<WhizbangAvailabilityOptions>(o => o.Mode = AvailabilityGateMode.MutationsOnly);
    using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<WhizbangAvailabilityOptions>>().Value;

    await Assert.That(options.Mode).IsEqualTo(AvailabilityGateMode.MutationsOnly);
  }
}
