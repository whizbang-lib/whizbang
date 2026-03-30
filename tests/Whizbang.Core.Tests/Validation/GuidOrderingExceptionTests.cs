using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Tests.Validation;

public class GuidOrderingExceptionTests {
  [Test]
  public async Task DefaultConstructor_CreatesExceptionAsync() {
    var ex = new GuidOrderingException();
    await Assert.That(ex.Message).IsNotNull();
  }

  [Test]
  public async Task MessageConstructor_SetsMessageAsync() {
    var ex = new GuidOrderingException("test message");
    await Assert.That(ex.Message).IsEqualTo("test message");
  }

  [Test]
  public async Task MessageAndInnerConstructor_SetsBothAsync() {
    var inner = new InvalidOperationException("inner");
    var ex = new GuidOrderingException("test message", inner);
    await Assert.That(ex.Message).IsEqualTo("test message");
    await Assert.That(ex.InnerException).IsEqualTo(inner);
  }
}
