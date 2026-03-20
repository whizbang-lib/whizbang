using Microsoft.AspNetCore.Builder;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Extension methods for registering Whizbang middleware in the ASP.NET Core pipeline.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
public static class WhizbangFlushMiddlewareExtensions {
  /// <summary>
  /// Adds the Whizbang flush middleware to the request pipeline. This middleware
  /// flushes the work coordinator after the request completes but before the
  /// DI scope is disposed, ensuring lifecycle stages run with valid ambient context.
  /// </summary>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushMiddlewareTests.cs:FlushMiddleware_CallsFlushAsyncAfterPipelineAsync</tests>
  public static IApplicationBuilder UseWhizbangFlush(this IApplicationBuilder app) {
    return app.UseMiddleware<WhizbangFlushMiddleware>();
  }
}
