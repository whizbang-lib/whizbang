using CollectiveEvents.Sample.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Perspectives;

namespace CollectiveEvents.Sample.Wiring;

/// <summary>
/// Shows the DI wiring a consuming service performs to make the sample
/// perspective discoverable + executable. The two pieces are:
/// </summary>
/// <list type="number">
///   <item><description>The perspective class itself (transient or scoped, depending on host policy).</description></item>
///   <item><description>The <see cref="ICollectiveScopeResolver"/> for every scope kind a handler accepts. The sample uses tenant scope, so it registers <see cref="TenantCollectiveScopeResolver"/>.</description></item>
/// </list>
/// <remarks>
/// The Slice 5-generated <c>CollectiveApplyRegistry</c> is consumed
/// automatically by the projection runner (Slice 7+ wiring) — the
/// consumer doesn't register entries by hand. That's the AOT story: the
/// registry is a static <c>readonly</c> field in generated code,
/// reachable via direct static access, never via type scanning.
/// </remarks>
public static class ServiceRegistration {
  public static IServiceCollection AddCollectiveEventsSample(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.AddSingleton<JobCollectivePerspective>();
    services.AddSingleton<ICollectiveScopeResolver, TenantCollectiveScopeResolver>();

    return services;
  }
}
