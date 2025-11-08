using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Policies;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for PolicyDecisionTrail implementation.
/// Ensures policy decision tracking and filtering methods are properly tested.
/// </summary>
public class PolicyDecisionTrailTests {
  [Test]
  public async Task GetMatchedRules_ReturnsOnlyMatchedDecisionsAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("Policy1", "Rule1", true, "Config1", "Matched rule 1");
    trail.RecordDecision("Policy2", "Rule2", false, null, "Did not match");
    trail.RecordDecision("Policy3", "Rule3", true, "Config3", "Matched rule 3");
    trail.RecordDecision("Policy4", "Rule4", false, null, "Did not match");

    // Act
    var matchedRules = trail.GetMatchedRules().ToList();

    // Assert
    await Assert.That(matchedRules).HasCount().EqualTo(2);
    await Assert.That(matchedRules[0].PolicyName).IsEqualTo("Policy1");
    await Assert.That(matchedRules[0].Matched).IsTrue();
    await Assert.That(matchedRules[1].PolicyName).IsEqualTo("Policy3");
    await Assert.That(matchedRules[1].Matched).IsTrue();
  }

  [Test]
  public async Task GetMatchedRules_ReturnsEmptyWhenNoMatchesAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("Policy1", "Rule1", false, null, "Did not match");
    trail.RecordDecision("Policy2", "Rule2", false, null, "Did not match");

    // Act
    var matchedRules = trail.GetMatchedRules().ToList();

    // Assert
    await Assert.That(matchedRules).IsEmpty();
  }

  [Test]
  public async Task GetUnmatchedRules_ReturnsOnlyUnmatchedDecisionsAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("Policy1", "Rule1", true, "Config1", "Matched");
    trail.RecordDecision("Policy2", "Rule2", false, null, "Unmatched rule 2");
    trail.RecordDecision("Policy3", "Rule3", true, "Config3", "Matched");
    trail.RecordDecision("Policy4", "Rule4", false, null, "Unmatched rule 4");

    // Act
    var unmatchedRules = trail.GetUnmatchedRules().ToList();

    // Assert
    await Assert.That(unmatchedRules).HasCount().EqualTo(2);
    await Assert.That(unmatchedRules[0].PolicyName).IsEqualTo("Policy2");
    await Assert.That(unmatchedRules[0].Matched).IsFalse();
    await Assert.That(unmatchedRules[1].PolicyName).IsEqualTo("Policy4");
    await Assert.That(unmatchedRules[1].Matched).IsFalse();
  }

  [Test]
  public async Task GetUnmatchedRules_ReturnsEmptyWhenAllMatchedAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("Policy1", "Rule1", true, "Config1", "Matched");
    trail.RecordDecision("Policy2", "Rule2", true, "Config2", "Matched");

    // Act
    var unmatchedRules = trail.GetUnmatchedRules().ToList();

    // Assert
    await Assert.That(unmatchedRules).IsEmpty();
  }

  [Test]
  public async Task RecordDecision_AddsDecisionWithAllPropertiesAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    var before = DateTimeOffset.UtcNow;

    // Act
    trail.RecordDecision("TestPolicy", "TestRule", true, "TestConfig", "Test reason");
    var after = DateTimeOffset.UtcNow;

    // Assert
    await Assert.That(trail.Decisions).HasCount().EqualTo(1);
    var decision = trail.Decisions[0];
    await Assert.That(decision.PolicyName).IsEqualTo("TestPolicy");
    await Assert.That(decision.Rule).IsEqualTo("TestRule");
    await Assert.That(decision.Matched).IsTrue();
    await Assert.That(decision.Configuration).IsEqualTo("TestConfig");
    await Assert.That(decision.Reason).IsEqualTo("Test reason");
    await Assert.That(decision.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(decision.Timestamp).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task Decisions_IsInitializedEmptyByDefaultAsync() {
    // Arrange & Act
    var trail = new PolicyDecisionTrail();

    // Assert
    await Assert.That(trail.Decisions).IsNotNull();
    await Assert.That(trail.Decisions).IsEmpty();
  }

  [Test]
  public async Task GetMatchedRules_PreservesOrderAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("Policy1", "Rule1", true, null, "First");
    await Task.Delay(10); // Ensure different timestamps
    trail.RecordDecision("Policy2", "Rule2", true, null, "Second");
    await Task.Delay(10);
    trail.RecordDecision("Policy3", "Rule3", true, null, "Third");

    // Act
    var matchedRules = trail.GetMatchedRules().ToList();

    // Assert
    await Assert.That(matchedRules).HasCount().EqualTo(3);
    await Assert.That(matchedRules[0].Reason).IsEqualTo("First");
    await Assert.That(matchedRules[1].Reason).IsEqualTo("Second");
    await Assert.That(matchedRules[2].Reason).IsEqualTo("Third");
  }

  [Test]
  public async Task GetUnmatchedRules_PreservesOrderAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("Policy1", "Rule1", false, null, "First unmatched");
    await Task.Delay(10); // Ensure different timestamps
    trail.RecordDecision("Policy2", "Rule2", false, null, "Second unmatched");
    await Task.Delay(10);
    trail.RecordDecision("Policy3", "Rule3", false, null, "Third unmatched");

    // Act
    var unmatchedRules = trail.GetUnmatchedRules().ToList();

    // Assert
    await Assert.That(unmatchedRules).HasCount().EqualTo(3);
    await Assert.That(unmatchedRules[0].Reason).IsEqualTo("First unmatched");
    await Assert.That(unmatchedRules[1].Reason).IsEqualTo("Second unmatched");
    await Assert.That(unmatchedRules[2].Reason).IsEqualTo("Third unmatched");
  }
}
