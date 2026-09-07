using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Tail-of-round coverage for <see cref="OrphanInboxJanitorExtensions"/>: the snapshot walk must
/// skip a registration that IS generic but whose generic type definition is neither
/// <c>IReceptor&lt;TMessage&gt;</c> nor <c>IReceptor&lt;TMessage,TResponse&gt;</c> — distinct from
/// the already-covered non-generic skip.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OrphanInboxJanitorExtensions.cs</code-under-test>
public class OrphanInboxJanitorExtensionsCoverageTests {

  public sealed record FakeCommand : IMessage;

  public sealed class FakeVoidReceptor : IReceptor<FakeCommand> {
    public ValueTask HandleAsync(FakeCommand message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>
  /// A snapshot that mistakenly picked up an unrelated generic service's type argument as a
  /// "handled" message type would let the janitor treat an orphaned inbox row for that type as
  /// safe to keep — the exact miss the snapshot exists to avoid. Registering another generic
  /// interface (here <c>IOptions&lt;T&gt;</c>) alongside a real receptor pins that only the
  /// receptor's message type survives into the snapshot.
  /// </summary>
  [Test]
  public async Task AddOrphanInboxJanitor_SkipsGenericRegistrationsThatAreNotIReceptorAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IOptions<ThrottleRetryOptions>>(Options.Create(new ThrottleRetryOptions()));
    services.AddTransient<IReceptor<FakeCommand>, FakeVoidReceptor>();

    services.AddOrphanInboxJanitor();

    var sp = services.BuildServiceProvider();
    var snapshot = sp.GetRequiredService<HandledReceptorTypeSnapshot>();

    await Assert.That(snapshot.ReceptorMessageTypes.Count).IsEqualTo(1)
      .Because("IOptions<ThrottleRetryOptions> is generic but not an IReceptor<> registration, "
             + "and must not contribute a type to the snapshot");
    await Assert.That(snapshot.ReceptorMessageTypes).Contains(typeof(FakeCommand));
  }
}
