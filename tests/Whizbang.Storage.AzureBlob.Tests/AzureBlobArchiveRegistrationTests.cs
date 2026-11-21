// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Azure.Core;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Archive.AzureBlob;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Storage.AzureBlob.Tests;

/// <summary>
/// How a host archives to Azure Blob Storage (#2502, #2503): the earlier package's calls still compile and work, the
/// archive object store is selected whether the call comes before or after the driver, its settings are read with
/// documented defaults, a value it cannot read is refused naming the key, and the account is reached without a secret in
/// code. Nothing here opens a connection: every client is built offline.
/// </summary>
/// <docs>fundamentals/events/archive-store#registering-the-azure-blob-store</docs>
#pragma warning disable CS0618 // The earlier package's calls are exercised on purpose: they must keep working this release.
public class AzureBlobArchiveRegistrationTests {
  private const string EMULATOR = "UseDevelopmentStorage=true";
  private const string LEGACY = "Whizbang:Archive:AzureBlob";

  // Never opened: these tests build clients and stores, they do not use them.
  private static readonly PostgresArchiveConnection _database =
    new(_ => throw new InvalidOperationException("no database in a unit test"), Schema: "svc");

  private static IConfiguration _config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

  // What a Postgres driver registers: the database handle, and, only when no store is present yet, its coordinator over a
  // selected object store, or else its table store (PostgresArchiveStores).
  private static void _driver(IServiceCollection services) {
    services.TryAddSingleton(_database);
    services.TryAddSingleton<IArchiveStore>(sp => ArchiveObjectStoreSelection.Resolve(sp) is { } objects
      ? new PostgresObjectArchiveStore(sp.GetRequiredService<PostgresArchiveConnection>(), objects)
      : new TableStoreStandIn());
  }

  [Test]
  public async Task Registration_TheDriverArchivesThroughBlob_WhetherItComesBeforeOrAfterTheDriverAsync() {
    var before = new ServiceCollection();
    before.AddWhizbangAzureBlobArchive(o => o.ConnectionString = EMULATOR);
    _driver(before);
    var after = new ServiceCollection();
    _driver(after);
    after.AddWhizbangAzureBlobArchive(o => o.ConnectionString = EMULATOR);
    var driverOnly = new ServiceCollection();
    _driver(driverOnly);

    await using var first = before.BuildServiceProvider();
    await using var second = after.BuildServiceProvider();
    await using var control = driverOnly.BuildServiceProvider();

    await Assert.That(first.GetRequiredService<IArchiveStore>()).IsTypeOf<PostgresObjectArchiveStore>();
    await Assert.That(second.GetRequiredService<IArchiveStore>()).IsTypeOf<PostgresObjectArchiveStore>()
      .Because("the package selects an object store, and the driver's archive store is the coordinator over it, in either order");
    await Assert.That(((IArchiveObjectCoordinator)second.GetRequiredService<IArchiveStore>()).Objects).IsTypeOf<AzureBlobArchiveObjectStore>();
    await Assert.That(control.GetRequiredService<IArchiveStore>()).IsTypeOf<TableStoreStandIn>()
      .Because("the control: without the package the driver's table store is what resolves");
  }

  [Test]
  public async Task Registration_AddsTheObjectDeletionStep_OnlyWithThePackageAsync() {
    var withPackage = new ServiceCollection();
    _driver(withPackage);
    withPackage.AddWhizbangAzureBlobArchive(o => o.ConnectionString = EMULATOR);
    var driverOnly = new ServiceCollection();
    _driver(driverOnly);
    await using var provider = withPackage.BuildServiceProvider();
    await using var control = driverOnly.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().ToList();

    await Assert.That(steps).Count().IsEqualTo(1);
    await Assert.That(steps[0]).IsTypeOf<ArchiveObjectDeletionStep>();
    await Assert.That(steps[0].Name).IsEqualTo("archive-object-deletion");
    await Assert.That(control.GetServices<IMaintenanceStep>()).IsEmpty()
      .Because("the table store keeps no objects outside the database, so a service without the package has no such step");
  }

  [Test]
  public async Task Registration_WithAnArchiveConfiguredButNoAccount_KeepsTheTableStore_AndSaysWhyAsync() {
    var services = new ServiceCollection();
    _driver(services);
    services.AddWhizbangAzureBlobArchive(o => o.ContainerName = "archive-a");
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value;

    await Assert.That(provider.GetRequiredService<IArchiveStore>()).IsTypeOf<TableStoreStandIn>()
      .Because("a feature activates only when an account is named for it");
    await Assert.That(options.Notices).Contains(n => n.Contains("no account is named", StringComparison.Ordinal));
    await Assert.That(provider.GetRequiredService<ArchiveObjectStoreSelection>().Reason).Contains("names no account");
  }

  [Test]
  public async Task ServiceUri_AuthenticatesWithTheRegisteredCredential_AndNoSecretInConfigurationAsync() {
    var services = new ServiceCollection();
    _driver(services);
    services.AddSingleton<TokenCredential>(new NoCallCredential());
    services.AddWhizbangAzureBlobArchive(_config(new() {
      [$"{LEGACY}:ServiceUri"] = "https://account.blob.core.windows.net",
    }));
    await using var provider = services.BuildServiceProvider();

    var coordinator = (IArchiveObjectCoordinator)provider.GetRequiredService<IArchiveStore>();

    await Assert.That(coordinator.ConfiguredLocation.Endpoint).IsEqualTo("https://account.blob.core.windows.net");
    await Assert.That(coordinator.ConfiguredLocation.Container).IsEqualTo("whizbang-archive");
  }

  [Test]
  public async Task CreateContainerClient_ServiceUri_UsesTheEndpointAndTheContainerAsync() {
    var client = AzureBlobStorageConfiguration.CreateContainerClient(
      new Uri("https://account.blob.core.windows.net"), null, "archive-a", new NoCallCredential(), "archive");

    await Assert.That(client.Uri.ToString()).IsEqualTo("https://account.blob.core.windows.net/archive-a");
  }

  [Test]
  public async Task CreateContainerClient_ServiceUriWithNoCredential_IsRefused_NamingWhatIsMissingAsync() {
    var refused = await Assert.That(() => AzureBlobStorageConfiguration.CreateContainerClient(
        new Uri("https://account.blob.core.windows.net"), null, "archive-a", credential: null, "archive"))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("TokenCredential")
      .Because("the store never falls back to anonymous access or to a key it was not given");
  }

  [Test]
  public async Task CreateContainerClient_ConnectionString_UsesItAndTheContainerAsync() {
    var client = AzureBlobStorageConfiguration.CreateContainerClient(null, EMULATOR, "archive-a", credential: null, "archive");

    await Assert.That(client.Name).IsEqualTo("archive-a");
    await Assert.That(client.AccountName).IsEqualTo("devstoreaccount1");
  }

  [Test]
  public async Task CreateContainerClient_NeitherOrBothEndpoints_IsRefusedAsync() {
    await Assert.That(() => AzureBlobStorageConfiguration.CreateContainerClient(null, null, "c", new NoCallCredential(), "archive"))
      .Throws<InvalidOperationException>().Because("an archive with no account would fail on its first write, not at startup");
    await Assert.That(() => AzureBlobStorageConfiguration.CreateContainerClient(
        new Uri("https://account.blob.core.windows.net"), EMULATOR, "c", new NoCallCredential(), "archive"))
      .Throws<InvalidOperationException>().Because("two accounts is ambiguous; the store never picks one silently");
  }

  [Test]
  public async Task Configuration_BindsEverySetting_AndKeepsTheDefaultsForWhatIsAbsentAsync() {
    var bound = new AzureBlobArchiveOptions();
    var defaults = new AzureBlobArchiveOptions();

    AzureBlobStorageConfiguration.BindArchive(_config(new() {
      ["ServiceUri"] = "https://account.blob.core.windows.net",
      ["ConnectionString"] = EMULATOR,
      ["ContainerName"] = "archive-a",
      ["CompressionLevel"] = "9",
      ["MaxEventsPerChunk"] = "500",
      ["AccessTier"] = "Cool",
    }), LEGACY, bound);
    AzureBlobStorageConfiguration.BindArchive(_config(new() { ["ContainerName"] = " " }), LEGACY, defaults);

    await Assert.That(bound.ServiceUri).IsEqualTo(new Uri("https://account.blob.core.windows.net"));
    await Assert.That(bound.ConnectionString?.Reveal()).IsEqualTo(EMULATOR);
    await Assert.That(bound.ContainerName).IsEqualTo("archive-a");
    await Assert.That(bound.CompressionLevel).IsEqualTo(9);
    await Assert.That(bound.MaxEventsPerChunk).IsEqualTo(500);
    await Assert.That(defaults.ServiceUri).IsNull();
    await Assert.That(defaults.ConnectionString).IsNull();
    await Assert.That(defaults.ContainerName).IsEqualTo("whizbang-archive").Because("a blank value keeps the default");
    await Assert.That(defaults.CompressionLevel).IsEqualTo(4);
    await Assert.That(defaults.MaxEventsPerChunk).IsEqualTo(10_000);
    await Assert.That(bound.AccessTier).IsEqualTo(AccessTier.Cool);
    await Assert.That(defaults.AccessTier).IsNull().Because("no tier configured leaves the storage account's default tier");
  }

  [Test]
  [Arguments("Hot", "Hot")]
  [Arguments("cool", "Cool")]
  [Arguments("COLD", "Cold")]
  public async Task Configuration_AccessTier_BindsHotCoolOrCold_InAnyCaseAsync(string value, string expected) {
    var bound = new AzureBlobArchiveOptions();

    AzureBlobStorageConfiguration.BindArchive(_config(new() { ["AccessTier"] = value }), LEGACY, bound);

    await Assert.That(bound.AccessTier.ToString()).IsEqualTo(expected)
      .Because("the tier is stored in the SDK's own spelling, whatever case the configuration used");
  }

  [Test]
  [Arguments("Archive", "rehydrat")]
  [Arguments("Premium", "Hot, Cool or Cold")]
  [Arguments("P10", "Hot, Cool or Cold")]
  [Arguments("Glacier", "Hot, Cool or Cold")]
  public async Task Configuration_AnAccessTierTheStoreCannotWriteAt_IsRefused_NamingTheKeyAndWhyAsync(string value, string why) {
    var refused = await Assert.That(() => AzureBlobStorageConfiguration.BindArchive(
        _config(new() { ["AccessTier"] = value }), AzureBlobStorageOptions.ARCHIVE_SECTION, new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("Whizbang:Storage:AzureBlob:Archive:AccessTier")
      .Because("the operator is told which setting to change");
    await Assert.That(refused.Message).Contains(why);
  }

  [Test]
  [Arguments("Archive")]
  [Arguments("Premium")]
  public async Task Store_RefusesATierItCannotReadBackFromAsync(string tier) {
    var options = new AzureBlobArchiveOptions { AccessTier = new AccessTier(tier) };

    await Assert.That(() => new AzureBlobArchiveObjectStore(_container(), options)).Throws<ArgumentOutOfRangeException>()
      .Because("a store built by hand is held to the same tiers as one built from configuration");
  }

  [Test]
  [Arguments("Hot")]
  [Arguments("Cool")]
  [Arguments("Cold")]
  [Arguments(null)]
  public async Task Store_AcceptsHotCoolColdOrTheAccountDefaultAsync(string? tier) {
    var options = new AzureBlobArchiveOptions { AccessTier = tier is null ? (AccessTier?)null : new AccessTier(tier) };

    var store = new AzureBlobArchiveObjectStore(_container(), options);

    await Assert.That(store.Location.Provider).IsEqualTo("azure-blob").Because("the control: every online tier, and no tier, is valid");
  }

  [Test]
  public async Task Startup_AnArchiveTierSetInCode_FailsTheHostAtStart_ACoolTierDoesNotAsync() {
    var refusedServices = new ServiceCollection();
    _driver(refusedServices);
    refusedServices.AddWhizbangAzureBlobArchive(o => {
      o.ConnectionString = EMULATOR;
      o.AccessTier = AccessTier.Archive;
    });
    var acceptedServices = new ServiceCollection();
    _driver(acceptedServices);
    acceptedServices.AddWhizbangAzureBlobArchive(o => {
      o.ConnectionString = EMULATOR;
      o.AccessTier = AccessTier.Cool;
    });
    await using var refusedProvider = refusedServices.BuildServiceProvider();
    await using var acceptedProvider = acceptedServices.BuildServiceProvider();

    var refused = await Assert.That(() => refusedProvider.GetRequiredService<IStartupValidator>().Validate())
      .Throws<OptionsValidationException>().Because("the host fails as it starts, not on the first archive write hours later");
    await Assert.That(refused!.Message).Contains("rehydrat");
    await Assert.That(() => acceptedProvider.GetRequiredService<IStartupValidator>().Validate()).ThrowsNothing()
      .Because("the control: the validator runs and passes an online tier");
  }

  [Test]
  [Arguments("CompressionLevel", "fast")]
  [Arguments("MaxEventsPerChunk", "ten thousand")]
  [Arguments("ServiceUri", "not an absolute uri")]
  [Arguments("VerifyLocation", "maybe")]
  [Arguments("DeleteAfter", "soon")]
  [Arguments("DeleteAfter", "00:00:00")]
  [Arguments("DeleteAfter", "-01:00:00")]
  public async Task Configuration_AValueThatCannotBeRead_IsRefused_NamingTheKeyAsync(string key, string value) {
    var refused = await Assert.That(() => AzureBlobStorageConfiguration.BindArchive(
        _config(new() { [key] = value }), LEGACY, new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains($"{LEGACY}:{key}")
      .Because("a misread setting would otherwise fall back to a default nobody chose");
  }

  [Test]
  public async Task Configuration_DeleteAfter_IsOffByDefault_AndBindsATimeSpanAsync() {
    var bound = new AzureBlobArchiveOptions();
    var blank = new AzureBlobArchiveOptions();

    AzureBlobStorageConfiguration.BindArchive(_config(new() { ["DeleteAfter"] = "35.00:00:00" }), LEGACY, bound);
    AzureBlobStorageConfiguration.BindArchive(_config(new() { ["DeleteAfter"] = " " }), LEGACY, blank);

    await Assert.That(new AzureBlobArchiveOptions().DeleteAfter).IsNull()
      .Because("off by default: a queued deletion runs at once, as before the option existed");
    await Assert.That(bound.DeleteAfter).IsEqualTo(TimeSpan.FromDays(35));
    await Assert.That(blank.DeleteAfter).IsNull().Because("a blank value keeps the default");
  }

  [Test]
  [Arguments(0L)]
  [Arguments(-1L)]
  public async Task Store_RefusesADeleteAfterThatIsNotAboveZeroAsync(long ticks) {
    var options = new AzureBlobArchiveOptions { DeleteAfter = TimeSpan.FromTicks(ticks) };

    var refused = await Assert.That(() => new AzureBlobArchiveObjectStore(_container(), options)).Throws<ArgumentOutOfRangeException>();

    await Assert.That(refused!.Message).Contains("DeleteAfter");
    await Assert.That(new AzureBlobArchiveObjectStore(_container(), new AzureBlobArchiveOptions { DeleteAfter = TimeSpan.FromTicks(1) }).Settings.DeleteAfter)
      .IsEqualTo(TimeSpan.FromTicks(1)).Because("the control: any span above zero is a delay, handed to the coordinator");
  }

  [Test]
  public async Task Startup_ADeleteAfterOfZeroSetInCode_FailsTheHostAtStartAsync() {
    var services = new ServiceCollection();
    _driver(services);
    services.AddWhizbangAzureBlobArchive(o => {
      o.ConnectionString = EMULATOR;
      o.DeleteAfter = TimeSpan.Zero;
    });
    await using var provider = services.BuildServiceProvider();

    var refused = await Assert.That(() => provider.GetRequiredService<IStartupValidator>().Validate()).Throws<OptionsValidationException>();

    await Assert.That(refused!.Message).Contains("Whizbang:Storage:AzureBlob:Archive").And.Contains("DeleteAfter");
  }

  [Test]
  [Arguments(-1, 10_000)]
  [Arguments(12, 10_000)]
  [Arguments(4, 0)]
  public async Task Store_RefusesAQualityOrAChunkSizeOutOfRangeAsync(int quality, int maxEventsPerChunk) {
    var options = new AzureBlobArchiveOptions { CompressionLevel = quality, MaxEventsPerChunk = maxEventsPerChunk };

    await Assert.That(() => new AzureBlobArchiveObjectStore(_container(), options)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Arguments(0, 1)]
  [Arguments(11, 1)]
  public async Task Store_AcceptsTheEdgesOfTheRange_AndHandsThemToTheCoordinatorAsync(int quality, int maxEventsPerChunk) {
    var options = new AzureBlobArchiveOptions { CompressionLevel = quality, MaxEventsPerChunk = maxEventsPerChunk, VerifyLocation = false };

    var settings = new AzureBlobArchiveObjectStore(_container(), options).Settings;

    var expected = new ArchiveObjectStoreSettings { CompressionLevel = quality, MaxEventsPerChunk = maxEventsPerChunk, VerifyLocation = false };
    await Assert.That(settings).IsEqualTo(expected).Because("the control: Brotli's whole range and a one-event chunk are valid, and the coordinator writes with them");
  }

  [Test]
  public async Task Store_And_Registration_RefuseMissingArgumentsAsync() {
    await Assert.That(() => new AzureBlobArchiveObjectStore(null!, new AzureBlobArchiveOptions())).Throws<ArgumentNullException>();
    await Assert.That(() => new AzureBlobArchiveObjectStore(_container(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangAzureBlobArchive((Action<AzureBlobArchiveOptions>)null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAzureBlobArchive(_ => { })).Throws<ArgumentNullException>();
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAzureBlobArchive(_config([]))).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangAzureBlobArchive((IConfiguration)null!)).Throws<ArgumentNullException>();
    await Assert.That(AzureBlobArchiveServiceCollectionExtensions.CONFIGURATION_SECTION).IsEqualTo(LEGACY);
  }

  [Test]
  public async Task Locator_NamesTheStoreAndTheStream_NeverTheContainer_WithBrotliAndFormat1Async() {
    var stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");
    var coordinator = new PostgresObjectArchiveStore(_database, new AzureBlobArchiveObjectStore(_container(), new AzureBlobArchiveOptions()));

    var locator = coordinator.LocatorFor(stream);

    await Assert.That(locator.ToString())
      .IsEqualTo("azure-blob://0199a1b2-0000-7000-8000-000000000001?codec=brotli&format=1")
      .Because("unchanged by the split (#2503): the tombstone says which store archived the stream; where it lives is the " +
        "manifest's location (#2137), so a repoint never leaves it stale");
  }

  private static Azure.Storage.Blobs.BlobContainerClient _container() =>
    new(EMULATOR, AzureBlobArchiveOptions.DEFAULT_CONTAINER_NAME);

  /// <summary>A credential that is never asked for a token: building a client must not authenticate.</summary>
  internal sealed class NoCallCredential : TokenCredential {
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("a unit test never authenticates");

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("a unit test never authenticates");
  }

  /// <summary>Stands in for the driver's table store; never called.</summary>
  internal sealed class TableStoreStandIn : IArchiveStore {
    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public async IAsyncEnumerable<ArchivedEvent> ReadAsync(
        Guid streamId, long fromVersion = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }
}
#pragma warning restore CS0618
