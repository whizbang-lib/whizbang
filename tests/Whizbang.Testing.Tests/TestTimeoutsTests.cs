namespace Whizbang.Testing.Tests;

/// <summary>
/// Locks <see cref="TestTimeouts"/>'s scaling contract. Both overloads read the same
/// <see cref="TestTimeouts.Multiplier"/>, so they must produce the same scaled duration for the
/// same input: a suite that mixes the two would otherwise get two different CI budgets for what
/// the author wrote as one timeout.
/// </summary>
public class TestTimeoutsTests {

  [Test]
  public async Task Scale_TimeSpan_AppliesTheSameMultiplierAsTheMillisecondOverloadAsync() {
    var scaled = TestTimeouts.Scale(TimeSpan.FromMilliseconds(250));

    await Assert.That(scaled).IsEqualTo(TimeSpan.FromMilliseconds(250 * TestTimeouts.Multiplier));
    await Assert.That((int)scaled.TotalMilliseconds).IsEqualTo(TestTimeouts.Scale(250));
  }

  [Test]
  public async Task Scale_TimeSpan_IsProportionalAndKeepsZeroAtZeroAsync() {
    // Scaling must be a multiplication, not an offset: doubling the input doubles the budget,
    // and a zero timeout stays zero however large the CI multiplier is.
    await Assert.That(TestTimeouts.Scale(TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
    await Assert.That(TestTimeouts.Scale(TimeSpan.FromSeconds(4)))
      .IsEqualTo(TestTimeouts.Scale(TimeSpan.FromSeconds(2)) * 2);
  }
}
