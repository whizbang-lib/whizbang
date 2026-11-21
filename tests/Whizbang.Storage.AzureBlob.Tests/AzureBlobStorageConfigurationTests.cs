// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Configuration;
using Whizbang.Core.Offloads;

namespace Whizbang.Storage.AzureBlob.Tests;

/// <summary>
/// <c>Whizbang:Storage:AzureBlob</c> (#2502): one account, a section per feature, each feature active when an account is
/// named for it; the earlier packages' keys still bind with a warning naming the new key, and the new key wins.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#configuration</docs>
public class AzureBlobStorageConfigurationTests {
  private const string EMULATOR = "UseDevelopmentStorage=true";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

  private static ServiceProvider _build(IConfiguration configuration, Action<IServiceCollection>? arrange = null) {
    var services = new ServiceCollection();
    services.AddSingleton(configuration);
    services.AddLogging();
    arrange?.Invoke(services);
    services.AddWhizbangAzureBlobStorage();
    return services.BuildServiceProvider();
  }

  private static AzureBlobStorageOptions _options(ServiceProvider provider) => provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value;

  [Test]
  public async Task TheNewKeys_NameOneAccount_AndEachFeatureTakesItAsync() {
    await using var provider = _build(_config(
      ("Whizbang:Storage:AzureBlob:ConnectionString", EMULATOR),
      ("Whizbang:Storage:AzureBlob:Offloads:ContainerName", "bodies"),
      ("Whizbang:Storage:AzureBlob:Offloads:DefaultAccessTier", "Cool"),
      ("Whizbang:Storage:AzureBlob:Offloads:MaxDownloadBytes", "1024"),
      ("Whizbang:Storage:AzureBlob:Archive:ContainerName", "archive"),
      ("Whizbang:Storage:AzureBlob:Archive:CompressionLevel", "6")));

    var options = _options(provider);
    var selection = provider.GetRequiredService<ArchiveObjectStoreSelection>();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>(AzureBlobOffloadOptions.DEFAULT_PROVIDER_NAME);

    await Assert.That(options.Archive!.ConnectionString?.Reveal()).IsEqualTo(EMULATOR).Because("the archive takes the shared account");
    await Assert.That(options.Offloads!.ConnectionString?.Reveal()).IsEqualTo(EMULATOR).Because("so does body offload");
    await Assert.That(options.Offloads.ContainerName).IsEqualTo("bodies");
    await Assert.That(options.Offloads.MaxDownloadBytes).IsEqualTo(1024L);
    await Assert.That(options.Offloads.DefaultAccessTier.ToString()).IsEqualTo("Cool");
    await Assert.That(selection.Store!.Location.Container).IsEqualTo("archive");
    await Assert.That(selection.Store.Settings.CompressionLevel).IsEqualTo(6);
    await Assert.That(selection.Reason).IsEqualTo("azure-blob, account from Whizbang:Storage:AzureBlob:ConnectionString");
    await Assert.That(store.ProviderName).IsEqualTo("azure-blob");
    await Assert.That(provider.GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value.ProviderName).IsEqualTo("azure-blob")
      .Because("the store the section configures is the one bodies are offloaded to");
    await Assert.That(provider.GetServices<StorageFeatureActivation>().Single(a => a.Feature == StorageFeatureActivation.OFFLOAD).Reason)
      .IsEqualTo("azure-blob, account from Whizbang:Storage:AzureBlob:ConnectionString");
    await Assert.That(options.Notices).IsEmpty();
  }

  [Test]
  public async Task AFeature_ItsOwnAccount_WinsOverTheShared_AndAServiceUriUsesTheRegisteredCredentialAsync() {
    await using var provider = _build(_config(
      ("Whizbang:Storage:AzureBlob:ServiceUri", "https://shared.blob.core.windows.net"),
      ("Whizbang:Storage:AzureBlob:Offloads:ServiceUri", "https://bodies.blob.core.windows.net"),
      ("Whizbang:Storage:AzureBlob:Offloads:ProviderName", "bodies"),
      ("Whizbang:Storage:AzureBlob:Archive:ConnectionString", EMULATOR)),
      services => services.AddSingleton<Azure.Core.TokenCredential>(new AzureBlobArchiveRegistrationTests.NoCallCredential()));

    var options = _options(provider);
    var archive = provider.GetRequiredService<ArchiveObjectStoreSelection>();
    var bodies = provider.GetRequiredKeyedService<IMessageBodyStore>("bodies");

    await Assert.That(options.Offloads!.ServiceUri).IsEqualTo(new Uri("https://bodies.blob.core.windows.net"));
    await Assert.That(archive.Store!.Location.Endpoint).IsEqualTo("http://127.0.0.1:10000/devstoreaccount1");
    await Assert.That(archive.Reason).IsEqualTo("azure-blob, account from Whizbang:Storage:AzureBlob:Archive:ConnectionString");
    await Assert.That(bodies.ProviderName).IsEqualTo("bodies");
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>(AzureBlobOffloadOptions.DEFAULT_PROVIDER_NAME)).IsNull()
      .Because("a store resolves only under the name it was given");
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>(42)).IsNull().Because("a key that is not a name resolves nothing");
  }

  [Test]
  [Arguments("https://named.blob.core.windows.net", "https://named.blob.core.windows.net/", null)]
  [Arguments("UseDevelopmentStorage=true", null, "UseDevelopmentStorage=true")]
  public async Task AConnectionName_NamesTheAccount_FromTheHostsConnectionStrings_AndIsTheArchivesLocationNameAsync(
      string entry, string? serviceUri, string? connectionString) {
    await using var provider = _build(_config(
      ("Whizbang:Storage:AzureBlob:ConnectionName", "blobs"),
      ("ConnectionStrings:blobs", entry),
      ("Whizbang:Storage:AzureBlob:Archive:ContainerName", "archive")));

    var options = _options(provider);

    await Assert.That(options.ServiceUri?.ToString()).IsEqualTo(serviceUri);
    await Assert.That(options.ConnectionString?.Reveal()).IsEqualTo(connectionString);
    await Assert.That(options.Archive!.ConnectionName).IsEqualTo("blobs").Because("the archive records the connection name in every location");
    await Assert.That(options.ArchiveReason).Contains("ConnectionStrings:blobs");
  }

  [Test]
  public async Task AConnectionName_WithNoConnectionStringsEntry_NamesNoAccount_AndTheFeatureStaysOff_WithAWarningAsync() {
    await using var provider = _build(_config(
      ("Whizbang:Storage:AzureBlob:ConnectionName", "missing"),
      ("Whizbang:Storage:AzureBlob:Offloads:ContainerName", "bodies"),
      ("Whizbang:Storage:AzureBlob:Archive:ContainerName", "archive")));

    var options = _options(provider);

    await Assert.That(options.Archive).IsNull();
    await Assert.That(options.Offloads).IsNull();
    await Assert.That(provider.GetRequiredService<ArchiveObjectStoreSelection>().Store).IsNull();
    await Assert.That(options.Notices).Count().IsEqualTo(2).Because("each feature configured with no account says so at start");
    await Assert.That(provider.GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value.ProviderName).IsNull();
  }

  [Test]
  public async Task AConnectionName_AlongsideAnAccount_OnlyNamesTheLocation_AndNeverReplacesTheArchivesOwnAsync() {
    await using var shared = _build(_config(
      ("Whizbang:Storage:AzureBlob:ConnectionString", EMULATOR),
      ("Whizbang:Storage:AzureBlob:ConnectionName", "shared"),
      ("ConnectionStrings:shared", "https://ignored.blob.core.windows.net"),
      ("Whizbang:Archive:AzureBlob:ConnectionName", "archive-own")));
    await using var inherited = _build(_config(
      ("Whizbang:Storage:AzureBlob:ConnectionString", EMULATOR),
      ("Whizbang:Storage:AzureBlob:ConnectionName", "shared"),
      ("Whizbang:Archive:AzureBlob:ContainerName", "legacy-archive")));

    await Assert.That(_options(shared).ServiceUri).IsNull().Because("an account already named is not replaced by a connection entry");
    await Assert.That(_options(shared).Archive!.ConnectionName).IsEqualTo("archive-own")
      .Because("an environment's archive keeps the name its records were written with");
    await Assert.That(_options(inherited).Archive!.ConnectionName).IsEqualTo("shared");
  }

  [Test]
  public async Task TheEarlierKeys_StillBind_WithAWarningNamingTheNewKey_AndTheNewKeyWinsAsync() {
    await using var provider = _build(_config(
      ("Whizbang:Archive:AzureBlob:ConnectionString", EMULATOR),
      ("Whizbang:Archive:AzureBlob:ContainerName", "legacy-archive"),
      ("Whizbang:Archive:AzureBlob:MaxEventsPerChunk", "50"),
      ("Whizbang:Storage:AzureBlob:Archive:ContainerName", "new-archive"),
      ("Whizbang:Offloads:AzureBlob:legacy-bodies:ConnectionString", EMULATOR),
      ("Whizbang:Offloads:AzureBlob:legacy-bodies:ContainerName", "legacy-bodies"),
      ("Whizbang:Storage:AzureBlob:ConnectionString", EMULATOR),
      ("Whizbang:Storage:AzureBlob:Offloads:ContainerName", "new-bodies"),
      ("Whizbang:BodyOffload:ProviderName", "legacy-bodies")));

    var options = _options(provider);

    await Assert.That(options.Archive!.ContainerName).IsEqualTo("new-archive").Because("the new key wins");
    await Assert.That(options.Archive.MaxEventsPerChunk).IsEqualTo(50).Because("an earlier key the new section does not set still binds");
    await Assert.That(options.ArchiveReason).IsEqualTo("azure-blob, account from Whizbang:Archive:AzureBlob:ConnectionString");
    await Assert.That(provider.GetRequiredKeyedService<IMessageBodyStore>("legacy-bodies").ProviderName).IsEqualTo("legacy-bodies")
      .Because("claims an earlier release sent name the earlier store, and still resolve");
    await Assert.That(provider.GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value.ProviderName).IsEqualTo("azure-blob")
      .Because("the new section chooses the body store over Whizbang:BodyOffload:ProviderName");
    await Assert.That(options.Notices).Count().IsEqualTo(3);
    await Assert.That(options.Notices[0]).Contains("Whizbang:Archive:AzureBlob is read for compatibility; its new key is")
      .And.Contains("Whizbang:Storage:AzureBlob:Archive").And.DoesNotContain("release")
      .Because("the warning names the new key and promises nothing about when the earlier one stops being read");
    await Assert.That(options.Notices[1]).Contains("Whizbang:Offloads:AzureBlob:legacy-bodies").And.Contains("ProviderName set to 'legacy-bodies'");
    await Assert.That(options.Notices[2]).Contains("Whizbang:BodyOffload:ProviderName is set").And.Contains("wins");
  }

  [Test]
  public async Task AnEarlierNamedStore_WithoutAConnectionString_IsRefusedWhenResolved_NamingTheKeyAsync() {
    await using var provider = _build(_config(("Whizbang:Offloads:AzureBlob:sparse:ContainerName", "c")));

    var refused = await Assert.That(() => provider.GetKeyedService<IMessageBodyStore>("sparse")).Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("Whizbang:Offloads:AzureBlob:sparse:ConnectionString");
  }

  [Test]
  [Arguments("Whizbang:Storage:AzureBlob:ServiceUri", "not a uri", "Whizbang:Storage:AzureBlob:ServiceUri")]
  [Arguments("Whizbang:Storage:AzureBlob:ConnectionName", "AccountKey=secret", "Whizbang:Storage:AzureBlob:ConnectionName")]
  [Arguments("Whizbang:Storage:AzureBlob:Offloads:ServiceUri", "relative/path", "Whizbang:Storage:AzureBlob:Offloads:ServiceUri")]
  [Arguments("Whizbang:Storage:AzureBlob:Archive:CompressionLevel", "fast", "Whizbang:Storage:AzureBlob:Archive:CompressionLevel")]
  public async Task AValueThatCannotBeRead_FailsTheStart_NamingTheKey_NeverEchoingASecretAsync(string key, string value, string named) {
    await using var provider = _build(_config((key, value)));

    var refused = await Assert.That(() => provider.GetRequiredService<IStartupValidator>().Validate()).Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains(named);
    await Assert.That(refused.Message.Contains("AccountKey=secret", StringComparison.Ordinal)).IsFalse();
  }

  [Test]
  public async Task AGivenConfiguration_IsReadOverTheHostsOwnAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(("Whizbang:Storage:AzureBlob:ConnectionString", EMULATOR), ("Whizbang:Storage:AzureBlob:Archive:ContainerName", "host")));
    services.AddWhizbangAzureBlobStorage(_config(("Whizbang:Storage:AzureBlob:Archive:ContainerName", "given")));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(_options(provider).Archive!.ContainerName).IsEqualTo("given");
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAzureBlobStorage(_config())).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangAzureBlobStorage((IConfiguration)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheStartupNotices_AreLoggedOnce_EachAsAWarningAsync() {
    var logger = new FakeLogger<AzureBlobStorageStartupNotices>();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(_config(("Whizbang:Archive:AzureBlob:ConnectionString", EMULATOR)));
    services.AddSingleton<ILogger<AzureBlobStorageStartupNotices>>(logger);
    services.AddWhizbangAzureBlobStorage(_config(("Whizbang:Archive:AzureBlob:ContainerName", "given")));
    await using var provider = services.BuildServiceProvider();
    var notices = provider.GetServices<IHostedService>().OfType<AzureBlobStorageStartupNotices>().Single();

    await notices.StartAsync(CancellationToken.None);
    await notices.StopAsync(CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot()).Count().IsEqualTo(1)
      .Because("the same warning from the host's configuration and a given one is logged once");
    await Assert.That(logger.LatestRecord.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.LatestRecord.Message).Contains("Whizbang:Archive:AzureBlob is read for compatibility");
  }

  [Test]
  public async Task WithNoHostConfiguration_NothingIsActiveAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobStorage();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<ArchiveObjectStoreSelection>().Store).IsNull();
    await Assert.That(provider.GetRequiredService<ArchiveObjectStoreSelection>().Reason).IsEqualTo("Whizbang:Storage:AzureBlob:Archive is not set");
    await Assert.That(provider.GetServices<StorageFeatureActivation>().Single().Provider).IsEmpty();
  }
}
