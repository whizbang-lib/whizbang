using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.Mutations.Tests.Unit;

/// <summary>
/// Tests for <see cref="IMutationContext"/> and <see cref="MutationContext"/>.
/// Verifies context properties and behavior.
/// </summary>
public class MutationContextTests {
  [Test]
  public async Task Constructor_ShouldSetCancellationTokenAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var ct = cts.Token;

    // Act
    var context = new MutationContext(ct);

    // Assert
    await Assert.That(context.CancellationToken).IsEqualTo(ct);
  }

  [Test]
  public async Task Constructor_WithDefaultToken_ShouldUseNoneAsync() {
    // Arrange & Act
    var context = new MutationContext(CancellationToken.None);

    // Assert
    await Assert.That(context.CancellationToken).IsEqualTo(CancellationToken.None);
  }

  [Test]
  public async Task Items_ShouldBeEmptyByDefaultAsync() {
    // Arrange & Act
    var context = new MutationContext(CancellationToken.None);

    // Assert
    await Assert.That(context.Items).Count().IsEqualTo(0);
  }

  [Test]
  public async Task Items_ShouldAllowAddingValuesAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);

    // Act
    context.Items["key1"] = "value1";
    context.Items["key2"] = 42;

    // Assert
    await Assert.That(context.Items).Count().IsEqualTo(2);
    await Assert.That(context.Items["key1"]).IsEqualTo("value1");
    await Assert.That(context.Items["key2"]).IsEqualTo(42);
  }

  [Test]
  public async Task Items_ShouldAllowOverwritingValuesAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);
    context.Items["key"] = "initial";

    // Act
    context.Items["key"] = "updated";

    // Assert
    await Assert.That(context.Items["key"]).IsEqualTo("updated");
  }

  [Test]
  public async Task Items_ShouldAllowNullValuesAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);

    // Act
    context.Items["nullable"] = null;

    // Assert
    await Assert.That(context.Items.ContainsKey("nullable")).IsTrue();
    await Assert.That(context.Items["nullable"]).IsNull();
  }

  [Test]
  public async Task Context_ShouldImplementIMutationContextAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);

    // Assert
    await Assert.That(context).IsAssignableTo<IMutationContext>();
  }

  [Test]
  public async Task TryGetItem_ShouldReturnTrueForExistingKeyAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);
    context.Items["existing"] = "value";

    // Act
    var exists = context.Items.TryGetValue("existing", out var value);

    // Assert
    await Assert.That(exists).IsTrue();
    await Assert.That(value).IsEqualTo("value");
  }

  [Test]
  public async Task TryGetItem_ShouldReturnFalseForMissingKeyAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);

    // Act
    var exists = context.Items.TryGetValue("missing", out var value);

    // Assert
    await Assert.That(exists).IsFalse();
    await Assert.That(value).IsNull();
  }

  [Test]
  public async Task Items_ShouldBeIndependentBetweenContextsAsync() {
    // Arrange
    var context1 = new MutationContext(CancellationToken.None);
    var context2 = new MutationContext(CancellationToken.None);

    // Act
    context1.Items["key"] = "context1-value";
    context2.Items["key"] = "context2-value";

    // Assert - each context has its own items
    await Assert.That(context1.Items["key"]).IsEqualTo("context1-value");
    await Assert.That(context2.Items["key"]).IsEqualTo("context2-value");
  }

  [Test]
  public async Task CancellationToken_ShouldReflectSourceTokenStateAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var context = new MutationContext(cts.Token);

    // Act
    await cts.CancelAsync();

    // Assert
    await Assert.That(context.CancellationToken.IsCancellationRequested).IsTrue();
  }

  [Test]
  public async Task Items_ShouldAllowComplexObjectsAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);
    var complexObject = new ComplexItem { Id = 1, Name = "Test" };

    // Act
    context.Items["complex"] = complexObject;

    // Assert
    var retrieved = context.Items["complex"] as ComplexItem;
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.Id).IsEqualTo(1);
    await Assert.That(retrieved.Name).IsEqualTo("Test");
  }

  [Test]
  public async Task Items_Remove_ShouldRemoveKeyAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);
    context.Items["toRemove"] = "value";

    // Act
    var removed = context.Items.Remove("toRemove");

    // Assert
    await Assert.That(removed).IsTrue();
    await Assert.That(context.Items.ContainsKey("toRemove")).IsFalse();
  }

  [Test]
  public async Task Items_Clear_ShouldRemoveAllItemsAsync() {
    // Arrange
    var context = new MutationContext(CancellationToken.None);
    context.Items["key1"] = "value1";
    context.Items["key2"] = "value2";

    // Act
    context.Items.Clear();

    // Assert
    await Assert.That(context.Items).Count().IsEqualTo(0);
  }
}

public class ComplexItem {
  public int Id { get; set; }
  public string? Name { get; set; }
}
