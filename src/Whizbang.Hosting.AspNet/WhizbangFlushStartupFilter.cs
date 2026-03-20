using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Startup filter that automatically injects the Whizbang flush middleware
/// at the beginning of the request pipeline, ensuring the flush runs after
/// all other middleware but before scope disposal.
/// </summary>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushStartupFilterTests.cs</tests>
internal sealed class WhizbangFlushStartupFilter : IStartupFilter {
  public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) {
    return app => {
      app.UseWhizbangFlush();
      next(app);
    };
  }
}
