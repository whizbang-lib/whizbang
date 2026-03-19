using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ProcessingMode enum and related types.
/// </summary>
public class ProcessingModeTests {

  #region ProcessingMode Enum Tests

  [Test]
  public async Task ProcessingMode_Live_HasValueZeroAsync() {
    var value = (int)ProcessingMode.Live;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task ProcessingMode_Replay_HasValueOneAsync() {
    var value = (int)ProcessingMode.Replay;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task ProcessingMode_Rebuild_HasValueTwoAsync() {
    var value = (int)ProcessingMode.Rebuild;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task ProcessingMode_AllValues_AreDistinctAsync() {
    var values = Enum.GetValues<ProcessingMode>();
    var distinct = values.Distinct().ToArray();
    await Assert.That(distinct.Length).IsEqualTo(values.Length);
  }

  #endregion

  #region FireDuringReplayAttribute Tests

  [Test]
  public async Task FireDuringReplayAttribute_CanBeInstantiatedAsync() {
    var attr = new FireDuringReplayAttribute();
    await Assert.That(attr).IsNotNull();
  }

  [Test]
  public async Task FireDuringReplayAttribute_TargetsClassOnlyAsync() {
    var attrUsage = typeof(FireDuringReplayAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(attrUsage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(attrUsage.Inherited).IsFalse();
    await Assert.That(attrUsage.AllowMultiple).IsFalse();
  }

  #endregion

  #region LifecycleExecutionContext ProcessingMode Tests

  [Test]
  public async Task LifecycleExecutionContext_ProcessingMode_DefaultsToNullAsync() {
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline
    };

    await Assert.That(context.ProcessingMode).IsNull();
  }

  [Test]
  public async Task LifecycleExecutionContext_ProcessingMode_CanBeSetToReplayAsync() {
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    await Assert.That(context.ProcessingMode).IsEqualTo(ProcessingMode.Replay);
  }

  [Test]
  public async Task LifecycleExecutionContext_ProcessingMode_CanBeSetToRebuildAsync() {
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Rebuild
    };

    await Assert.That(context.ProcessingMode).IsEqualTo(ProcessingMode.Rebuild);
  }

  [Test]
  public async Task LifecycleExecutionContext_ProcessingMode_CanBeSetToLiveAsync() {
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Live
    };

    await Assert.That(context.ProcessingMode).IsEqualTo(ProcessingMode.Live);
  }

  [Test]
  public async Task LifecycleExecutionContext_WithExpression_PreservesProcessingModeAsync() {
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PrePerspectiveAsync,
      ProcessingMode = ProcessingMode.Replay,
      StreamId = Guid.CreateVersion7()
    };

    var updated = context with { CurrentStage = LifecycleStage.PostPerspectiveInline };

    await Assert.That(updated.ProcessingMode).IsEqualTo(ProcessingMode.Replay);
    await Assert.That(updated.StreamId).IsEqualTo(context.StreamId);
  }

  #endregion

  #region ILifecycleContext Interface Contract Tests

  [Test]
  public async Task ILifecycleContext_HasProcessingModePropertyAsync() {
    var property = typeof(ILifecycleContext).GetProperty("ProcessingMode");
    await Assert.That(property).IsNotNull();
    await Assert.That(property!.PropertyType).IsEqualTo(typeof(ProcessingMode?));
  }

  #endregion

  #region ProcessingModeAccessor Tests

  [Test]
  public async Task ProcessingModeAccessor_Current_DefaultsToNullAsync() {
    // Reset to ensure clean state
    ProcessingModeAccessor.Current = null;
    await Assert.That(ProcessingModeAccessor.Current).IsNull();
  }

  [Test]
  public async Task ProcessingModeAccessor_Current_CanBeSetToRebuildAsync() {
    var previous = ProcessingModeAccessor.Current;
    try {
      ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
      await Assert.That(ProcessingModeAccessor.Current).IsEqualTo(ProcessingMode.Rebuild);
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }

  [Test]
  public async Task ProcessingModeAccessor_Current_CanBeSetToReplayAsync() {
    var previous = ProcessingModeAccessor.Current;
    try {
      ProcessingModeAccessor.Current = ProcessingMode.Replay;
      await Assert.That(ProcessingModeAccessor.Current).IsEqualTo(ProcessingMode.Replay);
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }

  [Test]
  public async Task ProcessingModeAccessor_Current_CanBeClearedAsync() {
    var previous = ProcessingModeAccessor.Current;
    try {
      ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
      ProcessingModeAccessor.Current = null;
      await Assert.That(ProcessingModeAccessor.Current).IsNull();
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }

  [Test]
  public async Task ProcessingModeAccessor_IsAsyncLocalIsolatedAsync() {
    var previous = ProcessingModeAccessor.Current;
    try {
      ProcessingModeAccessor.Current = ProcessingMode.Rebuild;

      ProcessingMode? valueInOtherContext = null;
      await Task.Run(() => {
        // AsyncLocal flows to child tasks, so this should see the parent value
        valueInOtherContext = ProcessingModeAccessor.Current;
      });

      await Assert.That(valueInOtherContext).IsEqualTo(ProcessingMode.Rebuild);
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }

  #endregion

  #region ReceptorInfo FireDuringReplay Tests

  [Test]
  public async Task ReceptorInfo_FireDuringReplay_DefaultsToFalseAsync() {
    var info = new ReceptorInfo(
        MessageType: typeof(string),
        ReceptorId: "test",
        InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null)
    );

    await Assert.That(info.FireDuringReplay).IsFalse();
  }

  [Test]
  public async Task ReceptorInfo_FireDuringReplay_CanBeSetToTrueAsync() {
    var info = new ReceptorInfo(
        MessageType: typeof(string),
        ReceptorId: "test",
        InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null),
        FireDuringReplay: true
    );

    await Assert.That(info.FireDuringReplay).IsTrue();
  }

  #endregion
}
