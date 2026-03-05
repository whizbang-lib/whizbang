using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry for DbContext and NpgsqlDataSource registration callbacks.
/// Consumer assemblies register their DbContext configuration via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// The callback handles:
/// - NpgsqlDataSource creation with JSON options, EnableDynamicJson(), and UseVector() if needed
/// - DbContext registration with UseNpgsql() and UseVector() if needed
/// </summary>
/// <docs>features/vector-search#turnkey-setup</docs>
public static class DbContextRegistrationRegistry {
  private static readonly List<DbContextRegistration> _registrations = [];
  private static readonly ConditionalWeakTable<IServiceCollection, HashSet<Type>> _invoked = [];
  private static readonly object _lock = new();

  /// <summary>
  /// Represents a DbContext registration with its configuration callback.
  /// </summary>
  /// <param name="DbContextType">The DbContext type this registration handles.</param>
  /// <param name="Callback">
  /// The callback that registers NpgsqlDataSource and DbContext.
  /// The string parameter is an optional connection string name override.
  /// </param>
  private sealed record DbContextRegistration(
      Type DbContextType,
      Action<IServiceCollection, string?> Callback);

  /// <summary>
  /// Registers a callback that will register NpgsqlDataSource and DbContext.
  /// Called by source-generated module initializer in the consumer assembly.
  /// </summary>
  /// <typeparam name="TDbContext">The DbContext type.</typeparam>
  /// <param name="callback">
  /// Callback that registers NpgsqlDataSource and DbContext.
  /// The string parameter is an optional connection string name override that takes precedence
  /// over the ConnectionStringName from the [WhizbangDbContext] attribute.
  /// </param>
  public static void Register<TDbContext>(Action<IServiceCollection, string?> callback)
      where TDbContext : class {
    lock (_lock) {
      Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] Register called for {typeof(TDbContext).FullName}");
      _registrations.Add(new DbContextRegistration(typeof(TDbContext), callback));
      Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] Registry now has {_registrations.Count} registration(s)");
    }
  }

  /// <summary>
  /// Invokes the registered callback for the given DbContext type.
  /// Called by PostgresDriverExtensions.Postgres to register NpgsqlDataSource and DbContext.
  /// Only invokes once per (ServiceCollection, DbContext) pair to prevent duplicate registrations.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type to register.</param>
  /// <param name="connectionStringNameOverride">
  /// Optional connection string name that overrides the default from [WhizbangDbContext] attribute.
  /// When null, the attribute value is used.
  /// </param>
  /// <returns>True if a registration was invoked, false if no matching registration found or already invoked.</returns>
  internal static bool InvokeRegistration(IServiceCollection services, Type dbContextType, string? connectionStringNameOverride = null) {
    lock (_lock) {
      Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] InvokeRegistration called for {dbContextType.FullName}");
      Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] Registry has {_registrations.Count} registration(s)");

      // Get or create the invocation tracking set for this ServiceCollection
      if (!_invoked.TryGetValue(services, out var invokedSet)) {
        invokedSet = [];
        _invoked.Add(services, invokedSet);
      }

      // Skip if already invoked for this DbContext
      if (!invokedSet.Add(dbContextType)) {
        Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] Already invoked for {dbContextType.FullName}, skipping");
        return false;
      }

      // Find matching registration (latest one wins)
      for (var i = _registrations.Count - 1; i >= 0; i--) {
        Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] Checking registration[{i}]: {_registrations[i].DbContextType.FullName}");
        if (_registrations[i].DbContextType == dbContextType) {
          Console.WriteLine("[Whizbang:DbContextRegistrationRegistry] Found match! Invoking callback...");
          _registrations[i].Callback(services, connectionStringNameOverride);
          Console.WriteLine("[Whizbang:DbContextRegistrationRegistry] Callback completed successfully");
          return true;
        }
      }

      Console.WriteLine($"[Whizbang:DbContextRegistrationRegistry] No matching registration found for {dbContextType.FullName}");
      return false;
    }
  }

  /// <summary>
  /// Checks if a registration exists for the given DbContext type.
  /// </summary>
  internal static bool HasRegistration(Type dbContextType) {
    lock (_lock) {
      return _registrations.Any(r => r.DbContextType == dbContextType);
    }
  }

  /// <summary>
  /// Gets all registered DbContext types.
  /// Used by EnsureWhizbangInitializedAsync to initialize all DbContexts.
  /// </summary>
  public static IReadOnlyList<Type> GetRegisteredDbContextTypes() {
    lock (_lock) {
      return _registrations.Select(r => r.DbContextType).Distinct().ToList().AsReadOnly();
    }
  }
}
