using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// The exception raised when tag policy configuration is itself wrong.
/// <para>
/// This is a startup-time fault, not a runtime one: the policy that decides where tagged messages
/// route has been declared in a way that cannot be honored. Failing loudly here is the point —
/// a mis-declared routing policy that starts anyway sends messages somewhere nobody intended, and
/// the symptom appears later as messages missing from a destination rather than as a configuration
/// error anyone can trace back.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/TagPolicyConfigurationException.cs</code-under-test>
public class TagPolicyConfigurationExceptionTests {

  [Test]
  public async Task TheWrappingForm_KeepsTheCauseThatExplainsTheViolationAsync() {
    var cause = new FormatException("'orders.*.eu' is not a valid namespace key");

    var ex = new TagPolicyConfigurationException("tag policy 'regional' is invalid", cause);

    await Assert.That(ex.Message).IsEqualTo("tag policy 'regional' is invalid");
    await Assert.That(ex.InnerException).IsSameReferenceAs(cause)
      .Because("the outer message names the policy and the inner one says what about it was "
             + "wrong; dropping the inner leaves an operator with only the former");
  }

  [Test]
  public async Task TheParameterlessForm_StillCarriesAUsableMessageAsync() {
    var ex = new TagPolicyConfigurationException();

    await Assert.That(ex.Message).IsNotNull().And.IsNotEmpty()
      .Because("this fault aborts startup, so the message is the whole of what an operator sees");
  }
}
