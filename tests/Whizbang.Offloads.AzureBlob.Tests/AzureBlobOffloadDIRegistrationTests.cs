#pragma warning disable CA1707

using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Tests;

/// <summary>
/// Locks the Azure Blob provider's DI surface: AddWhizbangAzureBlobOffload
/// registers the keyed singleton store, binds options by provider name, and
/// the resolved instance exposes the correct ProviderName. Deep Azure SDK
/// behavior (upload/download against a real container) belongs in
/// integration tests against Azurite — out of scope here.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public class AzureBlobOffloadDIRegistrationTests {

  [Test]
  public async Task AddWhizbangAzureBlobOffload_RegistersStoreUnderProviderNameAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azure-blob-prod", opts => {
      opts.ConnectionString = "UseDevelopmentStorage=true";
      opts.ContainerName = "test-container";
    });

    var provider = services.BuildServiceProvider();
    var store = provider.GetKeyedService<IMessageBodyStore>("azure-blob-prod");

    await Assert.That(store).IsNotNull();
    await Assert.That(store).IsTypeOf<AzureBlobMessageBodyStore>();
    await Assert.That(store!.ProviderName).IsEqualTo("azure-blob-prod")
      .Because("The DI key matches the provider's declared ProviderName so the receive-side resolver finds the store by the claim's ProviderName.");
  }

  [Test]
  public async Task AddWhizbangAzureBlobOffload_TwoProviders_HaveDistinctOptionsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azure-blob-prod", opts => {
      opts.ConnectionString = "UseDevelopmentStorage=true";
      opts.ContainerName = "prod-bodies";
    });
    services.AddWhizbangAzureBlobOffload("azure-blob-archive", opts => {
      opts.ConnectionString = "UseDevelopmentStorage=true";
      opts.ContainerName = "archive-bodies";
      opts.DefaultAccessTier = AccessTier.Cool;
    });

    var provider = services.BuildServiceProvider();
    var prodStore = provider.GetKeyedService<IMessageBodyStore>("azure-blob-prod");
    var archiveStore = provider.GetKeyedService<IMessageBodyStore>("azure-blob-archive");

    await Assert.That(prodStore).IsNotNull();
    await Assert.That(archiveStore).IsNotNull();
    await Assert.That(prodStore!.ProviderName).IsEqualTo("azure-blob-prod");
    await Assert.That(archiveStore!.ProviderName).IsEqualTo("azure-blob-archive");

    // Each provider's options are bound under its own name — verify by reading
    // the options monitor directly. This is the wiring that lets one process
    // run multiple Azure Blob providers with different containers / tiers.
    var monitor = provider.GetRequiredService<IOptionsMonitor<AzureBlobOffloadOptions>>();
    await Assert.That(monitor.Get("azure-blob-prod").ContainerName).IsEqualTo("prod-bodies");
    await Assert.That(monitor.Get("azure-blob-archive").ContainerName).IsEqualTo("archive-bodies");
    await Assert.That(monitor.Get("azure-blob-archive").DefaultAccessTier).IsEqualTo(AccessTier.Cool);
  }

  [Test]
  public async Task AddWhizbangAzureBlobOffload_NullProviderName_ThrowsAsync() {
    var services = new ServiceCollection();

    Action act = () => services.AddWhizbangAzureBlobOffload(null!, opts => { });

    await Assert.That(act).Throws<ArgumentException>();
  }

  [Test]
  public async Task AddWhizbangAzureBlobOffload_NullConfigure_ThrowsAsync() {
    var services = new ServiceCollection();

    Action act = () => services.AddWhizbangAzureBlobOffload("name", null!);

    await Assert.That(act).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddWhizbangAzureBlobOffload_NullServices_ThrowsAsync() {
    Action act = () => ((IServiceCollection)null!).AddWhizbangAzureBlobOffload("provider", opts => { });

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("services");
  }

  [Test]
  public async Task AddWhizbangAzureBlobOffload_WhitespaceProviderName_ThrowsAsync() {
    // A whitespace provider name would silently produce an unresolvable DI key
    // (and could never match a claim's ProviderName) — rejected up front.
    var services = new ServiceCollection();

    Action act = () => services.AddWhizbangAzureBlobOffload("   ", opts => { });

    var ex = await Assert.That(act).ThrowsExactly<ArgumentException>();
    await Assert.That(ex!.ParamName).IsEqualTo("providerName");
  }

  [Test]
  public async Task AzureBlobMessageBodyStore_MissingConnectionString_ThrowsClearMessageAsync() {
    // Configure the option with an empty connection string and resolve the store.
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("misconfigured", opts => {
      opts.ConnectionString = null;
      opts.ContainerName = "test";
    });
    var sp = services.BuildServiceProvider();

    Action act = () => sp.GetKeyedService<IMessageBodyStore>("misconfigured");

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("ConnectionString")
      .Because("Misconfiguration MUST surface at DI resolution with a clear actionable message, not later as an obscure failure at first upload.");
  }
}
