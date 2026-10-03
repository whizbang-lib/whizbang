using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Binds each NAMED <see cref="CircuitBreakerOptions"/> instance from
/// <c>Whizbang:CircuitBreakers:&lt;name&gt;</c>, after the code's <c>Configure</c> callbacks, so an
/// operator can retune one breaker during an incident without a redeploy and without touching
/// the others. The unnamed instance has no section and keeps its code values.
/// </summary>
/// <remarks>
/// A breaker picks up its section by being resolved by name:
/// <c>IOptionsMonitor&lt;CircuitBreakerOptions&gt;.Get("payments")</c>. The <c>Bind</c> call has a
/// concrete type, so the binder source generator compiles it to typed assignments and no
/// reflection reaches the AOT path.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#circuit-breakers</docs>
/// <tests>tests/Whizbang.Core.Tests/Resilience/CircuitBreakerOptionsConfigurationTests.cs</tests>
internal sealed class CircuitBreakerOptionsPostConfigure(IServiceProvider services)
  : IPostConfigureOptions<CircuitBreakerOptions> {

  /// <summary>Parent section; each child key is a breaker name.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:CircuitBreakers";

  public void PostConfigure(string? name, CircuitBreakerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(name)) {
      return;
    }

    // TryAddWhizbangDefaults, which registers this binder, also guarantees an IConfiguration.
    var configuration = services.GetRequiredService<IConfiguration>();

#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    ConfigurationBinder.Bind(configuration.GetSection(CONFIGURATION_SECTION).GetSection(name), options);
#pragma warning restore IL2026
  }
}
