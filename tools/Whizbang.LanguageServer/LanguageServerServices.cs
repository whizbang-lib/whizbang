using Microsoft.Extensions.DependencyInjection;
using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer;

/// <summary>
/// Service and handler registration for the language server.
/// </summary>
/// <remarks>
/// Separated from the entry point so the registration list can be asserted. The entry point
/// itself binds stdin/stdout and blocks on the server's lifetime, so nothing that runs inside
/// it is reachable from a test — and the registration list is precisely the part that breaks
/// quietly: add a handler class, forget a line here, and the IDE feature simply never responds.
/// </remarks>
public static class LanguageServerServices {
  /// <summary>Default documentation host used when the environment does not override it.</summary>
  public const string DefaultDocsBaseUrl = "https://whizbang-lib.github.io";

  /// <summary>
  /// Resolves the documentation base URL from the environment, falling back to the public site.
  /// </summary>
  /// <remarks>
  /// This URL is what every "go to docs" link in the editor is built from, so an empty or
  /// missing variable has to fall back rather than produce links to nowhere.
  /// </remarks>
  public static string ResolveDocsBaseUrl() {
    var configured = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL");
    return string.IsNullOrWhiteSpace(configured) ? DefaultDocsBaseUrl : configured;
  }

  /// <summary>
  /// Registers every language server service and handler.
  /// </summary>
  /// <param name="services">The collection to register into.</param>
  /// <param name="docsBaseUrl">Documentation host for symbol links.</param>
  /// <returns>The same collection, for chaining.</returns>
  public static IServiceCollection AddLanguageServerServices(
      this IServiceCollection services,
      string docsBaseUrl) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(docsBaseUrl);

    // Services
    services.AddSingleton<MermaidGenerator>();
    services.AddSingleton(new SymbolResolver(docsBaseUrl));
    services.AddSingleton<SearchService>();
    services.AddSingleton<TestCoverageService>();

    // Debug
    services.AddSingleton<DebugSessionManager>();

    // Handlers
    services.AddSingleton<DebugSessionHandler>();
    services.AddSingleton<SearchHandler>();
    services.AddSingleton<SymbolHandler>();
    services.AddSingleton<TestCoverageHandler>();
    services.AddSingleton<FlowDiagramHandler>();
    services.AddSingleton<StatusHandler>();

    return services;
  }
}
