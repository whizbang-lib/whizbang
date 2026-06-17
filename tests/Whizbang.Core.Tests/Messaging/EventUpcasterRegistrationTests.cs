using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for the AOT-safe upcaster registration API (explicit, no assembly scanning):
/// <c>AddEventUpcaster&lt;T&gt;()</c> registers an <see cref="IEventUpcaster"/> and makes the
/// <see cref="EventUpcasterPipeline"/> resolvable with the upcasters in registration order.
/// </summary>
public class EventUpcasterRegistrationTests {
  [Test]
  public async Task AddEventUpcaster_RegistersUpcasterAndPipeline_InOrderAsync() {
    var services = new ServiceCollection();

    var returned = services
      .AddEventUpcaster<FirstUpcaster>()
      .AddEventUpcaster<SecondUpcaster>();

    await Assert.That(returned).IsSameReferenceAs(services); // fluent

    using var sp = services.BuildServiceProvider();

    var upcasters = sp.GetServices<IEventUpcaster>().ToList();
    await Assert.That(upcasters.Count).IsEqualTo(2);
    await Assert.That(upcasters[0]).IsTypeOf<FirstUpcaster>();
    await Assert.That(upcasters[1]).IsTypeOf<SecondUpcaster>();

    var pipeline = sp.GetRequiredService<EventUpcasterPipeline>();
    await Assert.That(pipeline.HasAny).IsTrue();

    // Registration order composes V1 -> V2 -> V3.
    var result = pipeline.Apply(new V1 { StreamId = Guid.NewGuid() });
    await Assert.That(result).IsTypeOf<V3>();
  }

  [Test]
  public async Task AddEventUpcaster_RegistersAsSingletonAsync() {
    var services = new ServiceCollection();
    services.AddEventUpcaster<FirstUpcaster>();
    using var sp = services.BuildServiceProvider();

    var a = sp.GetServices<IEventUpcaster>().Single();
    var b = sp.GetServices<IEventUpcaster>().Single();

    await Assert.That(a).IsSameReferenceAs(b); // singleton — upcasters are pure/stateless
  }

#pragma warning disable WHIZ009
  public record V1 : IEvent { [StreamId] public Guid StreamId { get; set; } }
  public record V2 : IEvent { [StreamId] public Guid StreamId { get; set; } }
  public record V3 : IEvent { [StreamId] public Guid StreamId { get; set; } }
#pragma warning restore WHIZ009

  private sealed class FirstUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is V1;
    public IEvent Upcast(IEvent storedEvent) => new V2 { StreamId = ((V1)storedEvent).StreamId };
  }

  private sealed class SecondUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is V2;
    public IEvent Upcast(IEvent storedEvent) => new V3 { StreamId = ((V2)storedEvent).StreamId };
  }
}
