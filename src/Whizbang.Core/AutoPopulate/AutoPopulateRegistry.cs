using Whizbang.Core.Registry;

namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// Static registry aggregating all auto-populate registries from loaded assemblies.
/// </summary>
/// <remarks>
/// <para>
/// This class uses the <see cref="AssemblyRegistry{T}"/> pattern for multi-assembly
/// contributions. Each assembly with auto-populated message types generates an
/// <see cref="IAutoPopulateRegistry"/> implementation that self-registers via
/// [ModuleInitializer] at load time.
/// </para>
/// <para>
/// <strong>Priority convention:</strong>
/// <list type="bullet">
/// <item>100 = Contracts assemblies (tried first)</item>
/// <item>1000 = Service assemblies (default)</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>attributes/auto-populate</docs>
public static class AutoPopulateRegistry {
  /// <summary>
  /// Register an assembly's auto-populate registry.
  /// Called from generated [ModuleInitializer] code.
  /// </summary>
  /// <param name="registry">The registry to register.</param>
  /// <param name="priority">Lower = tried first. Contracts should use 100, services use 1000.</param>
  public static void Register(IAutoPopulateRegistry registry, int priority = 1000) {
    AssemblyRegistry<IAutoPopulateRegistry>.Register(registry, priority);
  }

  /// <summary>
  /// Get all auto-populate registrations for a message type across all loaded assemblies.
  /// </summary>
  /// <param name="messageType">The message type to get registrations for.</param>
  /// <returns>
  /// All registrations for the message type from all registered assemblies,
  /// ordered by assembly priority (lower priority first).
  /// </returns>
  public static IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) {
    foreach (var registry in AssemblyRegistry<IAutoPopulateRegistry>.GetOrderedContributions()) {
      foreach (var registration in registry.GetRegistrationsFor(messageType)) {
        yield return registration;
      }
    }
  }

  /// <summary>
  /// Count of registered auto-populate registries (for diagnostics/testing).
  /// </summary>
  public static int Count => AssemblyRegistry<IAutoPopulateRegistry>.Count;
}
