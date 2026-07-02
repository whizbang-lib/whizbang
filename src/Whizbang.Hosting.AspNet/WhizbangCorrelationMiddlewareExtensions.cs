using Microsoft.AspNetCore.Builder;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Registration helpers for <see cref="WhizbangCorrelationMiddleware"/>.
/// </summary>
public static class WhizbangCorrelationMiddlewareExtensions {
  /// <summary>
  /// Explicitly inserts the correlation-capture middleware at this point in the pipeline. Not usually needed
  /// — <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/> registers it turnkey via an
  /// <c>IStartupFilter</c>. Use only when you need to control its exact placement. Configure the headers via
  /// <c>services.Configure&lt;WhizbangCorrelationOptions&gt;(...)</c>.
  /// </summary>
  public static IApplicationBuilder UseWhizbangCorrelation(this IApplicationBuilder app) {
    return app.UseMiddleware<WhizbangCorrelationMiddleware>();
  }
}
