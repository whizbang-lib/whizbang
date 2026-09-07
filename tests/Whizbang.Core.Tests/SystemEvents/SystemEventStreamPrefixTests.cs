using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Pins <see cref="SystemEventStreams.Prefix"/> against the stream name it is supposed to select.
/// </summary>
/// <remarks>
/// The prefix is not decoration: it is what a subscription or a filter uses to say "system streams"
/// without enumerating them, and the <c>$</c> lead is what keeps a system stream out of the domain
/// namespace. If the prefix and the name ever drifted apart, every prefix-based filter would
/// quietly stop matching the one stream all system events are written to — a miss that produces no
/// error, just an empty result set.
/// </remarks>
public class SystemEventStreamPrefixTests {

  [Test]
  public async Task Prefix_SelectsTheSystemStreamNameAsync() {
    await Assert.That(SystemEventStreams.Name.StartsWith(SystemEventStreams.Prefix, StringComparison.Ordinal)).IsTrue()
      .Because("a prefix-based subscription must match the dedicated system stream, or every "
             + "system-event filter silently returns nothing");
  }

  [Test]
  public async Task Prefix_IsShorterThanTheNameSoItCanMatchMoreThanOneStreamAsync() {
    await Assert.That(SystemEventStreams.Prefix.Length).IsLessThan(SystemEventStreams.Name.Length)
      .Because("a prefix equal to the full name would only ever match that one stream, which "
             + "defeats the point of exposing a prefix separately from the name");
  }

  [Test]
  public async Task Prefix_KeepsTheSystemNamespaceMarkerAsync() {
    await Assert.That(SystemEventStreams.Prefix.StartsWith('$')).IsTrue()
      .Because("the leading $ is the convention that separates system streams from domain streams; "
             + "dropping it from the prefix would let a domain stream name match the system filter");
  }

  [Test]
  public async Task Prefix_DoesNotMatchAnOrdinaryDomainStreamNameAsync() {
    const string domainStream = "orders-1f2c";

    await Assert.That(domainStream.StartsWith(SystemEventStreams.Prefix, StringComparison.Ordinal)).IsFalse()
      .Because("the prefix has to be selective — one that matched ordinary stream names would pull "
             + "domain events into every system-event query");
  }
}
