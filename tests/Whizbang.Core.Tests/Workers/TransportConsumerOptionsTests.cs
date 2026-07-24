using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="TransportConsumerOptions"/> — pins defaults
/// + setter behavior. The Destinations collection backs every consumer's
/// per-destination subscription state, so lock the surface here.
/// </summary>
/// <docs>messaging/transports/transport-consumer</docs>
public class TransportConsumerOptionsTests {

  [Test]
  public async Task Destinations_DefaultsToEmptyMutableListAsync() {
    var options = new TransportConsumerOptions();
    await Assert.That(options.Destinations).IsNotNull();
    await Assert.That(options.Destinations.Count).IsEqualTo(0);

    // Mutability — callers add destinations via the property's collection.
    options.Destinations.Add(new TransportDestination("topic-a", "sub-a"));
    await Assert.That(options.Destinations.Count).IsEqualTo(1);
    await Assert.That(options.Destinations[0].Address).IsEqualTo("topic-a");
  }

  [Test]
  public async Task SubscriberName_DefaultsToNullAsync() {
    var options = new TransportConsumerOptions();
    await Assert.That(options.SubscriberName).IsNull();
  }

  [Test]
  public async Task SubscriberName_SetterRoundTripsAsync() {
    var options = new TransportConsumerOptions { SubscriberName = "bff-service" };
    await Assert.That(options.SubscriberName).IsEqualTo("bff-service");
  }

  [Test]
  public async Task MultipleDestinations_PreserveOrderAsync() {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("first", "s1"));
    options.Destinations.Add(new TransportDestination("second", "s2"));
    options.Destinations.Add(new TransportDestination("third", "s3"));

    await Assert.That(options.Destinations.Count).IsEqualTo(3);
    await Assert.That(options.Destinations[0].Address).IsEqualTo("first");
    await Assert.That(options.Destinations[1].Address).IsEqualTo("second");
    await Assert.That(options.Destinations[2].Address).IsEqualTo("third");
  }
}
