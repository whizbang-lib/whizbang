using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

public class OrphanInboxJanitorExtensionsTests {

  public sealed record FakeCommand : IMessage;
  public sealed record FakeEvent : IMessage;
  public sealed record FakeQuery : IMessage;
  public sealed record FakeResult : IMessage;

  public sealed class FakeVoidReceptor : IReceptor<FakeCommand> {
    public ValueTask HandleAsync(FakeCommand message, System.Threading.CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  public sealed class FakeResultReceptor : IReceptor<FakeQuery, FakeResult> {
    public ValueTask<FakeResult> HandleAsync(FakeQuery message, System.Threading.CancellationToken cancellationToken = default) => ValueTask.FromResult(new FakeResult());
  }

  [Test]
  public async Task AddOrphanInboxJanitor_SnapshotsReceptorMessageTypes_FromServiceCollectionAsync() {
    var services = new ServiceCollection();
    services.AddTransient<IReceptor<FakeCommand>, FakeVoidReceptor>();
    services.AddTransient<IReceptor<FakeQuery, FakeResult>, FakeResultReceptor>();

    services.AddOrphanInboxJanitor();

    var sp = services.BuildServiceProvider();
    var snapshot = sp.GetRequiredService<HandledReceptorTypeSnapshot>();

    await Assert.That(snapshot.ReceptorMessageTypes).Contains(typeof(FakeCommand));
    await Assert.That(snapshot.ReceptorMessageTypes).Contains(typeof(FakeQuery));
    await Assert.That(snapshot.ReceptorMessageTypes.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AddOrphanInboxJanitor_SkipsNonReceptorRegistrationsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IRawReceptor, FakeRawReceptor>();  // not IReceptor<T>
    services.AddSingleton<IRawReceptorRegistry>(_ => new RawReceptorRegistry([new FakeRawReceptor()]));
    services.AddTransient<IReceptor<FakeCommand>, FakeVoidReceptor>();

    services.AddOrphanInboxJanitor();

    var sp = services.BuildServiceProvider();
    var snapshot = sp.GetRequiredService<HandledReceptorTypeSnapshot>();

    // Only IReceptor<> registrations end up in the snapshot.
    await Assert.That(snapshot.ReceptorMessageTypes.Count).IsEqualTo(1);
    await Assert.That(snapshot.ReceptorMessageTypes).Contains(typeof(FakeCommand));
  }

  [Test]
  public async Task AddOrphanInboxJanitor_RegistersBackgroundService_ForHostedExecutionAsync() {
    var services = new ServiceCollection();
    services.AddOrphanInboxJanitor();

    var sp = services.BuildServiceProvider();
    var hosted = sp.GetServices<IHostedService>().OfType<OrphanInboxJanitor>().ToArray();

    await Assert.That(hosted.Length).IsEqualTo(1);
  }

  private sealed class FakeRawReceptor : IRawReceptor {
    public string TargetMessageTypeName => "Fake.Type, FakeAssembly";
    public Task HandleAsync(System.Text.Json.JsonElement payload, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
  }
}
