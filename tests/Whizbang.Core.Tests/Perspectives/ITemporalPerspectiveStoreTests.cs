using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for ITemporalPerspectiveStore interface definition.
/// Unlike IPerspectiveStore which does UPSERT, this store only INSERTs (append-only).
/// </summary>
[Category("TemporalPerspectives")]
public class ITemporalPerspectiveStoreTests {
  [Test]
  public async Task ITemporalPerspectiveStore_HasAppendAsyncMethodAsync() {
    // Assert - AppendAsync creates new rows, never updates
    var method = typeof(ITemporalPerspectiveStore<TestTemporalModel>).GetMethod("AppendAsync");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(5); // streamId, eventId, model, validTime, cancellationToken

    await Assert.That(parameters[0].Name).IsEqualTo("streamId");
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(Guid));

    await Assert.That(parameters[1].Name).IsEqualTo("eventId");
    await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(Guid));

    await Assert.That(parameters[2].Name).IsEqualTo("model");
    await Assert.That(parameters[2].ParameterType).IsEqualTo(typeof(TestTemporalModel));

    await Assert.That(parameters[3].Name).IsEqualTo("validTime");
    await Assert.That(parameters[3].ParameterType).IsEqualTo(typeof(DateTimeOffset));
  }

  [Test]
  public async Task ITemporalPerspectiveStore_AppendAsync_HasCancellationTokenWithDefaultAsync() {
    // Assert - CancellationToken should have default value
    var method = typeof(ITemporalPerspectiveStore<TestTemporalModel>).GetMethod("AppendAsync");
    var parameters = method!.GetParameters();

    await Assert.That(parameters[4].Name).IsEqualTo("cancellationToken");
    await Assert.That(parameters[4].HasDefaultValue).IsTrue();
  }

  [Test]
  public async Task ITemporalPerspectiveStore_AppendAsync_ReturnsTaskAsync() {
    // Assert - AppendAsync returns Task (not Task<T>)
    var method = typeof(ITemporalPerspectiveStore<TestTemporalModel>).GetMethod("AppendAsync");
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
  }

  [Test]
  public async Task ITemporalPerspectiveStore_HasFlushAsyncMethodAsync() {
    // Assert - FlushAsync commits pending changes
    var method = typeof(ITemporalPerspectiveStore<TestTemporalModel>).GetMethod("FlushAsync");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
  }

  [Test]
  public async Task ITemporalPerspectiveStore_FlushAsync_HasCancellationTokenWithDefaultAsync() {
    // Assert - FlushAsync CancellationToken should have default value
    var method = typeof(ITemporalPerspectiveStore<TestTemporalModel>).GetMethod("FlushAsync");
    var parameters = method!.GetParameters();

    await Assert.That(parameters.Length).IsEqualTo(1);
    await Assert.That(parameters[0].Name).IsEqualTo("cancellationToken");
    await Assert.That(parameters[0].HasDefaultValue).IsTrue();
  }

  [Test]
  public async Task ITemporalPerspectiveStore_IsGenericWithModelConstraintAsync() {
    // Assert - ITemporalPerspectiveStore<TModel> where TModel : class
    var type = typeof(ITemporalPerspectiveStore<>);
    await Assert.That(type.IsInterface).IsTrue();
    await Assert.That(type.IsGenericTypeDefinition).IsTrue();

    var typeParam = type.GetGenericArguments()[0];
    _ = typeParam.GetGenericParameterConstraints();
    // The "class" constraint is expressed as a reference type constraint
    await Assert.That((typeParam.GenericParameterAttributes &
        System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint) != 0).IsTrue();
  }
}

// Test model for ITemporalPerspectiveStore tests
internal sealed record TestTemporalModel {
  public required string Activity { get; init; }
}
