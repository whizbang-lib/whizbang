using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Workers;

/// <summary>
/// Registers the schema-readiness gate that startup-sensitive workers wait on.
/// </summary>
/// <remarks>
/// <para>
/// Every registration extension that composes a worker requiring <see cref="ISchemaReadyGate"/>
/// calls this, so each extension stands alone rather than assuming a fuller composition registered
/// the gate first. A pipeline composed without the core worker registration previously left the
/// gate absent, and because the parameter was optional the worker started immediately instead of
/// waiting: the skipped wait produced no error, and the work ran against a schema nobody had
/// confirmed was there.
/// </para>
/// <para>
/// The gate itself is a real implementation, not an inert one. A permissive stub would answer
/// "ready" without checking, which does not decline to intervene but asserts the very invariant the
/// type exists to establish, and would be worse than the absence it replaced.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/SchemaReadyGateWiringTests.cs</tests>
public static class SchemaReadyGateRegistration {

  /// <summary>Ensures an <see cref="ISchemaReadyGate"/> is registered.</summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection, for chaining.</returns>
  public static IServiceCollection AddWhizbangSchemaReadyGate(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton<ISchemaReadyGate, SchemaReadyGate>();
    return services;
  }
}
