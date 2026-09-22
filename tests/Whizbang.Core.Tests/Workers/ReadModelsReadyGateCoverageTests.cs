using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="WhizbangNotReadyException"/>'s parameterless and
/// message-plus-cause constructors — standard exception-type boilerplate that the framework's own
/// throw site (<see cref="ReadModelsGuard.ThrowIfNotReady"/>) never exercises, since it always
/// supplies a message and no inner exception. Both are still public API surface: a consumer
/// catching and rethrowing with an inner cause, or constructing one directly, must get a real
/// <see cref="InvalidOperationException"/>-shaped exception, not one whose base-class wiring silently
/// dropped the argument.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReadModelsReadyGate.cs</code-under-test>
public class ReadModelsReadyGateCoverageTests {

  [Test]
  public async Task WhizbangNotReadyException_ParameterlessConstructor_ProducesUsableExceptionAsync() {
    var exception = new WhizbangNotReadyException();

    await Assert.That(exception).IsAssignableTo<InvalidOperationException>();
    await Assert.That(exception.Message).IsNotNull();
  }

  [Test]
  public async Task WhizbangNotReadyException_MessageAndInnerException_PreservesBothAsync() {
    var inner = new InvalidOperationException("schema migration failed");

    var exception = new WhizbangNotReadyException("lens read refused", inner);

    await Assert.That(exception.Message).IsEqualTo("lens read refused");
    await Assert.That(exception.InnerException).IsSameReferenceAs(inner)
      .Because("a caller that wraps a lower-level failure as the reason the barrier never opened "
             + "must not lose that cause — losing it turns a diagnosable startup failure into an "
             + "unexplained refusal");
  }
}
