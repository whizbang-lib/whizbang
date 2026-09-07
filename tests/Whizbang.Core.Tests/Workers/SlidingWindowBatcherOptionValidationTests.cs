using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Constructor validation for <see cref="SlidingWindowBatcher{T}"/>'s window options.
/// </summary>
/// <remarks>
/// Both windows feed straight into <c>Task.Delay</c> arithmetic in the accumulate loop. A negative
/// sliding window makes the computed remaining wait negative on the first pass, so the batcher
/// flushes every single item on its own and the coalescing this class exists for silently stops
/// happening. A non-positive max wait removes the hard cap entirely, so a busy producer can hold a
/// batch open indefinitely. Neither failure raises anything at runtime — the pipeline just gets
/// slower or less timely — so the constructor has to reject them at composition time, where the
/// bad configuration value is still in hand.
/// </remarks>
public class SlidingWindowBatcherOptionValidationTests {

  [Test]
  public async Task Constructor_NegativeSlidingWindow_ThrowsNamingTheOptionsArgumentAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var options = new SlidingWindowBatcherOptions {
      MaxSize = 10,
      SlidingWindow = TimeSpan.FromMilliseconds(-1),
      MaxWait = TimeSpan.FromSeconds(1),
    };

    await Assert.That(() => new SlidingWindowBatcher<int>(channel.Reader, options))
      .ThrowsExactly<ArgumentOutOfRangeException>()
      .Because("a negative quiet period would make the first computed wait negative and flush "
             + "every item alone — the batcher must refuse to be constructed rather than "
             + "silently stop coalescing");
  }

  [Test]
  public async Task Constructor_ZeroMaxWait_ThrowsNamingTheOptionsArgumentAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var options = new SlidingWindowBatcherOptions {
      MaxSize = 10,
      SlidingWindow = TimeSpan.Zero,
      MaxWait = TimeSpan.Zero,
    };

    await Assert.That(() => new SlidingWindowBatcher<int>(channel.Reader, options))
      .ThrowsExactly<ArgumentOutOfRangeException>()
      .Because("MaxWait is the hard cap that bounds latency under a continuously busy producer; "
             + "zero removes the cap rather than tightening it");
  }

  [Test]
  public async Task Constructor_NegativeMaxWait_ThrowsAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var options = new SlidingWindowBatcherOptions {
      MaxSize = 10,
      SlidingWindow = TimeSpan.Zero,
      MaxWait = TimeSpan.FromSeconds(-1),
    };

    await Assert.That(() => new SlidingWindowBatcher<int>(channel.Reader, options))
      .ThrowsExactly<ArgumentOutOfRangeException>()
      .Because("the max-wait guard rejects the whole non-positive range, not just zero");
  }

  [Test]
  public async Task Constructor_SlidingWindowCheckedBeforeMaxWait_AndBothAreDistinctFromTheOrderingRuleAsync() {
    // A negative sliding window is ALSO greater-than nothing and smaller than MaxWait, so it
    // would sail past the "SlidingWindow <= MaxWait" ordering check that follows. Pinning the
    // rejection here proves the negative-window guard is doing the work, not the ordering rule.
    var channel = Channel.CreateUnbounded<int>();
    var negativeWindow = new SlidingWindowBatcherOptions {
      MaxSize = 1,
      SlidingWindow = TimeSpan.FromSeconds(-5),
      MaxWait = TimeSpan.FromSeconds(30),
    };

    await Assert.That(() => new SlidingWindowBatcher<int>(channel.Reader, negativeWindow))
      .ThrowsExactly<ArgumentOutOfRangeException>()
      .Because("a negative window satisfies SlidingWindow <= MaxWait, so only its own guard can "
             + "reject it");

    var ordered = new SlidingWindowBatcherOptions {
      MaxSize = 1,
      SlidingWindow = TimeSpan.FromSeconds(5),
      MaxWait = TimeSpan.FromSeconds(30),
    };
    var batcher = new SlidingWindowBatcher<int>(channel.Reader, ordered);

    await Assert.That(batcher).IsNotNull()
      .Because("the guards must accept a well-ordered pair — otherwise the rejections above prove nothing");
  }
}
