using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Extension methods for registering Whizbang ASP.NET Core hosting services.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/ServiceCollectionExtensionsTests.cs</tests>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers the Whizbang flush middleware as a startup filter so it is automatically
  /// injected into the ASP.NET Core request pipeline. This ensures the work coordinator
  /// is flushed after each request completes but before the DI scope is disposed.
  /// </summary>
  /// <remarks>
  /// Call this in ASP.NET Core services that use Whizbang with scoped work coordination.
  /// The middleware is safe to register even if IWorkFlusher is not registered — it
  /// gracefully skips the flush in that case.
  /// </remarks>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangAspNet_RegistersStartupFilterAsync</tests>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangAspNet_CalledMultipleTimes_RegistersOnceAsync</tests>
  public static IServiceCollection AddWhizbangAspNet(this IServiceCollection services) {
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IStartupFilter, WhizbangFlushStartupFilter>());
    return services;
  }
}
