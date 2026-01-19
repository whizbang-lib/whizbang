namespace Whizbang.Data.Schema.Tests;

public class SequenceDefinitionTests {
  [Test]
  public async Task SequenceDefinition_WithRequiredProperties_CreatesInstanceAsync() {
    // Arrange & Act
    var sequence = new SequenceDefinition(
      Name: "event_sequence"
    );

    // Assert
    await Assert.That(sequence.Name).IsEqualTo("event_sequence");
    await Assert.That(sequence.StartValue).IsEqualTo(1);
    await Assert.That(sequence.IncrementBy).IsEqualTo(1);
  }

  [Test]
  public async Task SequenceDefinition_WithCustomStartValue_SetsPropertyAsync() {
    // Arrange & Act
    var sequence = new SequenceDefinition(
      Name: "order_sequence",
      StartValue: 1000
    );

    // Assert
    await Assert.That(sequence.StartValue).IsEqualTo(1000);
  }

  [Test]
  public async Task SequenceDefinition_WithCustomIncrementBy_SetsPropertyAsync() {
    // Arrange & Act
    var sequence = new SequenceDefinition(
      Name: "batch_sequence",
      IncrementBy: 10
    );

    // Assert
    await Assert.That(sequence.IncrementBy).IsEqualTo(10);
  }

  [Test]
  public async Task SequenceDefinition_WithAllCustomProperties_SetsAllAsync() {
    // Arrange & Act
    var sequence = new SequenceDefinition(
      Name: "custom_sequence",
      StartValue: 5000,
      IncrementBy: 5
    );

    // Assert
    await Assert.That(sequence.Name).IsEqualTo("custom_sequence");
    await Assert.That(sequence.StartValue).IsEqualTo(5000);
    await Assert.That(sequence.IncrementBy).IsEqualTo(5);
  }

  [Test]
  public async Task SequenceDefinition_SameValues_AreEqualAsync() {
    // Arrange
    var sequence1 = new SequenceDefinition("seq1", 100, 2);
    var sequence2 = new SequenceDefinition("seq1", 100, 2);

    // Assert - record equality
    await Assert.That(sequence1).IsEqualTo(sequence2);
    await Assert.That(sequence1.GetHashCode()).IsEqualTo(sequence2.GetHashCode());
  }

  [Test]
  public async Task SequenceDefinition_DifferentName_AreNotEqualAsync() {
    // Arrange
    var sequence1 = new SequenceDefinition("seq1");
    var sequence2 = new SequenceDefinition("seq2");

    // Assert
    await Assert.That(sequence1).IsNotEqualTo(sequence2);
  }

  [Test]
  public async Task SequenceDefinition_DifferentStartValue_AreNotEqualAsync() {
    // Arrange
    var sequence1 = new SequenceDefinition("seq", StartValue: 1);
    var sequence2 = new SequenceDefinition("seq", StartValue: 100);

    // Assert
    await Assert.That(sequence1).IsNotEqualTo(sequence2);
  }

  [Test]
  public async Task SequenceDefinition_DifferentIncrementBy_AreNotEqualAsync() {
    // Arrange
    var sequence1 = new SequenceDefinition("seq", IncrementBy: 1);
    var sequence2 = new SequenceDefinition("seq", IncrementBy: 10);

    // Assert
    await Assert.That(sequence1).IsNotEqualTo(sequence2);
  }

  [Test]
  public async Task SequenceDefinition_IsRecordAsync() {
    // Arrange & Act
    var sequence = new SequenceDefinition("test_sequence");

    // Assert - records support with-expressions
    var modified = sequence with { StartValue = 1000 };
    await Assert.That(modified.Name).IsEqualTo("test_sequence");
    await Assert.That(modified.StartValue).IsEqualTo(1000);
    await Assert.That(sequence.StartValue).IsEqualTo(1); // Original unchanged
  }
}
