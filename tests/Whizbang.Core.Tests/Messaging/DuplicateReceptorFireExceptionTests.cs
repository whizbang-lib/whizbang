using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks every constructor on <see cref="DuplicateReceptorFireException"/>:
/// the rich diagnostic constructor (receptorId / stages / messageId / prior
/// invocation) plus the three <c>Exception</c>-convention constructors
/// (parameterless / message / message+inner). All four are public surface and
/// were under-tested — coverage tracker had the class at 33%.
/// </summary>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public class DuplicateReceptorFireExceptionTests {

  [Test]
  public async Task RichConstructor_PopulatesAllPropertiesAndComposesMessageAsync() {
    var messageId = Guid.NewGuid();
    var priorInvocation = new ReceptorInvocationRecord {
      ReceptorId = "MyReceptor",
      Stage = LifecycleStage.PreOutboxInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.FromMilliseconds(12),
      ServiceName = "test-service",
    };

    var ex = new DuplicateReceptorFireException(
      receptorId: "MyReceptor",
      currentStage: LifecycleStage.PostInboxInline,
      priorStage: LifecycleStage.PreOutboxInline,
      messageId: messageId,
      priorInvocation: priorInvocation);

    await Assert.That(ex.ReceptorId).IsEqualTo("MyReceptor");
    await Assert.That(ex.CurrentStage).IsEqualTo(LifecycleStage.PostInboxInline);
    await Assert.That(ex.PriorStage).IsEqualTo(LifecycleStage.PreOutboxInline);
    await Assert.That(ex.MessageId).IsEqualTo(messageId);
    await Assert.That(ex.PriorInvocation).IsSameReferenceAs(priorInvocation);
    await Assert.That(ex.Message).Contains("MyReceptor");
    await Assert.That(ex.Message).Contains("PostInboxInline");
    await Assert.That(ex.Message).Contains("PreOutboxInline");
    await Assert.That(ex.Message).Contains(messageId.ToString());
  }

  [Test]
  public async Task RichConstructor_NullPriorInvocation_IsAllowedAsync() {
    var ex = new DuplicateReceptorFireException(
      receptorId: "R",
      currentStage: LifecycleStage.PostInboxDetached,
      priorStage: LifecycleStage.PostInboxInline,
      messageId: Guid.Empty,
      priorInvocation: null);

    await Assert.That(ex.PriorInvocation).IsNull();
  }

  [Test]
  public async Task ParameterlessConstructor_HasDefaultMessageAndEmptyReceptorIdAsync() {
    var ex = new DuplicateReceptorFireException();
    await Assert.That(ex.ReceptorId).IsEqualTo(string.Empty);
    await Assert.That(ex.Message).Contains("second time");
  }

  [Test]
  public async Task MessageConstructor_UsesProvidedMessage_KeepsReceptorIdEmptyAsync() {
    var ex = new DuplicateReceptorFireException("custom message");
    await Assert.That(ex.Message).IsEqualTo("custom message");
    await Assert.That(ex.ReceptorId).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task InnerExceptionConstructor_Wraps_PreservesMessageAsync() {
    var inner = new InvalidOperationException("inner");
    var ex = new DuplicateReceptorFireException("outer", inner);
    await Assert.That(ex.Message).IsEqualTo("outer");
    await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
    await Assert.That(ex.ReceptorId).IsEqualTo(string.Empty);
  }
}
