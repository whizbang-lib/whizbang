using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// The exception that distinguishes a retryable offload download from a permanent one.
/// <para>
/// Its whole purpose is to be thrown rather than converted into a dead-letter result: a body that
/// cannot be downloaded right now is usually the store being briefly unavailable, so throwing lets
/// the transport redeliver for a bounded automatic retry. Permanent faults — unknown provider, hash
/// mismatch, deserialization failure — stay terminal. Losing the inner exception on the way through
/// would erase the only evidence of which of those actually happened.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/BodyClaimDownloadException.cs</code-under-test>
public class BodyClaimDownloadExceptionTests {

  [Test]
  public async Task TheWrappingForm_KeepsTheUnderlyingFailureAsync() {
    var cause = new HttpRequestException("503 from the blob store");

    var ex = new BodyClaimDownloadException("download failed for claim abc", cause);

    await Assert.That(ex.Message).IsEqualTo("download failed for claim abc");
    await Assert.That(ex.InnerException).IsSameReferenceAs(cause)
      .Because("the inner exception is the only record of whether this was transient or permanent, "
             + "and the retry decision downstream is made on exactly that");
  }

  [Test]
  public async Task TheParameterlessForm_StillCarriesAUsableMessageAsync() {
    var ex = new BodyClaimDownloadException();

    await Assert.That(ex.Message).IsNotNull().And.IsNotEmpty()
      .Because("a thrown exception with no message reaches a log as a bare type name, which tells "
             + "an operator nothing about which claim failed");
  }
}
