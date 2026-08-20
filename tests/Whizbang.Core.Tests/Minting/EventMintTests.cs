using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// Tests for <see cref="IEventMint"/> — the pure-aggregation facade over the minted event
/// families (<c>mint.Composites</c> / <c>mint.Collective</c> / <c>mint.Checkpoints</c>) and its
/// turnkey registration inside <c>AddWhizbang()</c>. The facade is the single test seam a
/// producer mocks; the per-family interfaces stay focused.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Minting/EventMint.cs</code-under-test>
public class EventMintTests {
  [Test]
  public async Task EventMint_ExposesTheConstructedFamilies_UnchangedAsync() {
    var composites = new CompositeFactory();
    var collective = new CollectiveMint();
    var checkpoints = new CheckpointMint();

    var mint = new EventMint(composites, collective, checkpoints);

    await Assert.That(mint.Composites).IsSameReferenceAs(composites)
      .Because("the facade is pure aggregation — no wrapping, no indirection");
    await Assert.That(mint.Collective).IsSameReferenceAs(collective);
    await Assert.That(mint.Checkpoints).IsSameReferenceAs(checkpoints);
  }

  [Test]
  public async Task AddWhizbang_RegistersTheMintTurnkeyAsync() {
    // Turnkey directive: the mint rides the core AddWhizbang pipeline (never a per-assembly
    // generated registration, which multi-assembly hosts can strip — the silent
    // worker-never-starts signature).
    var services = new ServiceCollection();

    services.AddWhizbang();
    using var provider = services.BuildServiceProvider();

    var mint = provider.GetService<IEventMint>();
    await Assert.That(mint is not null).IsTrue()
      .Because("IEventMint is a turnkey framework service — producers inject it without wiring");
    await Assert.That(mint!.Composites is not null).IsTrue();
    await Assert.That(mint.Collective is not null).IsTrue();
    await Assert.That(mint.Checkpoints is not null).IsTrue();
    var factory = provider.GetService<ICompositeFactory>();
    await Assert.That(factory is not null).IsTrue()
      .Because("the composite factory is independently resolvable — workers take the focused "
             + "interface, not the whole facade");
    await Assert.That(factory).IsSameReferenceAs(mint.Composites);
  }

  [Test]
  public async Task AddWhizbang_CalledTwice_MintRegistrationsStayIdempotentAsync() {
    var services = new ServiceCollection();

    services.AddWhizbang();
    services.AddWhizbang();

    await Assert.That(services.Count(d => d.ServiceType == typeof(IEventMint))).IsEqualTo(1)
      .Because("TryAdd keeps repeat AddWhizbang() calls idempotent");
    await Assert.That(services.Count(d => d.ServiceType == typeof(ICompositeFactory))).IsEqualTo(1);
  }
}
