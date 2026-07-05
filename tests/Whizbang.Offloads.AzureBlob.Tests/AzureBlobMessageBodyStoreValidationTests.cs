#pragma warning disable CA1707

using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Tests;

/// <summary>
/// Locks the <see cref="AzureBlobMessageBodyStore"/> constructor validation
/// and argument-guard branches that the DI registration tests don't reach:
/// provider-name/options-monitor guards, the fall-back-to-unnamed-default
/// options branch, the ContainerName-required failure, and the null-claim
/// guards on Download/Delete. Azure SDK behavior stays in the Azurite
/// integration tests — nothing here touches the network.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public class AzureBlobMessageBodyStoreValidationTests {

  /// <summary>Builds a real IOptionsMonitor so ctor guards run against genuine named-options plumbing.</summary>
  private static IOptionsMonitor<AzureBlobOffloadOptions> _buildMonitor(
      Action<AzureBlobOffloadOptions>? configureDefault = null,
      string? name = null,
      Action<AzureBlobOffloadOptions>? configureNamed = null) {
    var services = new ServiceCollection();
    services.AddOptions();
    if (configureDefault is not null) {
      services.Configure(configureDefault);
    }
    if (name is not null && configureNamed is not null) {
      services.Configure(name, configureNamed);
    }
    return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<AzureBlobOffloadOptions>>();
  }

  [Test]
  public async Task Constructor_NullProviderName_ThrowsArgumentNullExceptionAsync() {
    var monitor = _buildMonitor(opts => opts.ConnectionString = "UseDevelopmentStorage=true");

    Action act = () => _ = new AzureBlobMessageBodyStore(null!, monitor);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("providerName")
      .Because("The guard names the offending parameter so DI misconfiguration is diagnosable at a glance.");
  }

  [Test]
  public async Task Constructor_WhitespaceProviderName_ThrowsArgumentExceptionAsync() {
    var monitor = _buildMonitor(opts => opts.ConnectionString = "UseDevelopmentStorage=true");

    Action act = () => _ = new AzureBlobMessageBodyStore("   ", monitor);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentException>();
    await Assert.That(ex!.ParamName).IsEqualTo("providerName")
      .Because("A whitespace provider name would silently produce an unresolvable DI key — it must be rejected up front.");
  }

  [Test]
  public async Task Constructor_NullOptionsMonitor_ThrowsArgumentNullExceptionAsync() {
    Action act = () => _ = new AzureBlobMessageBodyStore("provider", null!);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task Constructor_NamedBindingEmpty_FallsBackToUnnamedDefaultOptionsAsync() {
    // Only the UNNAMED default carries a connection string; the named
    // binding for "custom-provider" was never configured, so its
    // ConnectionString is null and the ctor must fall back to CurrentValue.
    var monitor = _buildMonitor(configureDefault: opts => {
      opts.ConnectionString = "UseDevelopmentStorage=true";
      opts.ContainerName = "fallback-container";
    });

    var store = new AzureBlobMessageBodyStore("custom-provider", monitor);

    await Assert.That(store.ProviderName).IsEqualTo("custom-provider")
      .Because("Construction succeeding proves the fallback branch engaged — without it the blank named binding would raise the ConnectionString-required error. ProviderName stays the requested name, not the binding that supplied the options.");
  }

  [Test]
  public async Task Constructor_EmptyContainerName_ThrowsWithContainerNameInMessageAsync() {
    // Named binding HAS a connection string (so no fallback), but the
    // container name was explicitly blanked out — the second validation
    // branch, distinct from the ConnectionString-required error already
    // covered by AzureBlobOffloadDIRegistrationTests.
    var monitor = _buildMonitor(
      name: "blank-container",
      configureNamed: opts => {
        opts.ConnectionString = "UseDevelopmentStorage=true";
        opts.ContainerName = "";
      });

    Action act = () => _ = new AzureBlobMessageBodyStore("blank-container", monitor);

    var ex = await Assert.That(act).ThrowsExactly<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("ContainerName")
      .Because("Misconfiguration MUST name the missing option — a bare failure at first upload would send the operator hunting.");
    await Assert.That(ex.Message).Contains("blank-container")
      .Because("With multiple providers registered, the message must say WHICH provider is misconfigured.");
  }

  [Test]
  public async Task DownloadAsync_NullClaim_ThrowsArgumentNullExceptionAsync() {
    var store = _buildStoreWithFakeContainer();

    var ex = await Assert.That(async () => await store.DownloadAsync(null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("claim")
      .Because("The guard fires before any cap evaluation or blob access — no network call for a null claim.");
  }

  [Test]
  public async Task DeleteAsync_NullClaim_ThrowsArgumentNullExceptionAsync() {
    var store = _buildStoreWithFakeContainer();

    var ex = await Assert.That(async () => await store.DeleteAsync(null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("claim")
      .Because("The guard fires before resolving a blob client — no network call for a null claim.");
  }

  private static AzureBlobMessageBodyStore _buildStoreWithFakeContainer() {
    // Internal test ctor: hand the store a mock-friendly container client so
    // the argument guards can be exercised with zero Azure connectivity.
    var options = new AzureBlobOffloadOptions {
      ConnectionString = "UseDevelopmentStorage=true",
      ContainerName = "guard-tests",
    };
    return new AzureBlobMessageBodyStore("guard-tests", options, new FakeBlobContainerClient());
  }

  /// <summary>
  /// Uses the Azure SDK's protected mocking ctor. Any actual member call
  /// would throw — the guard tests must fail before touching the client.
  /// </summary>
  private sealed class FakeBlobContainerClient : BlobContainerClient {
  }
}
