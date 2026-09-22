using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// The exception raised when a stream identifier fails validation.
/// <para>
/// A stream id is the partition key for everything that follows — ordering, claim leases, and
/// perspective cursors all hang off it — so an invalid one has to stop at the boundary rather than
/// be written and discovered later as a stream nothing can be correlated with. The message and the
/// inner exception are what tell an operator which value was rejected and why.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Validation/InvalidStreamIdException.cs</code-under-test>
public class InvalidStreamIdExceptionTests {

  [Test]
  public async Task TheParameterlessForm_StillCarriesAUsableMessageAsync() {
    var ex = new InvalidStreamIdException();

    await Assert.That(ex.Message).IsNotNull().And.IsNotEmpty()
      .Because("a bare type name in a log tells an operator nothing about which id was rejected");
  }

  [Test]
  public async Task TheWrappingForm_KeepsTheValidationFailureThatCausedItAsync() {
    var cause = new FormatException("'not-a-guid' is not a valid identifier");

    var ex = new InvalidStreamIdException("stream id rejected", cause);

    await Assert.That(ex.Message).IsEqualTo("stream id rejected");
    await Assert.That(ex.InnerException).IsSameReferenceAs(cause)
      .Because("the outer message says a stream id was rejected and the inner one says what about "
             + "it was wrong; dropping the inner leaves only half the answer");
  }
}
