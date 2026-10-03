using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Binds the ASP.NET hosting options from <c>Whizbang:AspNet:Availability</c>,
/// <c>Whizbang:AspNet:Correlation</c> and <c>Whizbang:AspNet:SecurityHeaders</c> (#1014).
/// </summary>
/// <remarks>
/// Each call names its concrete options type: the binder source generator intercepts
/// <see cref="ConfigurationBinder.Bind(IConfiguration, object?)"/> only when it can see the type at the
/// call site, and a type parameter would fall back to reflection, which is an AOT break (IL3050). A host
/// that registered no <see cref="IConfiguration"/> keeps the code defaults.
/// </remarks>
/// <docs>operations/configuration/configuration-reference</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/AspNetOptionsConfigurationBindingTests.cs</tests>
internal sealed class AspNetOptionsConfigurationBinder(IConfiguration? configuration) :
    IConfigureOptions<WhizbangAvailabilityOptions>,
    IConfigureOptions<WhizbangCorrelationOptions>,
    IConfigureOptions<WhizbangSecurityHeadersOptions> {

  public void Configure(WhizbangAvailabilityOptions options) {
    if (configuration is not null) {
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
      ConfigurationBinder.Bind(configuration.GetSection("Whizbang:AspNet:Availability"), options);
#pragma warning restore IL2026, IL3050
    }
  }

  public void Configure(WhizbangCorrelationOptions options) {
    if (configuration is not null) {
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
      ConfigurationBinder.Bind(configuration.GetSection("Whizbang:AspNet:Correlation"), options);
#pragma warning restore IL2026, IL3050
    }
  }

  public void Configure(WhizbangSecurityHeadersOptions options) {
    if (configuration is not null) {
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
      ConfigurationBinder.Bind(configuration.GetSection("Whizbang:AspNet:SecurityHeaders"), options);
#pragma warning restore IL2026, IL3050
    }
  }
}
