// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Archive.AzureBlob.Tests;

/// <summary>
/// How a host opts in: the registration replaces the driver's table store in either order, reads its settings from
/// configuration with documented defaults, refuses what it cannot read, and authenticates without a secret in code.
/// Nothing here opens a connection: every client is built offline.
/// </summary>
/// <docs>fundamentals/events/archive-store#registering-the-azure-blob-store</docs>
public class AzureBlobArchiveRegistrationTests {
  private const string EMULATOR = "UseDevelopmentStorage=true";

  // Never opened: these tests build clients and stores, they do not use them.
  private static readonly PostgresArchiveConnection _database =
    new(_ => throw new InvalidOperationException("no database in a unit test"), Schema: "svc");

  private static IConfiguration _config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

  // What a Postgres driver registers: the database handle, and its table store only when no store is present yet.
  private static void _driver(IServiceCollection services) {
    services.TryAddSingleton(_database);
    services.TryAddSingleton<IArchiveStore, TableStoreStandIn>();
  }

  [Test]
  public async Task Registration_ReplacesTheTableStore_WhetherItComesBeforeOrAfterTheDriverAsync() {
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

    await Assert.That(first.GetRequiredService<IArchiveStore>()).IsTypeOf<AzureBlobArchiveStore>()
      .Because("the driver adds its store only when none is present");
    await Assert.That(second.GetRequiredService<IArchiveStore>()).IsTypeOf<AzureBlobArchiveStore>()
      .Because("registering the package replaces a store the driver already added");
    await Assert.That(second.GetServices<IArchiveStore>()).Count().IsEqualTo(1)
      .Because("replaced, not added beside: nothing can resolve the table store by accident");
    await Assert.That(control.GetRequiredService<IArchiveStore>()).IsTypeOf<TableStoreStandIn>()
      .Because("the control: without the package the driver's store is what resolves");
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
    await Assert.That(provider.GetRequiredService<IArchiveStore>()).IsSameReferenceAs(provider.GetRequiredService<AzureBlobArchiveStore>())
      .Because("the step drains the queue of the one store the service archives through");
    await Assert.That(control.GetServices<IMaintenanceStep>()).IsEmpty()
      .Because("the table store keeps no objects outside the database, so a service without the package has no such step");
  }

  [Test]
  public async Task Step_And_Drain_RefuseMissingOrEmptyArgumentsAsync() {
    var store = new AzureBlobArchiveStore(_database, _container(), new AzureBlobArchiveOptions());

    await Assert.That(() => new ArchiveObjectDeletionStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new ArchiveObjectDeletionStep(store).RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(() => store.DeleteQueuedObjectsAsync(0)).Throws<ArgumentOutOfRangeException>()
      .Because("a drain of nothing is a caller's mistake, not a no-op");
  }

  [Test]
  public async Task Resolution_WithoutAPostgresDriver_SaysWhatToRegisterAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobArchive(o => o.ConnectionString = EMULATOR);
    await using var provider = services.BuildServiceProvider();

    var refused = await Assert.That(() => provider.GetRequiredService<IArchiveStore>()).Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("Postgres driver")
      .Because("the manifest lives in the service's database, so the store cannot exist without a driver");
  }

  [Test]
  public async Task ServiceUri_AuthenticatesWithTheRegisteredCredential_AndNoSecretInConfigurationAsync() {
    var services = new ServiceCollection();
    _driver(services);
    services.AddSingleton<TokenCredential>(new NoCallCredential());
    services.AddWhizbangAzureBlobArchive(_config(new() {
      ["Whizbang:Archive:AzureBlob:ServiceUri"] = "https://account.blob.core.windows.net",
    }));
    await using var provider = services.BuildServiceProvider();

    var store = (AzureBlobArchiveStore)provider.GetRequiredService<IArchiveStore>();

    await Assert.That(store.LocatorFor(Guid.Empty).Address).StartsWith("whizbang-archive/");
  }

  [Test]
  public async Task CreateContainerClient_ServiceUri_UsesTheEndpointAndTheContainerAsync() {
    var options = new AzureBlobArchiveOptions { ServiceUri = new Uri("https://account.blob.core.windows.net"), ContainerName = "archive-a" };

    var client = AzureBlobArchiveServiceCollectionExtensions.CreateContainerClient(options, new NoCallCredential());

    await Assert.That(client.Uri.ToString()).IsEqualTo("https://account.blob.core.windows.net/archive-a");
  }

  [Test]
  public async Task CreateContainerClient_ServiceUriWithNoCredential_IsRefused_NamingWhatIsMissingAsync() {
    var options = new AzureBlobArchiveOptions { ServiceUri = new Uri("https://account.blob.core.windows.net") };

    var refused = await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.CreateContainerClient(options, credential: null))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("TokenCredential")
      .Because("the store never falls back to anonymous access or to a key it was not given");
  }

  [Test]
  public async Task CreateContainerClient_ConnectionString_UsesItAndTheContainerAsync() {
    var options = new AzureBlobArchiveOptions { ConnectionString = EMULATOR, ContainerName = "archive-a" };

    var client = AzureBlobArchiveServiceCollectionExtensions.CreateContainerClient(options, credential: null);

    await Assert.That(client.Name).IsEqualTo("archive-a");
    await Assert.That(client.AccountName).IsEqualTo("devstoreaccount1");
  }

  [Test]
  public async Task CreateContainerClient_NeitherOrBothEndpoints_IsRefusedAsync() {
    var neither = new AzureBlobArchiveOptions();
    var both = new AzureBlobArchiveOptions { ConnectionString = EMULATOR, ServiceUri = new Uri("https://account.blob.core.windows.net") };

    await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.CreateContainerClient(neither, new NoCallCredential()))
      .Throws<InvalidOperationException>().Because("an archive with no account would fail on its first write, not at startup");
    await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.CreateContainerClient(both, new NoCallCredential()))
      .Throws<InvalidOperationException>().Because("two accounts is ambiguous; the store never picks one silently");
  }

  [Test]
  public async Task Configuration_BindsEverySetting_AndKeepsTheDefaultsForWhatIsAbsentAsync() {
    var bound = new AzureBlobArchiveOptions();
    var defaults = new AzureBlobArchiveOptions();

    AzureBlobArchiveServiceCollectionExtensions.Bind(_config(new() {
      ["ServiceUri"] = "https://account.blob.core.windows.net",
      ["ConnectionString"] = EMULATOR,
      ["ContainerName"] = "archive-a",
      ["CompressionLevel"] = "9",
      ["MaxEventsPerChunk"] = "500",
    }), bound);
    AzureBlobArchiveServiceCollectionExtensions.Bind(_config(new() { ["ContainerName"] = " " }), defaults);

    await Assert.That(bound.ServiceUri).IsEqualTo(new Uri("https://account.blob.core.windows.net"));
    await Assert.That(bound.ConnectionString).IsEqualTo(EMULATOR);
    await Assert.That(bound.ContainerName).IsEqualTo("archive-a");
    await Assert.That(bound.CompressionLevel).IsEqualTo(9);
    await Assert.That(bound.MaxEventsPerChunk).IsEqualTo(500);
    await Assert.That(defaults.ServiceUri).IsNull();
    await Assert.That(defaults.ConnectionString).IsNull();
    await Assert.That(defaults.ContainerName).IsEqualTo("whizbang-archive").Because("a blank value keeps the default");
    await Assert.That(defaults.CompressionLevel).IsEqualTo(4);
    await Assert.That(defaults.MaxEventsPerChunk).IsEqualTo(10_000);
  }

  [Test]
  [Arguments("CompressionLevel", "fast")]
  [Arguments("MaxEventsPerChunk", "ten thousand")]
  [Arguments("ServiceUri", "not an absolute uri")]
  public async Task Configuration_AValueThatCannotBeRead_IsRefused_NamingTheKeyAsync(string key, string value) {
    var refused = await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.Bind(
        _config(new() { [key] = value }), new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains($"Whizbang:Archive:AzureBlob:{key}")
      .Because("a misread setting would otherwise fall back to a default nobody chose");
  }

  [Test]
  [Arguments(-1, 10_000)]
  [Arguments(12, 10_000)]
  [Arguments(4, 0)]
  public async Task Store_RefusesAQualityOrAChunkSizeOutOfRangeAsync(int quality, int maxEventsPerChunk) {
    var options = new AzureBlobArchiveOptions { CompressionLevel = quality, MaxEventsPerChunk = maxEventsPerChunk };

    await Assert.That(() => new AzureBlobArchiveStore(_database, _container(), options)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Arguments(0, 1)]
  [Arguments(11, 1)]
  public async Task Store_AcceptsTheEdgesOfTheRangeAsync(int quality, int maxEventsPerChunk) {
    var options = new AzureBlobArchiveOptions { CompressionLevel = quality, MaxEventsPerChunk = maxEventsPerChunk };

    var store = new AzureBlobArchiveStore(_database, _container(), options);

    await Assert.That(store).IsNotNull().Because("the control: Brotli's whole range and a one-event chunk are valid");
  }

  [Test]
  public async Task Store_RefusesMissingArgumentsAsync() {
    await Assert.That(() => new AzureBlobArchiveStore(null!, _container(), new AzureBlobArchiveOptions())).Throws<ArgumentNullException>();
    await Assert.That(() => new AzureBlobArchiveStore(_database, null!, new AzureBlobArchiveOptions())).Throws<ArgumentNullException>();
    await Assert.That(() => new AzureBlobArchiveStore(_database, _container(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangAzureBlobArchive((Action<AzureBlobArchiveOptions>)null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAzureBlobArchive(_ => { })).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangAzureBlobArchive((IConfiguration)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments("svc")]
  [Arguments(null)]
  public async Task LocatorFor_NamesTheStoreTheContainerAndTheStream_WithBrotliAndFormat1Async(string? schema) {
    var stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");

    var locator = new AzureBlobArchiveStore(_database with { Schema = schema }, _container(), new AzureBlobArchiveOptions())
      .LocatorFor(stream);

    await Assert.That(locator.ToString())
      .IsEqualTo("azure-blob://whizbang-archive/0199a1b2-0000-7000-8000-000000000001?codec=brotli&format=1")
      .Because("the locator names the container, never the database schema the manifest lives in");
  }

  private static Azure.Storage.Blobs.BlobContainerClient _container() =>
    new(EMULATOR, AzureBlobArchiveOptions.DEFAULT_CONTAINER_NAME);

  /// <summary>A credential that is never asked for a token: building a client must not authenticate.</summary>
  private sealed class NoCallCredential : TokenCredential {
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("a unit test never authenticates");

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("a unit test never authenticates");
  }

  /// <summary>Stands in for the driver's table store; never called.</summary>
  private sealed class TableStoreStandIn : IArchiveStore {
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
