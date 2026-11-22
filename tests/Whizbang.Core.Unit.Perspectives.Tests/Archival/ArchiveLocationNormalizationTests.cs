// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Core.Tests.Archival;

// Plain http endpoints are the subject here (the storage emulator, and the default-port rule for http): no request is sent.
#pragma warning disable S5332

/// <summary>
/// What "the same archive location" means (#2141): the database stores a location's normalized form, and two locations
/// are one when their normalized forms are equal. A difference only in how a location is written is not a mismatch:
/// surrounding whitespace, letter case, a trailing slash on the endpoint, and the scheme's default port written out
/// (443 for https, 80 for http). Every other difference is another location: another scheme, another host (another
/// account, the account's secondary endpoint, a custom domain), another port, another path, another connection name,
/// another container. The SQL helpers of migration 553 apply the same rules; ArchiveLocationSqlTests locks the two
/// together over the same cases.
/// </summary>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
public class ArchiveLocationNormalizationTests {
  private const string ACCOUNT = "https://account.blob.core.windows.net";

  [Test]
  [Arguments("https://account.blob.core.windows.net", ACCOUNT)]
  [Arguments("HTTPS://Account.Blob.Core.Windows.NET", ACCOUNT)]
  [Arguments("https://account.blob.core.windows.net/", ACCOUNT)]
  [Arguments("https://account.blob.core.windows.net//", ACCOUNT)]
  [Arguments(" \thttps://account.blob.core.windows.net \r\n", ACCOUNT)]
  [Arguments("https://account.blob.core.windows.net:443", ACCOUNT)]
  [Arguments("https://ACCOUNT.blob.core.windows.net:443/", ACCOUNT)]
  [Arguments("http://127.0.0.1:80/devstoreaccount1", "http://127.0.0.1/devstoreaccount1")]
  [Arguments("http://127.0.0.1:10000/DevStoreAccount1/", "http://127.0.0.1:10000/devstoreaccount1")]
  [Arguments("http://[::1]:80/devstoreaccount1", "http://[::1]/devstoreaccount1")]
  public async Task NormalizeEndpoint_DropsWhatOnlyChangesHowTheEndpointIsWrittenAsync(string written, string normalized) {
    await Assert.That(ArchiveLocation.NormalizeEndpoint(written)).IsEqualTo(normalized);
  }

  [Test]
  [Arguments("http://account.blob.core.windows.net", "another scheme")]
  [Arguments("https://account.blob.core.windows.net:4430", "another port")]
  [Arguments("https://account.blob.core.windows.net:8443", "another port")]
  [Arguments("http://account.blob.core.windows.net:443", "443 is not http's default port")]
  [Arguments("https://account-secondary.blob.core.windows.net", "the account's secondary endpoint")]
  [Arguments("https://other.blob.core.windows.net", "another account")]
  [Arguments("https://archive.example.com", "a custom domain")]
  [Arguments("https://account.blob.core.windows.net/account2", "another path")]
  public async Task NormalizeEndpoint_KeepsEveryDifferenceThatIsAnotherPlaceAsync(string written, string why) {
    await Assert.That(ArchiveLocation.NormalizeEndpoint(written)).IsNotEqualTo(ACCOUNT).Because(why);
  }

  [Test]
  public async Task NormalizeEndpoint_KeepsAPortThatIsNotTheSchemesDefault_AndAnEndpointNotRecordedAsync() {
    await Assert.That(ArchiveLocation.NormalizeEndpoint("http://account:443")).IsEqualTo("http://account:443");
    await Assert.That(ArchiveLocation.NormalizeEndpoint("https://account:80")).IsEqualTo("https://account:80");
    await Assert.That(ArchiveLocation.NormalizeEndpoint("https://account:4430/")).IsEqualTo("https://account:4430");
    await Assert.That(ArchiveLocation.NormalizeEndpoint("ftp://account:443")).IsEqualTo("ftp://account:443")
      .Because("only http and https have a default port to drop");
    await Assert.That(ArchiveLocation.NormalizeEndpoint(null)).IsNull()
      .Because("a location recorded before endpoints were has none, and normalizing it does not invent one");
  }

  [Test]
  [Arguments("archive", "archive")]
  [Arguments(" Archive ", "archive")]
  [Arguments("ARCHIVE-A", "archive-a")]
  [Arguments("\tdefault\r\n", "default")]
  public async Task NormalizeName_IgnoresSurroundingWhitespaceAndCaseAsync(string written, string normalized) {
    await Assert.That(ArchiveLocation.NormalizeName(written)).IsEqualTo(normalized);
    await Assert.That(ArchiveLocation.NormalizeName(null)).IsNull();
  }

  [Test]
  public async Task Normalize_TheSameLocationWrittenDifferently_IsOneLocation_AndIsNotChangedAgainAsync() {
    var stored = new ArchiveLocation("azure-blob", ACCOUNT, "archive", "archive-a");
    var written = new ArchiveLocation(" Azure-Blob", "HTTPS://Account.Blob.Core.Windows.Net:443/", " Archive", "Archive-A ");

    await Assert.That(written.Normalize()).IsEqualTo(stored);
    await Assert.That(stored.Normalize()).IsEqualTo(stored).Because("a normalized location is its own normalized form");
    await Assert.That(new ArchiveLocation("azure-blob", null, null, "Archive-A").Normalize())
      .IsEqualTo(new ArchiveLocation("azure-blob", null, null, "archive-a"))
      .Because("a location recorded before endpoints and names were keeps neither");
  }

  [Test]
  public async Task Normalize_AnotherAccountContainerOrConnectionName_StaysAnotherLocationAsync() {
    var stored = new ArchiveLocation("azure-blob", ACCOUNT, "archive", "archive-a");

    await Assert.That(stored with { Endpoint = "https://dr.blob.core.windows.net" }).IsNotEqualTo(stored.Normalize())
      .Because("failover to another account is another location");
    await Assert.That((stored with { Container = "archive-restored" }).Normalize()).IsNotEqualTo(stored)
      .Because("storage restored into another container is another location");
    await Assert.That((stored with { ConnectionName = "staging" }).Normalize()).IsNotEqualTo(stored)
      .Because("a database copied to another environment names that environment's connection");
  }
}
