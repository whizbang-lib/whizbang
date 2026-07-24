using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

public class ReceptorNotFoundExceptionTests {
  [Test]
  public async Task PrimaryConstructor_SetsMessageTypeAsync() {
    var ex = new ReceptorNotFoundException(typeof(string));

    await Assert.That(ex.MessageType).IsEqualTo(typeof(string));
    await Assert.That(ex.Message).Contains("System.String");
  }

  [Test]
  public async Task DefaultConstructor_UsesObjectTypeAsync() {
    var ex = new ReceptorNotFoundException();

    await Assert.That(ex.MessageType).IsEqualTo(typeof(object));
  }

  [Test]
  public async Task StringConstructor_UsesObjectTypeAsync() {
    var ex = new ReceptorNotFoundException("custom message");

    await Assert.That(ex.MessageType).IsEqualTo(typeof(object));
  }

  [Test]
  public async Task StringAndExceptionConstructor_UsesObjectTypeAsync() {
    var inner = new InvalidOperationException("inner");
    var ex = new ReceptorNotFoundException("custom message", inner);

    await Assert.That(ex.MessageType).IsEqualTo(typeof(object));
  }

  [Test]
  public async Task Message_ContainsHelpfulInstructionsAsync() {
    var ex = new ReceptorNotFoundException(typeof(string));

    await Assert.That(ex.Message).Contains("No receptor found for message type");
    await Assert.That(ex.Message).Contains("IReceptor<String, TResponse>");
    await Assert.That(ex.Message).Contains("auto-discovered at compile time");
  }
}
