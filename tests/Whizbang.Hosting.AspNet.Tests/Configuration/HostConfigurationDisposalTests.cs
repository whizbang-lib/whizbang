using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.AspNet.Tests.Configuration;

/// <summary>
/// Bijan Camp (a consumer/a consumer application) reported 2026-06-12: every Whizbang consumer that
/// registers an EF Core <c>[WhizbangDbContext]</c> silently loses all dynamic
/// configuration behaviour (<c>IOptionsMonitor</c>, <c>IFeatureManager</c>,
/// <c>appsettings.json reloadOnChange</c>, Azure App Configuration refresh,
/// any <c>ChangeToken.OnChange(() => config.GetReloadToken(), …)</c>) at
/// startup, with no error or log line, because <c>EFCoreServiceRegistrationGenerator</c>
/// emits this pattern in two places (around lines 1518-1526 and 1655-1663):
///
/// <code>
/// using var tempProvider = services.BuildServiceProvider(new ServiceProviderOptions {…});
/// var config = tempProvider.GetRequiredService&lt;IConfiguration&gt;();
/// </code>
///
/// <para>In a <see cref="WebApplicationBuilder"/> host, <c>IConfiguration</c> is
/// registered such that the container that <em>instantiates</em> the singleton
/// becomes its owner. The temp container resolves <c>IConfiguration</c>, takes
/// ownership of the live <see cref="ConfigurationManager"/>, and the <c>using</c>
/// disposes it. <see cref="ConfigurationManager.Dispose"/> destroys every
/// provider→CM change-token subscription. Reads still serve (so nothing visibly
/// breaks at boot), but no further configuration change ever propagates.</para>
///
/// <para>This file holds two paired tests: the broken pattern (current generator
/// output) and the fixed pattern (defer config access to provider-build time via
/// the <c>(sp, opts) =&gt; …</c> factory overload). Together they document the
/// contract the generator fix must produce.</para>
/// </summary>
/// <docs>fundamentals/hosting/configuration-disposal</docs>
public class HostConfigurationDisposalTests {

  [Test]
  public async Task BrokenPattern_TempServiceProviderDisposed_HostConfigurationManagerDies_RedAsync() {
    // Reproduces Bijan's bug: the temp provider's Dispose() disposes the host's
    // ConfigurationManager. Reads still work, but reload tokens are dead.
    var builder = WebApplication.CreateBuilder([]);
    var probe = new ProbeSource();
    builder.Configuration.Sources.Add(probe);
    // Touch the configuration so the probe provider is materialised on the
    // ConfigurationManager's active provider chain.
    _ = builder.Configuration["__force_provider_realisation__"];

    // === the buggy pattern the generator emits today (sites 1520 & 1657) ===
    using (var tempProvider = builder.Services.BuildServiceProvider(new ServiceProviderOptions {
      ValidateOnBuild = false,
      ValidateScopes = false
    })) {
      _ = tempProvider.GetRequiredService<IConfiguration>();
    }
    // ↑ ConfigurationManager.Dispose() ran here as a side-effect.

    // Arm a reload subscription on the (now-dead) ConfigurationManager.
    var fired = 0;
    using var subscription = ChangeToken.OnChange(
      () => ((IConfiguration)builder.Configuration).GetReloadToken(),
      () => Interlocked.Increment(ref fired));

    // Force a provider-level reload — in a healthy CM this propagates to all
    // subscribers via the change-token chain the provider registered with the
    // ConfigurationManager at build time.
    probe.Provider!.TriggerReload();

    await Assert.That(Volatile.Read(ref fired)).IsEqualTo(0)
      .Because("BUG REPRO: the temp provider's Dispose() disposed the ConfigurationManager, which destroyed every provider→CM change-token registration. No subsequent provider reload can ever reach a subscriber on the CM's reload token. This is exactly the bug Bijan reported on 2026-06-12 — every Whizbang EF Core consumer loses IOptionsMonitor / IFeatureManager / appsettings reload / Azure App Configuration refresh silently at startup.");
  }

  [Test]
  public async Task FixedPattern_DeferConfigAccess_ToProviderBuildTime_KeepsConfigurationManagerAliveAsync() {
    // Locks the contract the generator fix must produce: resolve IConfiguration
    // via a (sp, …) factory at registration time so the resolution happens only
    // when the REAL provider is built. No temp container, no disposal hazard.
    var builder = WebApplication.CreateBuilder([]);
    var probe = new ProbeSource();
    builder.Configuration.Sources.Add(probe);
    _ = builder.Configuration["__force_provider_realisation__"];

    // === fixed pattern: register a factory; don't build a temp container ===
    builder.Services.AddSingleton<MarkerService>(sp => {
      // Imagine this is the NpgsqlDataSource factory that today builds via
      // captured config; in the fix it resolves IConfiguration via sp at the
      // moment the REAL provider runs the factory.
      var config = sp.GetRequiredService<IConfiguration>();
      _ = config["DummyConnectionString"];
      return new MarkerService();
    });

    // Build the host normally. No temp container.
    using var host = builder.Build();
    _ = host.Services.GetRequiredService<MarkerService>();

    var fired = 0;
    using var subscription = ChangeToken.OnChange(
      () => ((IConfiguration)builder.Configuration).GetReloadToken(),
      () => Interlocked.Increment(ref fired));

    probe.Provider!.TriggerReload();

    await Assert.That(Volatile.Read(ref fired)).IsEqualTo(1)
      .Because("Fixed pattern — config access deferred to the real provider's build → the ConfigurationManager stays alive → the provider→CM change-token registration survives → provider reloads still propagate to every subscriber. This is the shape the generator fix must produce in EFCoreServiceRegistrationGenerator.cs around lines 1518-1526 and 1655-1663.");
  }

  // ============================================================================
  // helpers — Bijan's ProbeProvider pattern from the bug report
  // ============================================================================

  private sealed class ProbeSource : IConfigurationSource {
    public ProbeProvider? Provider;
    public IConfigurationProvider Build(IConfigurationBuilder b) => Provider = new ProbeProvider();
  }

  private sealed class ProbeProvider : ConfigurationProvider {
    public void TriggerReload() => OnReload();
  }

  private sealed class MarkerService { }
}
