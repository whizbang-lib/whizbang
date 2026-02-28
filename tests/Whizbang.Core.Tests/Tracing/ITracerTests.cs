using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for ITracer interface which provides the main tracing API.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/ITracer.cs</code-under-test>
public class ITracerTests {
  #region Interface Definition Tests

  [Test]
  public async Task ITracer_IsInterfaceAsync() {
    // Arrange
    var type = typeof(ITracer);

    // Assert
    await Assert.That(type.IsInterface).IsTrue();
  }

  [Test]
  public async Task ITracer_HasBeginTraceMethodAsync() {
    // Arrange
    var type = typeof(ITracer);
    var method = type.GetMethod("BeginTrace");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(ITraceScope));
  }

  [Test]
  public async Task ITracer_HasShouldTraceMethodAsync() {
    // Arrange
    var type = typeof(ITracer);
    var method = type.GetMethod("ShouldTrace");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(bool));
  }

  [Test]
  public async Task ITracer_HasGetEffectiveVerbosityMethodAsync() {
    // Arrange
    var type = typeof(ITracer);
    var method = type.GetMethod("GetEffectiveVerbosity");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(TraceVerbosity));
  }

  #endregion

  #region ITraceScope Tests

  [Test]
  public async Task ITraceScope_IsInterfaceAsync() {
    // Arrange
    var type = typeof(ITraceScope);

    // Assert
    await Assert.That(type.IsInterface).IsTrue();
  }

  [Test]
  public async Task ITraceScope_IsDisposableAsync() {
    // Arrange
    var type = typeof(ITraceScope);

    // Assert - Should implement IDisposable for using pattern
    await Assert.That(typeof(IDisposable).IsAssignableFrom(type)).IsTrue();
  }

  [Test]
  public async Task ITraceScope_HasCompleteMethodAsync() {
    // Arrange
    var type = typeof(ITraceScope);
    var method = type.GetMethod("Complete");

    // Assert
    await Assert.That(method).IsNotNull();
  }

  [Test]
  public async Task ITraceScope_HasFailMethodAsync() {
    // Arrange
    var type = typeof(ITraceScope);
    var method = type.GetMethod("Fail");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.GetParameters().Length).IsEqualTo(1);
    await Assert.That(method.GetParameters()[0].ParameterType).IsEqualTo(typeof(Exception));
  }

  [Test]
  public async Task ITraceScope_HasEarlyReturnMethodAsync() {
    // Arrange
    var type = typeof(ITraceScope);
    var method = type.GetMethod("EarlyReturn");

    // Assert
    await Assert.That(method).IsNotNull();
  }

  #endregion
}
