using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Self-registers the ASP.NET hosting integration so <c>AddWhizbang()</c> folds in
/// <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/> automatically. Runs when this assembly
/// is loaded — i.e. only when the ASP.NET hosting library is actually referenced/used — so Core never
/// needs to reference it (AOT-safe, no reflection). Consumers opt out via
/// <c>WhizbangCoreOptions.AutoRegisterAspNetHosting = false</c> and call <c>AddWhizbangAspNet()</c> themselves.
/// </summary>
internal static class WhizbangAspNetModuleInitializer {
  // CA2255: intentional use of ModuleInitializer in library code — the AOT-safe way to self-register
  // the hosting integration only when this assembly is loaded, without Core referencing it.
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  internal static void Register() {
    ServiceRegistrationCallbacks.HostingIntegration = static services => services.AddWhizbangAspNet();
  }
}
