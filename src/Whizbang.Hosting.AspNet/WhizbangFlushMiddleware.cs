using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// ASP.NET Core middleware that flushes the work coordinator after the request pipeline
/// completes but before the DI scope is disposed. This ensures lifecycle stages run while
/// ambient resources (HttpContext, security context) are still alive.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushMiddlewareTests.cs</tests>
public class WhizbangFlushMiddleware(RequestDelegate next) {
  /// <summary>
  /// Invokes the middleware, executing the rest of the pipeline and then flushing
  /// the work coordinator while the scope is still alive.
  /// </summary>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushMiddlewareTests.cs:FlushMiddleware_CallsFlushAsyncAfterPipelineAsync</tests>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushMiddlewareTests.cs:FlushMiddleware_NoFlusher_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushMiddlewareTests.cs:FlushMiddleware_RequestAborted_PassesCancellationAsync</tests>
  public async Task InvokeAsync(HttpContext context) {
    await next(context);
    // After pipeline completes, before scope disposal — everything still alive
    var flusher = context.RequestServices.GetService<IWorkFlusher>();
    if (flusher != null) {
      await flusher.FlushAsync(context.RequestAborted);
    }
  }
}
