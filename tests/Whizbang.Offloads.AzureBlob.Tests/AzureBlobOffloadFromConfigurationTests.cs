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
      ["Whizbang:Offloads:AzureBlob:consumer-offload:ConnectionString"] = "UseDevelopmentStorage=true",
      ["Whizbang:Offloads:AzureBlob:consumer-offload:ContainerName"] = "whizbang-offload-bodies-prod",
      ["Whizbang:Offloads:AzureBlob:consumer-offload:DefaultAccessTier"] = "Cool",
      ["Whizbang:Offloads:AzureBlob:consumer-offload:MaxDownloadBytes"] = "104857600",
      ["Whizbang:BodyOffload:ProviderName"] = "consumer-offload",
      ["Whizbang:BodyOffload:SizeThresholdBytes"] = "65536",
      ["Whizbang:BodyOffload:ActiveCleanup"] = "false",
    });

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    var provider = services.BuildServiceProvider();

    // Store registered under the provider name discovered in config.
    var store = provider.GetKeyedService<IMessageBodyStore>("consumer-offload");
    await Assert.That(store).IsNotNull();
    await Assert.That(store!.ProviderName).IsEqualTo("consumer-offload");

    // Provider options bound — including DefaultAccessTier parsed from the "Cool" string, the bit
    // ConfigurationBinder cannot convert (the reason for explicit binding).
    var blobOptions = provider.GetRequiredService<IOptionsMonitor<AzureBlobOffloadOptions>>().Get("consumer-offload");
    await Assert.That(blobOptions.ConnectionString).IsEqualTo("UseDevelopmentStorage=true");
    await Assert.That(blobOptions.ContainerName).IsEqualTo("whizbang-offload-bodies-prod");
    await Assert.That(blobOptions.DefaultAccessTier).IsEqualTo(AccessTier.Cool);
    await Assert.That(blobOptions.MaxDownloadBytes).IsEqualTo(104857600L);

    // Send-side selector bound + the claim-check hook chain registered (offload is ON).
    var bodyOptions = provider.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>().CurrentValue;
    await Assert.That(bodyOptions.ProviderName).IsEqualTo("consumer-offload");
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
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("consumer-offload")).IsNull();
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

  [Test]
  public async Task FromConfiguration_NullServices_ThrowsAsync() {
    var configuration = _config(new());

    Action act = () => ((IServiceCollection)null!).AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("services");
  }

  [Test]
  public async Task FromConfiguration_NullConfiguration_ThrowsAsync() {
    var services = new ServiceCollection();

    Action act = () => services.AddWhizbangAzureBlobOffloadsFromConfiguration(null!);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("configuration");
  }

  [Test]
  public async Task FromConfiguration_WhitespaceProviderKey_IsSkippedWithoutDisablingOthersAsync() {
    // Config section keys are environment-supplied; a whitespace-only key cannot
    // serve as a DI key and must be skipped — WITHOUT turning offload off for the
    // well-formed providers alongside it.
    var configuration = _config(new() {
      ["Whizbang:Offloads:AzureBlob:   :ConnectionString"] = "UseDevelopmentStorage=true",
      ["Whizbang:Offloads:AzureBlob:good-provider:ConnectionString"] = "UseDevelopmentStorage=true",
      ["Whizbang:BodyOffload:ProviderName"] = "good-provider",
    });

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("good-provider")).IsNotNull()
      .Because("A malformed sibling key must not take down the valid provider registrations.");
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("   ")).IsNull()
      .Because("The whitespace key is skipped — no store may be registered under it.");
    await Assert.That(provider.GetService<PostSerializeHookChain>()).IsNotNull()
      .Because("One valid provider is enough to enable the send-side claim-check hook.");
  }

  [Test]
  public async Task FromConfiguration_NoBodyOffloadSection_HookRegisteredWithSelectorDefaultsAsync() {
    // A provider without the Whizbang:BodyOffload section still wires the hook chain,
    // and the selector keeps its compiled defaults (no ProviderName, 64 KiB threshold,
    // passive cleanup) — the receive side can resolve claims even when this service
    // never offloads on send.
    var configuration = _config(new() {
      ["Whizbang:Offloads:AzureBlob:receive-only:ConnectionString"] = "UseDevelopmentStorage=true",
    });

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<PostSerializeHookChain>()).IsNotNull();
    var bodyOptions = provider.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>().CurrentValue;
    await Assert.That(bodyOptions.ProviderName).IsNull()
      .Because("No Whizbang:BodyOffload:ProviderName means no active send-side target — the selector must stay unbound.");
    await Assert.That(bodyOptions.SizeThresholdBytes).IsEqualTo(64L * 1024L);
    await Assert.That(bodyOptions.ActiveCleanup).IsFalse();
  }

  [Test]
  public async Task FromConfiguration_UnparseableOrBlankValues_LeaveOptionDefaultsAsync() {
    // Every binding branch is guarded: blank strings and unparseable numerics/bools
    // are ignored rather than bound — the options keep their compiled defaults so a
    // half-broken environment fails at the ConnectionString-required check, not with
    // a garbage configuration silently applied.
    var configuration = _config(new() {
      ["Whizbang:Offloads:AzureBlob:sparse:ConnectionString"] = "   ",
      ["Whizbang:Offloads:AzureBlob:sparse:ContainerName"] = "",
      ["Whizbang:Offloads:AzureBlob:sparse:DefaultAccessTier"] = "",
      ["Whizbang:Offloads:AzureBlob:sparse:MaxDownloadBytes"] = "not-a-number",
      ["Whizbang:BodyOffload:ProviderName"] = "",
      ["Whizbang:BodyOffload:SizeThresholdBytes"] = "NaN",
      ["Whizbang:BodyOffload:ActiveCleanup"] = "not-a-bool",
    });

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    var provider = services.BuildServiceProvider();

    var blobOptions = provider.GetRequiredService<IOptionsMonitor<AzureBlobOffloadOptions>>().Get("sparse");
    await Assert.That(blobOptions.ConnectionString).IsNull();
    await Assert.That(blobOptions.ContainerName).IsEqualTo("whizbang-offload-bodies");
    await Assert.That(blobOptions.DefaultAccessTier).IsNull();
    await Assert.That(blobOptions.MaxDownloadBytes).IsNull();

    var bodyOptions = provider.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>().CurrentValue;
    await Assert.That(bodyOptions.ProviderName).IsNull();
    await Assert.That(bodyOptions.SizeThresholdBytes).IsEqualTo(64L * 1024L);
    await Assert.That(bodyOptions.ActiveCleanup).IsFalse();
  }
}
