using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// The exception raised when a managed resource does not acknowledge a lifecycle phase in time.
/// <para>
/// The context-carrying constructor is the one that matters operationally: a phase transition waits
/// on every resource at once, so an exception that says only "something timed out" leaves an
/// operator to work out WHICH resource stalled the whole system from surrounding logs. Naming the
/// component and the phase in the message is what turns a halted deploy into a specific thing to go
/// look at.
/// </para>
/// <para>
/// The parameterless and message-only constructors exist to satisfy the standard exception shape —
/// serializers, rethrow helpers, and analyzers expect them — and they were untested, so nothing
/// held them to producing a usable message.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/RunControl/LifecycleAckTimeoutException.cs</code-under-test>
public class LifecycleAckTimeoutExceptionTests {

  [Test]
  public async Task TheContextForm_NamesTheComponentAndThePhaseAsync() {
    var ex = new LifecycleAckTimeoutException("transport", LifecyclePhase.Stopping);

    await Assert.That(ex.Component).IsEqualTo("transport");
    await Assert.That(ex.Phase).IsEqualTo(LifecyclePhase.Stopping);
    await Assert.That(ex.Message).Contains("transport")
      .Because("a transition waits on every resource at once, so the message has to say which one "
             + "stalled or the operator is left correlating logs to find it");
    await Assert.That(ex.Message).Contains("Stopping");
  }

  [Test]
  public async Task TheStandardForms_StillProduceUsableExceptionsAsync() {
    var bare = new LifecycleAckTimeoutException();
    var withMessage = new LifecycleAckTimeoutException("ack window elapsed");
    var cause = new TimeoutException("inner");
    var wrapped = new LifecycleAckTimeoutException("ack window elapsed", cause);

    await Assert.That(bare.Message).IsNotNull().And.IsNotEmpty();
    await Assert.That(withMessage.Message).IsEqualTo("ack window elapsed");
    await Assert.That(wrapped.InnerException).IsSameReferenceAs(cause);
    await Assert.That(bare.Component).IsNull()
      .Because("the standard forms carry no context, and inventing one would name a component that "
             + "did not actually time out");
  }
}
