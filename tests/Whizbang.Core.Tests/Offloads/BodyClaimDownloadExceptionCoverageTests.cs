using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Targeted coverage for <see cref="BodyClaimDownloadException"/> constructor overloads the
/// broader <see cref="BodyClaimDownloadExceptionTests"/> suite doesn't reach: the message-only
/// form (no wrapped inner exception).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/BodyClaimDownloadException.cs</code-under-test>
public class BodyClaimDownloadExceptionCoverageTests {

  [Test]
  public async Task TheMessageOnlyForm_CarriesTheGivenMessageWithNoInnerExceptionAsync() {
    // A download failure that never wrapped an underlying exception (e.g. a hand-thrown
    // validation failure inside the rehydrator) must still surface a specific, readable message —
    // if this constructor silently dropped or mangled it, the log line would say nothing about
    // which claim failed, and the transient-vs-permanent distinction the whole type exists for
    // would be unreadable at the point an operator needs it.
    var ex = new BodyClaimDownloadException("download failed for claim xyz: store unavailable");

    await Assert.That(ex.Message).IsEqualTo("download failed for claim xyz: store unavailable");
    await Assert.That(ex.InnerException).IsNull()
      .Because("this overload never wraps a cause; nothing should be fabricated when there isn't one");
  }
}
