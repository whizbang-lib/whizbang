using System.Threading.Channels;
using Exp;

namespace AsyncShapesRunner;

// Each class runs as its own test process. Both take every loop both ways (except UntilThrow's, which
// only ever throws out) and enter the awaiting catch of Unentered; A completes Guarded and B fails it.
public class A {
  [Test]
  public async Task RunAAsync() {
    await Assert.That(await AsyncShapes.DrainAsync(Completed(1, 2), false)).IsEqualTo(3);
    await Assert.That(await AsyncShapes.CountAsync(new Rows(2), false)).IsEqualTo(2);
    await Assert.That(async () => await AsyncShapes.UntilThrowAsync(new Rows(1))).Throws<InvalidOperationException>();
    await Assert.That(await AsyncShapes.GuardedAsync(new Rows(1), () => Task.CompletedTask)).IsEqualTo(1);
    await Assert.That(await AsyncShapes.UnenteredAsync(() => throw new InvalidOperationException(), () => Task.CompletedTask)).IsEqualTo(0);
  }

  internal static ChannelReader<int> Completed(params int[] items) {
    var channel = Channel.CreateUnbounded<int>();
    foreach (var item in items) {
      channel.Writer.TryWrite(item);
    }
    channel.Writer.Complete();
    return channel.Reader;
  }
}

public class B {
  [Test]
  public async Task RunBAsync() {
    await Assert.That(await AsyncShapes.DrainAsync(A.Completed(4), false)).IsEqualTo(4);
    await Assert.That(await AsyncShapes.CountAsync(new Rows(1), false)).IsEqualTo(1);
    await Assert.That(async () => await AsyncShapes.UntilThrowAsync(new Rows(1))).Throws<InvalidOperationException>();
    await Assert.That(async () => await AsyncShapes.GuardedAsync(new Rows(-1), () => Task.CompletedTask)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(await AsyncShapes.UnenteredAsync(() => throw new InvalidOperationException(), () => Task.CompletedTask)).IsEqualTo(0);
  }
}
