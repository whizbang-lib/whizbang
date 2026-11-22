// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Storage.AzureBlob.Tests;

/// <summary>
/// The location a store is configured for (#2137): the provider, the storage account's endpoint derived from the
/// configured client (never a key, a signature or a connection string), the logical connection name and the container.
/// It is what every archive record is checked against. Nothing here opens a connection.
/// </summary>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
public class AzureBlobArchiveLocationTests {
  private const string LEGACY = "Whizbang:Archive:AzureBlob";
  private const string NEW = "Whizbang:Storage:AzureBlob:Archive";

  private const string ACCOUNT_KEY = "c2VjcmV0LWFjY291bnQta2V5LW5vdC1yZWFs";

  private static AzureBlobArchiveObjectStore _store(BlobContainerClient container, AzureBlobArchiveOptions? options = null) =>
    new(container, options ?? new AzureBlobArchiveOptions { ContainerName = container.Name });

  [Test]
  public async Task ConfiguredLocation_FromAnAccountKeyConnectionString_IsTheEndpoint_NeverTheKeyAsync() {
    var container = new BlobContainerClient(
      $"DefaultEndpointsProtocol=https;AccountName=account;AccountKey={ACCOUNT_KEY};EndpointSuffix=core.windows.net", "archive-a");

    var location = _store(container, new AzureBlobArchiveOptions { ContainerName = "archive-a", ConnectionName = "archive" })
      .Location;

    await Assert.That(location).IsEqualTo(new ArchiveLocation("azure-blob", "https://account.blob.core.windows.net", "archive", "archive-a"));
    await Assert.That(location.ToString()).DoesNotContain(ACCOUNT_KEY).Because("a location is logical information only");
  }

  [Test]
  public async Task ConfiguredLocation_FromASharedAccessSignature_DropsTheSignatureAsync() {
    var container = new BlobContainerClient(
      "BlobEndpoint=https://account.blob.core.windows.net/;SharedAccessSignature=sv=2024-08-04&ss=b&sig=c2lnbmF0dXJl", "archive-a");
    var sasUri = new BlobContainerClient(new Uri("https://account.blob.core.windows.net/archive-b?sv=2024-08-04&sig=c2lnbmF0dXJl"));

    var fromConnectionString = _store(container).Location;
    var fromUri = _store(sasUri).Location;

    await Assert.That(fromConnectionString.Endpoint).IsEqualTo("https://account.blob.core.windows.net");
    await Assert.That(fromUri.Endpoint).IsEqualTo("https://account.blob.core.windows.net");
    await Assert.That(fromUri.Container).IsEqualTo("archive-b");
    await Assert.That(fromConnectionString.ToString() + fromUri).DoesNotContain("sig=")
      .Because("the query, where a signature sits, is never part of an endpoint");
  }

  [Test]
  public async Task ConfiguredLocation_ForTheEmulator_KeepsTheAccountInThePathAsync() {
    var location = _store(new BlobContainerClient("UseDevelopmentStorage=true", "archive-a")).Location;

    await Assert.That(location.Endpoint).IsEqualTo("http://127.0.0.1:10000/devstoreaccount1")
      .Because("a path-style endpoint names its account in the path, so two emulated accounts are two endpoints");
    await Assert.That(location.ConnectionName).IsEqualTo(AzureBlobArchiveOptions.DEFAULT_CONNECTION_NAME);
  }

  [Test]
  public async Task ConnectionName_DefaultsToDefault_AndIsBoundFromConfigurationAsync() {
    var bound = new AzureBlobArchiveOptions();
    var blank = new AzureBlobArchiveOptions();

    AzureBlobStorageConfiguration.BindArchive(
      new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionName"] = "archive" }).Build(), LEGACY, bound);
    AzureBlobStorageConfiguration.BindArchive(
      new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionName"] = " " }).Build(), LEGACY, blank);

    await Assert.That(new AzureBlobArchiveOptions().ConnectionName).IsEqualTo("default");
    await Assert.That(bound.ConnectionName).IsEqualTo("archive");
    await Assert.That(blank.ConnectionName).IsEqualTo("default").Because("a blank value keeps the default");
  }

  [Test]
  [Arguments("AccountName=a;AccountKey=k")]
  [Arguments("key=value")]
  public async Task ConnectionName_ThatLooksLikeAConnectionString_IsRefused_WhenBoundAndWhenBuiltAsync(string name) {
    var refusedBinding = await Assert.That(() => AzureBlobStorageConfiguration.BindArchive(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionName"] = name }).Build(),
        LEGACY, new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();
    var refusedStore = await Assert.That(() => _store(
        new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"), new AzureBlobArchiveOptions { ConnectionName = name }))
      .Throws<ArgumentException>();

    await Assert.That(refusedBinding!.Message).Contains("Whizbang:Archive:AzureBlob:ConnectionName").And.DoesNotContain(name)
      .Because("the refusal names the key, never echoes what may be a secret");
    await Assert.That(refusedStore!.Message).DoesNotContain(name);
  }

  [Test]
  public async Task ConnectionName_Empty_IsRefused_WhenBuiltAsync() {
    await Assert.That(() => _store(
        new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"), new AzureBlobArchiveOptions { ConnectionName = "" }))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task LocationMismatch_DefaultsToError_SoAMismatchStopsTheArchiveAsync() {
    var blank = new AzureBlobArchiveOptions();

    AzureBlobStorageConfiguration.BindArchive(_config(new() { ["LocationMismatch"] = " " }), NEW, blank);

    await Assert.That(new AzureBlobArchiveOptions().LocationMismatch).IsEqualTo(ArchiveLocationMismatchHandling.Error)
      .Because("the owner's decision (#2141): a mismatch stops the archive unless a service chooses otherwise");
    await Assert.That(blank.LocationMismatch).IsEqualTo(ArchiveLocationMismatchHandling.Error).Because("a blank value keeps the default");
  }

  [Test]
  [Arguments("Error", ArchiveLocationMismatchHandling.Error)]
  [Arguments("warning", ArchiveLocationMismatchHandling.Warning)]
  [Arguments(" INFO ", ArchiveLocationMismatchHandling.Info)]
  [Arguments("Ignore", ArchiveLocationMismatchHandling.Ignore)]
  public async Task LocationMismatch_BindsEveryHandling_InAnyCaseAsync(string value, ArchiveLocationMismatchHandling expected) {
    // Starts from another handling, so the value read is the one asserted, never a default left in place.
    var bound = new AzureBlobArchiveOptions {
      LocationMismatch = expected == ArchiveLocationMismatchHandling.Error ? ArchiveLocationMismatchHandling.Ignore : ArchiveLocationMismatchHandling.Error,
    };

    AzureBlobStorageConfiguration.BindArchive(_config(new() { ["LocationMismatch"] = value }), NEW, bound);

    await Assert.That(bound.LocationMismatch).IsEqualTo(expected);
    await Assert.That(bound.ToSettings().LocationMismatch).IsEqualTo(expected).Because("the coordinator reads the handling from the settings");
  }

  [Test]
  public async Task LocationMismatch_TheNewKeyWins_OverTheEarlierSectionAsync() {
    var configuration = _config(new() {
      [$"{LEGACY}:LocationMismatch"] = "Warning",
      [$"{NEW}:LocationMismatch"] = "Ignore",
    });
    var earlierOnly = new AzureBlobStorageOptions();
    var both = new AzureBlobStorageOptions();

    AzureBlobStorageConfiguration.Bind(_config(new() { [$"{LEGACY}:LocationMismatch"] = "Info" }), earlierOnly);
    AzureBlobStorageConfiguration.Bind(configuration, both);

    await Assert.That(earlierOnly.Archive!.LocationMismatch).IsEqualTo(ArchiveLocationMismatchHandling.Info)
      .Because("the earlier key is still read");
    await Assert.That(both.Archive!.LocationMismatch).IsEqualTo(ArchiveLocationMismatchHandling.Ignore).Because("the new key wins");
  }

  [Test]
  [Arguments("warn")]
  [Arguments("Information")]
  [Arguments("1")]
  [Arguments("false")]
  public async Task LocationMismatch_AValueThatIsNotAHandling_IsRefused_NamingTheKeyAndTheValuesAsync(string value) {
    var refused = await Assert.That(() => AzureBlobStorageConfiguration.BindArchive(
        _config(new() { ["LocationMismatch"] = value }), NEW, new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains($"{NEW}:LocationMismatch")
      .And.Contains("Error, Warning, Info or Ignore")
      .Because("a number or a near-miss is never read as some handling nobody chose");
  }

  [Test]
  [Arguments(NEW, "true")]
  [Arguments(NEW, "false")]
  [Arguments(LEGACY, "true")]
  [Arguments(LEGACY, "false")]
  public async Task VerifyLocation_IsRefused_NamingTheSettingThatReplacedItAsync(string section, string value) {
    var refused = await Assert.That(() => AzureBlobStorageConfiguration.Bind(
        _config(new() { [$"{section}:VerifyLocation"] = value }), new AzureBlobStorageOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains($"{section}:VerifyLocation")
      .And.Contains($"{NEW}:LocationMismatch")
      .Because("a removed setting is refused rather than silently ignored, so nobody's archive changes behavior unannounced");
  }

  [Test]
  public async Task Store_RefusesAHandlingThatIsNotDefinedAsync() {
    var options = new AzureBlobArchiveOptions { LocationMismatch = (ArchiveLocationMismatchHandling)42 };

    await Assert.That(() => _store(new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"), options))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(AzureBlobArchiveOptions.RefusalForLocationMismatch(options.LocationMismatch)).IsNotNull();
    await Assert.That(AzureBlobArchiveOptions.RefusalForLocationMismatch(ArchiveLocationMismatchHandling.Ignore)).IsNull();
  }

  [Test]
  public async Task ConnectionName_Whitespace_IsRefused_WhenBuiltAsync() {
    await Assert.That(() => _store(
        new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"), new AzureBlobArchiveOptions { ConnectionName = " \t" }))
      .Throws<ArgumentException>().Because("a name that normalizes to nothing names no connection");
  }

  private static IConfiguration _config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
