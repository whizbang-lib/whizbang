using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What the transport consumer calls itself when no <see cref="IServiceInstanceProvider"/> is in
/// the container.
/// </summary>
/// <remarks>
/// <para>The service name is not cosmetic: it becomes the <c>SubscriberName</c> stamped on every
/// inbox destination, which is what gives competing consumers a stable, per-service broker queue.
/// Two services that resolve to the same name share a queue and steal each other's messages; a
/// blank name is rejected outright by <c>TransportSubscriptionBuilder</c>, so an unwired
/// composition would fail at container build rather than at a diagnosable point.</para>
/// <para><c>AddTransportConsumer</c> now self-provisions the identity, so the fallback only runs
/// for a container that lost it — a composition that removed or replaced the registration, or a
/// call site that hands the same resolver to a container built elsewhere. Removing the
/// registration is how the test reaches it without reaching into the private helper.</para>
/// </remarks>
public class TransportConsumerServiceNameFallbackTests {

  private sealed class NamedInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public string ServiceName { get; } = serviceName;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string HostName => "test-host";
    public int ProcessId => Environment.ProcessId;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>The name the fallback is expected to produce in any managed host.</summary>
  private static string? _entryAssemblyName =>
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;

  /// <summary>
  /// Composes a transport consumer and resolves its options. <paramref name="providerName"/>
  /// null means the container ends up WITHOUT an <see cref="IServiceInstanceProvider"/> — the
  /// only condition under which the assembly-name fallback runs.
  /// </summary>
  private static TransportConsumerOptions _resolveOptions(string? providerName) {
    var services = new ServiceCollection();
    services.AddLogging();

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => routing.OwnDomains("sampleapp.orders.commands"));
    builder.AddTransportConsumer();

    // AddTransportConsumer self-provisions the identity, so both cases start by removing it.
    services.RemoveAll<IServiceInstanceProvider>();
    if (providerName is not null) {
      services.AddSingleton<IServiceInstanceProvider>(new NamedInstanceProvider(providerName));
    }

    var sp = services.BuildServiceProvider();
    return sp.GetRequiredService<TransportConsumerOptions>();
  }

  private static List<string> _subscriberNames(TransportConsumerOptions options) =>
    [.. options.Destinations
      .Where(d => d.Metadata is not null && d.Metadata.ContainsKey("SubscriberName"))
      .Select(d => d.Metadata!["SubscriberName"].GetString() ?? string.Empty)];

  [Test]
  public async Task Destinations_WithNoInstanceProvider_AreStampedWithTheEntryAssemblyNameAsync() {
    var expected = _entryAssemblyName;
    await Assert.That(expected).IsNotNull()
      .Because("the premise of the fallback under test is that a managed host HAS an entry assembly");

    var names = _subscriberNames(_resolveOptions(providerName: null)).Distinct().ToList();

    await Assert.That(names).IsNotEmpty()
      .Because("the resolution must actually have produced stamped destinations — with none, the "
             + "assertion below would hold vacuously");
    await Assert.That(names.Count).IsEqualTo(1)
      .Because("every destination must carry the same subscriber name; two different names would "
             + "split one service across two broker queues");
    await Assert.That(names[0]).IsEqualTo(expected!)
      .Because("with no instance provider the subscriber name must fall back to the entry "
             + "assembly, which is stable per deployed process — the alternative is a blank name "
             + "that TransportSubscriptionBuilder rejects outright");
  }

  [Test]
  public async Task Destinations_WithNoInstanceProvider_DoNotUseThePlaceholderNameAsync() {
    var names = _subscriberNames(_resolveOptions(providerName: null));

    await Assert.That(names).IsNotEmpty()
      .Because("an empty destination list would make the exclusion below meaningless");
    await Assert.That(names.Contains("UnknownService")).IsFalse()
      .Because("two different services both stamped \"UnknownService\" would collapse onto one "
             + "broker queue and steal each other's messages — the placeholder is the last "
             + "resort, not the fallback");
  }

  [Test]
  public async Task Destinations_WithAnInstanceProvider_UseItsNameNotTheAssemblyAsync() {
    // The control: with an identity registered, the provider wins. Without this, the fallback
    // test above could be passing because BOTH paths happen to yield the same string.
    var configured = "orders-consumer-" + Guid.NewGuid().ToString("N")[..8];
    var names = _subscriberNames(_resolveOptions(providerName: configured)).Distinct().ToList();

    await Assert.That(names.Count).IsEqualTo(1)
      .Because("one registered identity must yield one subscriber name");
    await Assert.That(names[0]).IsEqualTo(configured)
      .Because("a registered instance provider is the authority on the service name; falling back "
             + "past it would rename every queue on a host that configured one");
    await Assert.That(names[0]).IsNotEqualTo(_entryAssemblyName)
      .Because("the two paths must yield different names, or neither test distinguishes them");
  }
}
