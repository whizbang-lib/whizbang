// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Data.Postgres;

namespace Whizbang.Archive.AzureBlob.Tests;

/// <summary>
/// The location a store is configured for (#2137): the provider, the storage account's endpoint derived from the
/// configured client (never a key, a signature or a connection string), the logical connection name and the container.
/// It is what every archive record is checked against. Nothing here opens a connection.
/// </summary>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
public class AzureBlobArchiveLocationTests {
  private const string ACCOUNT_KEY = "c2VjcmV0LWFjY291bnQta2V5LW5vdC1yZWFs";

  private static readonly PostgresArchiveConnection _database =
    new(_ => throw new InvalidOperationException("no database in a unit test"), Schema: null);

  private static AzureBlobArchiveStore _store(BlobContainerClient container, AzureBlobArchiveOptions? options = null) =>
    new(_database, container, options ?? new AzureBlobArchiveOptions { ContainerName = container.Name });

  [Test]
  public async Task ConfiguredLocation_FromAnAccountKeyConnectionString_IsTheEndpoint_NeverTheKeyAsync() {
    var container = new BlobContainerClient(
      $"DefaultEndpointsProtocol=https;AccountName=account;AccountKey={ACCOUNT_KEY};EndpointSuffix=core.windows.net", "archive-a");

    var location = _store(container, new AzureBlobArchiveOptions { ContainerName = "archive-a", ConnectionName = "archive" })
      .ConfiguredLocation;

    await Assert.That(location).IsEqualTo(new ArchiveLocation("azure-blob", "https://account.blob.core.windows.net", "archive", "archive-a"));
    await Assert.That(location.ToString()).DoesNotContain(ACCOUNT_KEY).Because("a location is logical information only");
  }

  [Test]
  public async Task ConfiguredLocation_FromASharedAccessSignature_DropsTheSignatureAsync() {
    var container = new BlobContainerClient(
      "BlobEndpoint=https://account.blob.core.windows.net/;SharedAccessSignature=sv=2024-08-04&ss=b&sig=c2lnbmF0dXJl", "archive-a");
    var sasUri = new BlobContainerClient(new Uri("https://account.blob.core.windows.net/archive-b?sv=2024-08-04&sig=c2lnbmF0dXJl"));

    var fromConnectionString = _store(container).ConfiguredLocation;
    var fromUri = _store(sasUri).ConfiguredLocation;

    await Assert.That(fromConnectionString.Endpoint).IsEqualTo("https://account.blob.core.windows.net");
    await Assert.That(fromUri.Endpoint).IsEqualTo("https://account.blob.core.windows.net");
    await Assert.That(fromUri.Container).IsEqualTo("archive-b");
    await Assert.That(fromConnectionString.ToString() + fromUri).DoesNotContain("sig=")
      .Because("the query, where a signature sits, is never part of an endpoint");
  }

  [Test]
  public async Task ConfiguredLocation_ForTheEmulator_KeepsTheAccountInThePathAsync() {
    var location = _store(new BlobContainerClient("UseDevelopmentStorage=true", "archive-a")).ConfiguredLocation;

    await Assert.That(location.Endpoint).IsEqualTo("http://127.0.0.1:10000/devstoreaccount1")
      .Because("a path-style endpoint names its account in the path, so two emulated accounts are two endpoints");
    await Assert.That(location.ConnectionName).IsEqualTo(AzureBlobArchiveOptions.DEFAULT_CONNECTION_NAME);
  }

  [Test]
  public async Task ConnectionName_DefaultsToDefault_AndIsBoundFromConfigurationAsync() {
    var bound = new AzureBlobArchiveOptions();
    var blank = new AzureBlobArchiveOptions();

    AzureBlobArchiveServiceCollectionExtensions.Bind(
      new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionName"] = "archive" }).Build(), bound);
    AzureBlobArchiveServiceCollectionExtensions.Bind(
      new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionName"] = " " }).Build(), blank);

    await Assert.That(new AzureBlobArchiveOptions().ConnectionName).IsEqualTo("default");
    await Assert.That(bound.ConnectionName).IsEqualTo("archive");
    await Assert.That(blank.ConnectionName).IsEqualTo("default").Because("a blank value keeps the default");
  }

  [Test]
  [Arguments("AccountName=a;AccountKey=k")]
  [Arguments("key=value")]
  public async Task ConnectionName_ThatLooksLikeAConnectionString_IsRefused_WhenBoundAndWhenBuiltAsync(string name) {
    var refusedBinding = await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.Bind(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionName"] = name }).Build(),
        new AzureBlobArchiveOptions()))
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

    AzureBlobArchiveServiceCollectionExtensions.Bind(_config(new() { ["LocationMismatch"] = " " }), blank);

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

    AzureBlobArchiveServiceCollectionExtensions.Bind(_config(new() { ["LocationMismatch"] = value }), bound);

    await Assert.That(bound.LocationMismatch).IsEqualTo(expected);
  }

  [Test]
  [Arguments("warn")]
  [Arguments("Information")]
  [Arguments("1")]
  [Arguments("false")]
  public async Task LocationMismatch_AValueThatIsNotAHandling_IsRefused_NamingTheKeyAndTheValuesAsync(string value) {
    var refused = await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.Bind(
        _config(new() { ["LocationMismatch"] = value }), new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("Whizbang:Archive:AzureBlob:LocationMismatch")
      .And.Contains("Error, Warning, Info or Ignore")
      .Because("a number or a near-miss is never read as some handling nobody chose");
  }

  [Test]
  [Arguments("true")]
  [Arguments("false")]
  public async Task VerifyLocation_IsRefused_NamingTheSettingThatReplacedItAsync(string value) {
    var refused = await Assert.That(() => AzureBlobArchiveServiceCollectionExtensions.Bind(
        _config(new() { ["VerifyLocation"] = value }), new AzureBlobArchiveOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains("Whizbang:Archive:AzureBlob:VerifyLocation")
      .And.Contains("Whizbang:Archive:AzureBlob:LocationMismatch")
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
  public async Task ConfiguredLocation_IsStoredNormalized_WhateverCaseAndWhitespaceTheConfigurationUsesAsync() {
    var location = _store(
        new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"),
        new AzureBlobArchiveOptions { ContainerName = "archive-a", ConnectionName = " Archive " })
      .ConfiguredLocation;

    await Assert.That(location.ConnectionName).IsEqualTo("archive")
      .Because("the pin is stored normalized, so the same name written another way is not a mismatch");
    await Assert.That(location).IsEqualTo(location.Normalize());
  }

  [Test]
  public async Task ConnectionName_Whitespace_IsRefused_WhenBuiltAsync() {
    await Assert.That(() => _store(
        new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"), new AzureBlobArchiveOptions { ConnectionName = " \t" }))
      .Throws<ArgumentException>().Because("a name that normalizes to nothing names no connection");
  }

  private static IConfiguration _config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

  [Test]
  public async Task RepointLocation_RefusesAnIdThatCannotNameARowAsync() {
    var store = _store(new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"));

    await Assert.That(() => store.RepointLocationAsync(0)).Throws<ArgumentOutOfRangeException>();
  }
}
