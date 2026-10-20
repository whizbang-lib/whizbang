// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// <see cref="NameBasedGuid"/> is RFC 4122 version 5, byte for byte, so an id derived here equals the one
/// any other implementation (Postgres <c>uuid_generate_v5</c>, Python <c>uuid.uuid5</c>) derives.
/// </summary>
[Category("ValueObjects")]
public class NameBasedGuidTests {
  private static readonly Guid _dnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

  [Test]
  public async Task V5_RfcDnsNamespaceVector_MatchesTheReferenceImplementationAsync() {
    var id = NameBasedGuid.V5(_dnsNamespace, "www.example.com");

    await Assert.That(id).IsEqualTo(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"))
      .Because("the vector is what Python's uuid.uuid5(NAMESPACE_DNS, 'www.example.com') returns; any other value is not version 5");
  }

  [Test]
  public async Task V5_SameInputs_ProduceTheSameIdAsync() {
    await Assert.That(NameBasedGuid.V5(_dnsNamespace, "tenant-a"))
      .IsEqualTo(NameBasedGuid.V5(_dnsNamespace, "tenant-a"));
  }

  [Test]
  public async Task V5_DifferentNames_ProduceDifferentIdsAsync() {
    await Assert.That(NameBasedGuid.V5(_dnsNamespace, "tenant-a"))
      .IsNotEqualTo(NameBasedGuid.V5(_dnsNamespace, "tenant-b"));
  }

  [Test]
  public async Task V5_DifferentNamespaces_ProduceDifferentIdsAsync() {
    await Assert.That(NameBasedGuid.V5(_dnsNamespace, "tenant-a"))
      .IsNotEqualTo(NameBasedGuid.V5(Guid.Empty, "tenant-a"));
  }

  [Test]
  public async Task V5_EmptyName_IsAValidNameWithItsOwnIdAsync() {
    var id = NameBasedGuid.V5(Guid.Empty, string.Empty);

    await Assert.That(id).IsEqualTo(new Guid("e129f27c-5103-5c5c-844b-cdf0a15e160d"))
      .Because("uuid.uuid5(nil, '') is a defined id, and a tenantless record needs one");
  }

  [Test]
  public async Task V5_StampsVersionFiveAndTheRfcVariantAsync() {
    var bytes = NameBasedGuid.V5(_dnsNamespace, "anything").ToByteArray(bigEndian: true);

    await Assert.That(bytes[6] >> 4).IsEqualTo(5);
    await Assert.That(bytes[8] >> 6).IsEqualTo(2);
  }

  [Test]
  public async Task V5_NullName_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => NameBasedGuid.V5(_dnsNamespace, null!)).Throws<ArgumentNullException>();
  }
}
