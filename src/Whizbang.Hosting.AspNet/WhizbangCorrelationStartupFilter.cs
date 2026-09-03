using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Startup filter that automatically injects <see cref="WhizbangCorrelationMiddleware"/> at the very start of
/// the request pipeline, so an inbound correlation id is captured before any endpoint dispatches a message.
/// Registered by <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/> — correlation capture is turnkey.
/// </summary>
internal sealed class WhizbangCorrelationStartupFilter : IStartupFilter {
  public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) {
    return app => {
      app.UseMiddleware<WhizbangCorrelationMiddleware>();
      next(app);
    };
  }
}
