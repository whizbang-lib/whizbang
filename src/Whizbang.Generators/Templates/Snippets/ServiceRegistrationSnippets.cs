using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Code snippets for service registration generation.
/// These snippets are extracted by the generator and used to generate code.
/// Placeholders:
/// - __USER_INTERFACE__: The user-defined interface (e.g., global::MyApp.IOrderLens)
/// - __CONCRETE_CLASS__: The concrete implementation class (e.g., global::MyApp.OrderLens)
/// </summary>
internal static class ServiceRegistrationSnippets {

  // Example method showing interface registration snippet structure (for IDE support only)
  private static void InterfaceRegistrationExample(IServiceCollection services) {
    #region INTERFACE_REGISTRATION_SNIPPET
    services.AddScoped<__USER_INTERFACE__, __CONCRETE_CLASS__>();
    #endregion
  }

  // Example method showing self-registration snippet structure (for IDE support only)
  private static void SelfRegistrationExample(IServiceCollection services, ServiceRegistrationOptions options) {
    #region SELF_REGISTRATION_SNIPPET
    if (options.IncludeSelfRegistration) {
      services.AddScoped<__CONCRETE_CLASS__>();
    }
    #endregion
  }
}

/// <summary>
/// Placeholder options class for IDE support in snippets.
/// The real ServiceRegistrationOptions is generated in the output.
/// </summary>
internal sealed class ServiceRegistrationOptions {
  public bool IncludeSelfRegistration { get; set; } = true;
}
