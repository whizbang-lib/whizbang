using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Startup filter that automatically injects the schema-availability gate at the front of the request
/// pipeline (so no <c>app.UseDatabaseAvailabilityGate()</c> call is needed). Turnkey: on by default in
/// MutationsOnly mode — reads serve, writes 503 while the schema initializes, pass-through once ready.
/// Skips itself if disabled via <see cref="WhizbangAvailabilityOptions"/> or if no
/// <see cref="ISchemaReadyGate"/> is registered (nothing to gate on).
/// </summary>
internal sealed class WhizbangAvailabilityStartupFilter : IStartupFilter {
  public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) {
    return app => {
      var options = app.ApplicationServices.GetService<IOptions<WhizbangAvailabilityOptions>>()?.Value
        ?? new WhizbangAvailabilityOptions();
      // Only gate when enabled AND a schema-ready gate exists (the middleware requires it).
      if (options.Enabled && app.ApplicationServices.GetService<ISchemaReadyGate>() is not null) {
        app.UseDatabaseAvailabilityGate(options.ExemptPaths, options.Mode);
      }
      next(app);
    };
  }
}
