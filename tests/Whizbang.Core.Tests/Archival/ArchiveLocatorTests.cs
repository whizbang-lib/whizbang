// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The archive locator's text form (WS-3 S7): <c>{store}://{address}?codec={codec}&amp;format={format}</c>. The tombstone
/// stores it as text and every store reads it back, so the exact text is a contract: a reader must decode each archive by
/// the codec and format recorded on it, and must refuse a locator it cannot read rather than guess.
/// </summary>
/// <docs>fundamentals/events/archive-store#locator-format</docs>
public class ArchiveLocatorTests {
  [Test]
  public async Task ToString_WritesStoreAddressCodecAndFormat_InThatShapeAsync() {
    var locator = new ArchiveLocator("postgres-table", "svc.wh_event_archive", ArchiveCompression.None);

    await Assert.That(locator.ToString()).IsEqualTo("postgres-table://svc.wh_event_archive?codec=none&format=1")
      .Because("the tombstone text is read by every store; its exact shape is the contract");
  }

  [Test]
  [Arguments(ArchiveCompression.None, "none")]
  [Arguments(ArchiveCompression.Brotli, "brotli")]
  public async Task Codec_IsRecordedPerArchive_AndReadBackAsync(ArchiveCompression compression, string token) {
    var text = new ArchiveLocator("azure-blob", "whizbang-archive/streams/1", compression, format: 3).ToString();

    await Assert.That(text).IsEqualTo($"azure-blob://whizbang-archive/streams/1?codec={token}&format=3");
    await Assert.That(ArchiveLocator.TryParse(text, out var read)).IsTrue();
    await Assert.That(read!.Compression).IsEqualTo(compression)
      .Because("a reader decodes each archive by the codec written on it, so a store can change codec later");
    await Assert.That(read.Format).IsEqualTo(3);
    await Assert.That(read.Store).IsEqualTo("azure-blob");
    await Assert.That(read.Address).IsEqualTo("whizbang-archive/streams/1");
    await Assert.That(read).IsEqualTo(new ArchiveLocator("azure-blob", "whizbang-archive/streams/1", compression, 3));
  }

  [Test]
  public async Task Address_KeepsSchemeLikeText_BecauseOnlyTheFirstSeparatorSplitsTheStoreAsync() {
    // Not a Uri: a blob address may itself hold "://" and must come back unchanged.
    var text = new ArchiveLocator("blob", "https://acct/c", ArchiveCompression.Brotli).ToString();

    await Assert.That(ArchiveLocator.TryParse(text, out var read)).IsTrue();
    await Assert.That(read!.Address).IsEqualTo("https://acct/c");
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("no-separator?codec=none&format=1")]
  [Arguments("://addr?codec=none&format=1")]
  [Arguments("store://?codec=none&format=1")]
  [Arguments("store://addr")]
  [Arguments("store://addr?codec=none")]
  [Arguments("store://addr?codec=none&format=1&extra=1")]
  [Arguments("store://addr?format=1&codec=none")]
  [Arguments("store://addr?codec=none&fmt=1")]
  [Arguments("store://addr?codec=zstd&format=1")]
  [Arguments("store://addr?codec=none&format=x")]
  [Arguments("store://addr?codec=none&format=-1")]
  [Arguments("store://addr?codec=none&format=0")]
  [Arguments("Store://addr?codec=none&format=1")]
  public async Task TryParse_RefusesWhatItCannotRead_RatherThanGuessingAsync(string? text) {
    await Assert.That(ArchiveLocator.TryParse(text, out var read)).IsFalse()
      .Because("an unknown codec, a missing part or a bad store name must never be read as some default archive");
    await Assert.That(read).IsNull();
  }

  [Test]
  public async Task TryParse_TheSmallestValidLocator_IsReadAsync() {
    // The control for the refusals above: the parser accepts the minimal well-formed text.
    await Assert.That(ArchiveLocator.TryParse("s://a?codec=none&format=1", out var read)).IsTrue();
    await Assert.That(read!.ToString()).IsEqualTo("s://a?codec=none&format=1");
  }

  [Test]
  [Arguments("")]
  [Arguments("Upper")]
  [Arguments("has space")]
  [Arguments("under_score")]
  public async Task Constructor_RefusesAStoreNameTheTextFormCannotCarryAsync(string store) {
    await Assert.That(() => new ArchiveLocator(store, "addr", ArchiveCompression.None)).Throws<ArgumentException>();
  }

  [Test]
  [Arguments("")]
  [Arguments("a?b")]
  public async Task Constructor_RefusesAnAddressTheTextFormCannotCarryAsync(string address) {
    await Assert.That(() => new ArchiveLocator("store", address, ArchiveCompression.None)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_RefusesAFormatBelowOne_AndAnUndefinedCodecAsync() {
    await Assert.That(() => new ArchiveLocator("store", "addr", ArchiveCompression.None, format: 0))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new ArchiveLocator("store", "addr", (ArchiveCompression)99))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a codec with no text name could never be read back");
    await Assert.That(new ArchiveLocator("store-2", "addr", ArchiveCompression.None, format: 1).Format).IsEqualTo(1)
      .Because("the control: format 1 and a store with a digit and a hyphen are accepted");
  }
}
