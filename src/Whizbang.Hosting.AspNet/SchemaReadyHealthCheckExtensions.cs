using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Extension methods for registering <see cref="SchemaReadyHealthCheck"/>.
/// </summary>
public static class SchemaReadyHealthCheckExtensions {
  /// <summary>
  /// Adds <see cref="SchemaReadyHealthCheck"/> to the readiness health checks. Defaults to the name
  /// <c>schema</c> and the <c>ready</c> tag so it participates in a readiness endpoint (not liveness) —
  /// keeping the host out of traffic rotation until non-blocking schema init completes.
  /// </summary>
  /// <docs>resilience/database-availability-middleware</docs>
  public static IHealthChecksBuilder AddWhizbangSchemaReadyCheck(
      this IHealthChecksBuilder builder, string name = "schema", params string[] tags)
    => builder.AddCheck<SchemaReadyHealthCheck>(name, tags: tags.Length > 0 ? tags : ["ready"]);
}
