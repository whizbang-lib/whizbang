using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Minting;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// Tests for <c>mint.Checkpoints</c> — the phase-4 placeholder family's first real implementation
/// (topology arc phase 9). The mint is where a control message acquires its broker lifetime, so
/// that the derivation lives in ONE place instead of being recomputed at every control-plane
/// publish site: a supersedable message that outlives its successor is exactly the durable
/// control backlog this class exists to make structurally impossible.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Minting/CheckpointMint.cs</code-under-test>
[Category("Core")]
[Category("Minting")]
public class CheckpointMintTests {
  private sealed record ProbeControlSignal(string Detail);

  private static CheckpointMint _mint(ControlClassOptions? options = null) =>
    new(Options.Create(options ?? new ControlClassOptions()));

  [Test]
  public async Task Mint_DerivesTimeToLiveFromTheCadenceAsync() {
    var mint = _mint();

    var minted = mint.Mint(new ControlMintRequest<ProbeControlSignal> {
      Payload = new ProbeControlSignal("checkpoint"),
      Cadence = TimeSpan.FromSeconds(60),
    });

    await Assert.That(minted.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(120))
      .Because("TTL ≈ 2× cadence — a superseded copy expires before its successor's successor");
  }

  [Test]
  public async Task Mint_CarriesThePayloadUnchangedAsync() {
    var mint = _mint();
    var payload = new ProbeControlSignal("checkpoint");

    var minted = mint.Mint(new ControlMintRequest<ProbeControlSignal> {
      Payload = payload,
      Cadence = TimeSpan.FromSeconds(60),
    });

    await Assert.That(minted.Payload).IsSameReferenceAs(payload)
      .Because("the mint stamps a lifetime; it never rewrites the message");
  }

  [Test]
  public async Task Mint_RequestOverrideBeatsTheOptionsDerivationAsync() {
    var mint = _mint();

    var minted = mint.Mint(new ControlMintRequest<ProbeControlSignal> {
      Payload = new ProbeControlSignal("checkpoint"),
      Cadence = TimeSpan.FromSeconds(60),
      TimeToLive = TimeSpan.FromSeconds(9),
    });

    await Assert.That(minted.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(9));
  }

  [Test]
  public async Task Mint_OptionsOverrideBeatsTheCadenceAsync() {
    var mint = _mint(new ControlClassOptions { TimeToLive = TimeSpan.FromSeconds(11) });

    var minted = mint.Mint(new ControlMintRequest<ProbeControlSignal> {
      Payload = new ProbeControlSignal("checkpoint"),
      Cadence = TimeSpan.FromSeconds(60),
    });

    await Assert.That(minted.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(11));
  }

  [Test]
  public async Task Mint_Disabled_MintsNoTimeToLiveAsync() {
    // The killswitch must yield the pre-phase-9 wire shape exactly: no TTL at all, not a very
    // long one — a long TTL still changes the broker message.
    var mint = _mint(new ControlClassOptions { Enabled = false });

    var minted = mint.Mint(new ControlMintRequest<ProbeControlSignal> {
      Payload = new ProbeControlSignal("checkpoint"),
      Cadence = TimeSpan.FromSeconds(60),
      TimeToLive = TimeSpan.FromSeconds(9),
    });

    await Assert.That(minted.TimeToLive).IsNull();
  }

  [Test]
  public async Task Mint_NullRequest_ThrowsAsync() {
    var mint = _mint();

    await Assert.That(() => mint.Mint<ProbeControlSignal>(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddWhizbang_ResolvesACheckpointMintThatDerivesTtlAsync() {
    // The phase-4 facade shape does not change — mint.Checkpoints simply stops being empty.
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();

    var mint = provider.GetRequiredService<IEventMint>();
    var minted = mint.Checkpoints.Mint(new ControlMintRequest<ProbeControlSignal> {
      Payload = new ProbeControlSignal("checkpoint"),
      Cadence = TimeSpan.FromSeconds(60),
    });

    await Assert.That(minted.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  // ========================================
  // The destination rail the minted TTL rides
  // ========================================

  [Test]
  public async Task Stamp_ThenRead_RoundTripsAsync() {
    var destination = new TransportDestination("inbox.whizbang", "whizbang.core.messaging.integritycheckpoint");

    var stamped = ControlMessageTtl.Stamp(destination, TimeSpan.FromSeconds(120));

    await Assert.That(ControlMessageTtl.FromMetadata(stamped.Metadata)).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task Stamp_LeavesAddressAndRoutingKeyUntouchedAsync() {
    // A lifetime changes how long an entity holds the message, never which entity it is.
    var destination = new TransportDestination("inbox.whizbang", "whizbang.core.messaging.integritycheckpoint");

    var stamped = ControlMessageTtl.Stamp(destination, TimeSpan.FromSeconds(120));

    await Assert.That(stamped.Address).IsEqualTo(destination.Address);
    await Assert.That(stamped.RoutingKey).IsEqualTo(destination.RoutingKey);
  }

  [Test]
  public async Task Stamp_PreservesExistingMetadataAsync() {
    // Order of stamping must not matter: the session key (ControlPlaneDestination) and the
    // TransportNamespace key (phase 8) already ride this bag.
    var destination = TransportNamespaces.Stamp(
      new TransportDestination("inbox.whizbang"), "control");

    var stamped = ControlMessageTtl.Stamp(destination, TimeSpan.FromSeconds(120));

    await Assert.That(TransportNamespaces.FromMetadata(stamped.Metadata)).IsEqualTo("control");
    await Assert.That(ControlMessageTtl.FromMetadata(stamped.Metadata)).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task FromMetadata_AbsentOrUnreadable_IsNullAsync() {
    await Assert.That(ControlMessageTtl.FromMetadata(null)).IsNull();
    await Assert.That(ControlMessageTtl.FromMetadata(new Dictionary<string, JsonElement>())).IsNull();
    await Assert.That(ControlMessageTtl.FromMetadata(new Dictionary<string, JsonElement> {
      [ControlMessageTtl.METADATA_KEY] = JsonElementHelper.FromString("not-a-number"),
    })).IsNull()
      .Because("an unreadable stamp must degrade to 'no TTL', never to a zero-length lifetime");
  }

  [Test]
  public async Task Stamp_NonPositiveTimeToLive_ThrowsAsync() {
    var destination = new TransportDestination("inbox.whizbang");

    await Assert.That(() => ControlMessageTtl.Stamp(destination, TimeSpan.Zero))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => ControlMessageTtl.Stamp(destination, TimeSpan.FromSeconds(-1)))
      .Throws<ArgumentOutOfRangeException>();
  }
}
