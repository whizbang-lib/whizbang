using Microsoft.Extensions.DependencyInjection;
using Whizbang.Generators.Templates.Placeholders;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Code snippets for perspective registration generation.
/// These snippets are extracted by the generator and used to generate code.
/// </summary>
internal static class PerspectiveSnippets {

  // Example method showing snippet structure (for IDE support only)
  private static void ExampleMethod(IServiceCollection services) {
    #region PERSPECTIVE_REGISTRATION_SNIPPET
    services.AddScoped<__PERSPECTIVE_INTERFACE__<__TYPE_ARGUMENTS__>, __PERSPECTIVE_CLASS__>();
    #endregion
  }
}
