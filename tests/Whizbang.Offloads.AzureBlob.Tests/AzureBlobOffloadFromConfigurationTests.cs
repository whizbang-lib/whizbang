#pragma warning disable CA1707

using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Tests;

/// <summary>
/// Locks the convention-based, config-driven offload wiring:
/// <see cref="AzureBlobOffloadServiceCollectionExtensions.AddWhizbangAzureBlobOffloadsFromConfiguration"/>
/// scans <c>Whizbang:Offloads:AzureBlob:&lt;name&gt;</c> for providers, binds each (including the
/// AccessTier-from-string conversion ConfigurationBinder can't do), and wires the send-side hook +
/// selector from <c>Whizbang:BodyOffload</c>. This is the single call a host makes; the env-var keys
/// each deployment slot provides bind through it with zero per-service code.
/// </summary>
/// <docs>offloads</docs>
public class AzureBlobOffloadFromConfigurationTests {

  private static IConfiguration _config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

  [Test]
  public async Task FromConfiguration_RegistersProvider_BindsOptions_AndSelectorAsync() {
    var configuration = _config(new() {
      ["Whizbang:Offloads:AzureBlob:a consumer-offload:ConnectionString"] = "UseDevelopmentStorage=true",
      ["Whizbang:Offloads:AzureBlob:a consumer-offload:ContainerName"] = "whizbang-offload-bodies-production",
      ["Whizbang:Offloads:AzureBlob:a consumer-offload:DefaultAccessTier"] = "Cool",
      ["Whizbang:Offloads:AzureBlob:a consumer-offload:MaxDownloadBytes"] = "104857600",
      ["Whizbang:BodyOffload:ProviderName"] = "a consumer-offload",
      ["Whizbang:BodyOffload:SizeThresholdBytes"] = "65536",
      ["Whizbang:BodyOffload:ActiveCleanup"] = "false",
    });

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    var provider = services.BuildServiceProvider();

    // Store registered under the provider name discovered in config.
    var store = provider.GetKeyedService<IMessageBodyStore>("a consumer-offload");
    await Assert.That(store).IsNotNull();
    await Assert.That(store!.ProviderName).IsEqualTo("a consumer-offload");

    // Provider options bound — including DefaultAccessTier parsed from the "Cool" string, the bit
    // ConfigurationBinder cannot convert (the reason for explicit binding).
    var blobOptions = provider.GetRequiredService<IOptionsMonitor<AzureBlobOffloadOptions>>().Get("a consumer-offload");
    await Assert.That(blobOptions.ConnectionString).IsEqualTo("UseDevelopmentStorage=true");
    await Assert.That(blobOptions.ContainerName).IsEqualTo("whizbang-offload-bodies-production");
    await Assert.That(blobOptions.DefaultAccessTier).IsEqualTo(AccessTier.Cool);
    await Assert.That(blobOptions.MaxDownloadBytes).IsEqualTo(104857600L);

    // Send-side selector bound + the claim-check hook chain registered (offload is ON).
    var bodyOptions = provider.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>().CurrentValue;
    await Assert.That(bodyOptions.ProviderName).IsEqualTo("a consumer-offload");
    await Assert.That(bodyOptions.SizeThresholdBytes).IsEqualTo(65536L);
    await Assert.That(bodyOptions.ActiveCleanup).IsFalse();
    await Assert.That(provider.GetService<PostSerializeHookChain>()).IsNotNull();
  }

  [Test]
  public async Task FromConfiguration_NoProviders_IsNoOpAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(_config(new()));
    var provider = services.BuildServiceProvider();

    // No provider configured → no store, no hook chain — offload stays off and publish is inline.
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("a consumer-offload")).IsNull();
    await Assert.That(provider.GetService<PostSerializeHookChain>()).IsNull();
  }

  [Test]
  public async Task FromConfiguration_MultipleProviders_AllRegisteredAsync() {
    var configuration = _config(new() {
      ["Whizbang:Offloads:AzureBlob:azure-blob-prod:ConnectionString"] = "UseDevelopmentStorage=true",
      ["Whizbang:Offloads:AzureBlob:azure-blob-archive:ConnectionString"] = "UseDevelopmentStorage=true",
      ["Whizbang:Offloads:AzureBlob:azure-blob-archive:DefaultAccessTier"] = "Archive",
      ["Whizbang:BodyOffload:ProviderName"] = "azure-blob-prod",
    });

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("azure-blob-prod")).IsNotNull();
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("azure-blob-archive")).IsNotNull();

    var monitor = provider.GetRequiredService<IOptionsMonitor<AzureBlobOffloadOptions>>();
    await Assert.That(monitor.Get("azure-blob-archive").DefaultAccessTier).IsEqualTo(AccessTier.Archive);
  }
}
