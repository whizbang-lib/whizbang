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
  public async Task RepointLocation_RefusesAnIdThatCannotNameARowAsync() {
    var store = _store(new BlobContainerClient("UseDevelopmentStorage=true", "archive-a"));

    await Assert.That(() => store.RepointLocationAsync(0)).Throws<ArgumentOutOfRangeException>();
  }
}
