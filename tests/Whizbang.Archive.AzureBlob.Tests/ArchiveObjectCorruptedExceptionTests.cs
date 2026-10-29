// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Archive.AzureBlob.Tests;

/// <summary>The corruption report names the stream, the object and both hashes, so an operator can find the object.</summary>
/// <docs>fundamentals/events/archive-store#azure-blob-integrity</docs>
public class ArchiveObjectCorruptedExceptionTests {
  [Test]
  public async Task TheObjectsConstructor_NamesTheStreamTheObjectAndBothHashesAsync() {
    var stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");

    var ex = new ArchiveObjectCorruptedException(stream, "azure-blob://c/o?codec=brotli&format=1", 10, 9, "aa", "bb");

    await Assert.That(ex.StreamId).IsEqualTo(stream);
    await Assert.That(ex.Locator).IsEqualTo("azure-blob://c/o?codec=brotli&format=1");
    await Assert.That(ex.ExpectedSha256).IsEqualTo("aa");
    await Assert.That(ex.ActualSha256).IsEqualTo("bb");
    await Assert.That(ex.Message).Contains("recorded 10 bytes with SHA-256 aa").And.Contains("returned 9 bytes with SHA-256 bb");
    await Assert.That(ex).IsNotAssignableTo<Azure.RequestFailedException>()
      .Because("a corrupted object is a data fault, not a storage error a retry policy would retry");
  }

  [Test]
  public async Task TheStandardConstructors_LeaveTheDetailsUnknownAsync() {
    var inner = new InvalidOperationException("cause");

    var bare = new ArchiveObjectCorruptedException();
    var withMessage = new ArchiveObjectCorruptedException("corrupted");
    var withCause = new ArchiveObjectCorruptedException("corrupted", inner);

    await Assert.That(bare.StreamId).IsNull();
    await Assert.That(bare.Locator).IsNull();
    await Assert.That(withMessage.Message).IsEqualTo("corrupted");
    await Assert.That(withMessage.ExpectedSha256).IsNull();
    await Assert.That(withCause.InnerException).IsSameReferenceAs(inner);
    await Assert.That(withCause.ActualSha256).IsNull();
  }
}
