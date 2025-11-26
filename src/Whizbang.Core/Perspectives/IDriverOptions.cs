using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marker interface for driver selection in fluent perspective configuration API.
/// Extension properties from driver packages (Postgres, InMemory, SqlServer, etc.) extend this interface
/// to provide discoverable driver options via IntelliSense.
/// </summary>
/// <example>
/// Extension property from Whizbang.Data.EFCore.Postgres:
/// <code>
/// extension(IDriverOptions options) {
///     public WhizbangPerspectiveBuilder Postgres {
///         get {
///             // Registration logic
///             return new WhizbangPerspectiveBuilder(options.Services);
///         }
///     }
/// }
/// </code>
/// </example>
public interface IDriverOptions {
  /// <summary>
  /// Gets the service collection for driver registration.
  /// </summary>
  IServiceCollection Services { get; }
}
