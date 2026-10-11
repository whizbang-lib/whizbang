using Exp;

namespace ShapesRunner;

// Each class runs as its own test process. A takes every ?. statement's non-null outcome and B its null
// one, except where a shape needs otherwise: Unsplit and Continued are null in both, Throwing throws in
// both, and the lookup in Sweeper finds nothing in both.
public class A {
  [Test]
  public async Task RunAAsync() {
    var shapes = new Shapes(new Sink());
    await Assert.That(shapes.Split(1, false)).IsEqualTo(1);
    await Assert.That(await shapes.SplitAsync(new Sink(), 1, false)).IsEqualTo(1);
    await Assert.That(shapes.Unsplit(1, false)).IsEqualTo(1);
    await Assert.That(Shapes.Guarded(new Sink(), false)).IsEqualTo(1);
    await Assert.That(() => Shapes.Throwing(new Bomb(), false)).Throws<InvalidOperationException>();
    await Assert.That(Shapes.Continued(() => null, 1)).IsEqualTo(1);
    await Assert.That(Shapes.Nested(new Sink(), 1)).IsEqualTo(0);
    await Assert.That(Shapes.Chain([new Sink()])).IsEqualTo(0);
    await Assert.That(await Sweeper.SweepAsync(new NoMeters(), new Sink(), [1, 2], false)).IsEqualTo(3L);
  }
}

public class B {
  [Test]
  public async Task RunBAsync() {
    var shapes = new Shapes(null);
    await Assert.That(shapes.Split(1, false)).IsEqualTo(1);
    await Assert.That(await shapes.SplitAsync(null, 1, false)).IsEqualTo(1);
    await Assert.That(shapes.Unsplit(1, false)).IsEqualTo(1);
    await Assert.That(Shapes.Guarded(new Sink(), false)).IsEqualTo(1);
    await Assert.That(() => Shapes.Throwing(new Bomb(), false)).Throws<InvalidOperationException>();
    await Assert.That(Shapes.Continued(() => null, 1)).IsEqualTo(1);
    await Assert.That(Shapes.Nested(null, 1)).IsEqualTo(1);
    await Assert.That(Shapes.Chain([null])).IsEqualTo(0);
    await Assert.That(await Sweeper.SweepAsync(new NoMeters(), null, [1, 2], false)).IsEqualTo(3L);
  }
}

internal sealed class NoMeters : IMeterLookup {
  public T? Find<T>() where T : class => null;
}
