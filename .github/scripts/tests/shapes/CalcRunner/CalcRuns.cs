using Exp;

namespace CalcRunner;

// Each class runs as its own test process: A takes each if's true outcome, B its false one.
public class A {
  [Test]
  public async Task RunAAsync() {
    await Assert.That(Calc.Sign(1)).IsEqualTo("pos");
    await Assert.That(Calc.Pick(1)).IsEqualTo(1);
  }
}

public class B {
  [Test]
  public async Task RunBAsync() {
    await Assert.That(Calc.Sign(-1)).IsEqualTo("nonpos");
    await Assert.That(Calc.Pick(-1)).IsEqualTo(3);
  }
}
