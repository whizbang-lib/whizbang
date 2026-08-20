using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Registration and configuration-binding tests for the tag-bound TransportNamespace routing
/// surface (topology arc phase 8): <c>AddWhizbang</c> merges RouteNamespace bindings across
/// calls (the coalesce-merge idiom), registers <see cref="TransportNamespaceResolver"/> as a
/// singleton, and the <c>Whizbang:Tags:RouteNamespace:&lt;tag&gt;</c> configuration section
/// binds routing on top of code bindings (configuration wins — the operator seam).
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TransportNamespaceRoutingRegistrationTests {
  [Test]
  public async Task AddWhizbang_SecondCall_MergesRouteNamespaceBindingsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang(o => o.Tags.RouteNamespace("bulk-import", "bulk"));

    services.AddWhizbang(o => {
      o.Tags.RouteNamespace("bulk-import", "batch");
      o.Tags.RouteNamespace("sys-control", "control");
    });

    var tagOptions = (TagOptions)services.Single(d => d.ServiceType == typeof(TagOptions)).ImplementationInstance!;
    await Assert.That(tagOptions.RouteNamespaceBindings.Count).IsEqualTo(2);
    await Assert.That(tagOptions.RouteNamespaceBindings["bulk-import"]).IsEqualTo("batch")
      .Because("last-wins per tag applies across AddWhizbang calls too");
    await Assert.That(tagOptions.RouteNamespaceBindings["sys-control"]).IsEqualTo("control");
  }

  [Test]
  public async Task AddWhizbang_RegistersTransportNamespaceResolverSingletonAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang(o => o.Tags.RouteNamespace("bulk-import", "bulk"));

    using var provider = services.BuildServiceProvider();
    var resolver = provider.GetService<TransportNamespaceResolver>();
    var again = provider.GetService<TransportNamespaceResolver>();

    await Assert.That(resolver).IsNotNull();
    await Assert.That(again).IsSameReferenceAs(resolver!)
      .Because("the resolver caches per-type-name resolution and must be a singleton");
    await Assert.That(resolver!.HasBindings).IsTrue();
  }

  [Test]
  public async Task Resolver_ConfigurationBinding_AddsRouteBindingAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tags:RouteNamespace:bulk-import"] = "bulk"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbang(null);

    using var provider = services.BuildServiceProvider();
    var resolver = provider.GetRequiredService<TransportNamespaceResolver>();

    await Assert.That(resolver.HasBindings).IsTrue();
    var tagOptions = provider.GetRequiredService<TagOptions>();
    await Assert.That(tagOptions.RouteNamespaceBindings["bulk-import"]).IsEqualTo("bulk");
  }

  [Test]
  public async Task Resolver_ConfigurationOverridesCodeBindingAsync() {
    // Configuration post-configures OVER the code callback (the transport-options idiom): an
    // operator can re-class traffic without a redeploy.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tags:RouteNamespace:bulk-import"] = "batch"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbang(o => o.Tags.RouteNamespace("bulk-import", "bulk"));

    using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<TransportNamespaceResolver>();

    var tagOptions = provider.GetRequiredService<TagOptions>();
    await Assert.That(tagOptions.RouteNamespaceBindings["bulk-import"]).IsEqualTo("batch");
  }

  [Test]
  public async Task StartupValidator_SeesConfigurationBoundRouteBindingsAsync() {
    // A sys-* routing binding smuggled in via configuration must fail startup exactly like a
    // code binding would — the validator applies the configuration binder before validating.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tags:RouteNamespace:sys-mine"] = "bulk"
      })
      .Build();
    var validator = new TagPolicyStartupValidator(new TagOptions(), () => [], configuration);

    await Assert.That(async () => await validator.StartAsync(CancellationToken.None))
      .Throws<TagPolicyConfigurationException>();
  }
}
