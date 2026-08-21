using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Shared-inbox retirement (topology arc phase 7, spec migration step 3): the explicit
/// opt-in that completes the per-namespace-command-inbox migration. Under retirement the
/// subscription topology is exactly per-namespace inboxes + the one system broadcast inbox —
/// no catch-all remnant. Retirement is valid ONLY once EVERY command namespace is flipped
/// (<see cref="RoutingOptions.RouteAllCommandNamespacesToInbox"/> / the <c>"*"</c>
/// configuration entry): a namespace still publishing to the shared inbox after the
/// subscription is dropped would be silent loss, so startup validation throws instead.
/// </summary>
public class SharedInboxRetirementTests {
  #region RoutingOptions API

  [Test]
  public async Task RetireSharedInbox_DefaultsOn_AndBothFluentSwitchesChainAsync() {
    // DEFAULT LOCK (changed by the topology arc's default flip — retirement asserted OFF while
    // it was an explicit opt-in): the migration end-state IS the default now, and
    // KeepSharedInbox is its explicit inverse.
    var options = new RoutingOptions();
    await Assert.That(options.SharedInboxRetired).IsTrue()
      .Because("the catch-all is retired out of the box");

    var kept = options.KeepSharedInbox();
    await Assert.That(kept).IsSameReferenceAs(options);
    await Assert.That(options.SharedInboxRetired).IsFalse();

    var result = options.RetireSharedInbox();
    await Assert.That(result).IsSameReferenceAs(options);
    await Assert.That(options.SharedInboxRetired).IsTrue();
  }

  #endregion

  #region Startup validation — the retirement guard

  [Test]
  public async Task WithRouting_RetirementWithFullFlip_ResolvesAndIsRetiredAsync() {
    // The happy path: full flip + retirement resolves cleanly and the flag is live.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => {
      r.RouteAllCommandNamespacesToInbox();
      r.RetireSharedInbox();
    });

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.SharedInboxRetired).IsTrue();
  }

  [Test]
  public async Task WithRouting_RetirementWithoutFullFlip_ThrowsClearStartupErrorAsync() {
    // THE GUARD: retirement with the flip incomplete = a namespace still publishing to the
    // shared inbox with nobody subscribed = silent loss. Startup must throw, not degrade.
    // Since the topology arc's default flip, the incoherent combination can only be reached by
    // asking for BOTH halves explicitly — which is exactly what this arranges.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => {
      r.RouteNoCommandNamespacesToInbox();
      r.RetireSharedInbox();
    });

    using var provider = services.BuildServiceProvider();

    var exception = Assert.Throws<InvalidOperationException>(
      () => provider.GetRequiredService<IOptions<RoutingOptions>>());
    await Assert.That(exception!.Message).Contains("RouteAllCommandNamespacesToInbox")
      .Because("the error must point the operator at the missing full-flip switch");
    await Assert.That(exception.Message).Contains("silent loss")
      .Because("the error explains WHY partial retirement is unsafe");
  }

  [Test]
  public async Task WithRouting_RetirementWithPartialExplicitFlips_ThrowsAndNamesTheUnflippedStateAsync() {
    // Per-namespace flips are NOT enough — only the all-namespaces end-state qualifies. The
    // error names the partial state so the operator can see how far the migration got.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => {
      r.RouteCommandNamespaceToInbox("MyApp.Orders.Commands");
      r.RetireSharedInbox();
    });

    using var provider = services.BuildServiceProvider();

    var exception = Assert.Throws<InvalidOperationException>(
      () => provider.GetRequiredService<IOptions<RoutingOptions>>());
    await Assert.That(exception!.Message).Contains("myapp.orders.commands")
      .Because("the message names the explicitly flipped namespaces — the unflipped state made visible");
  }

  [Test]
  public async Task WithRouting_ResolvingOutboxStrategy_AlsoTripsTheRetirementGuardAsync() {
    // A consumer that resolves ONLY the strategy (never IOptions<RoutingOptions>) still hits
    // the guard — the strategy factory forces options resolution first.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => {
      r.Outbox.UseNamespaceRouting();
      r.RouteNoCommandNamespacesToInbox();
      r.RetireSharedInbox();
    });

    using var provider = services.BuildServiceProvider();

    Assert.Throws<InvalidOperationException>(
      () => provider.GetRequiredService<IOutboxRoutingStrategy>());
    await Task.CompletedTask;
  }

  #endregion

  #region Configuration binding — Whizbang:Routing:RetireSharedInbox

  [Test]
  public async Task WithRouting_ConfigurationRetireSharedInbox_WithWildcardFlip_BindsAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "*",
        ["Whizbang:Routing:RetireSharedInbox"] = "true"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Inbox.UseNamespaceInboxes());

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.SharedInboxRetired).IsTrue();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsTrue();
  }

  [Test]
  public async Task WithRouting_ConfigurationRetirementWithoutFullFlip_ThrowsOnResolutionAsync() {
    // The guard applies to configuration-driven retirement identically: the config binder
    // runs first, THEN validation — an operator cannot config their way into silent loss.
    // (The flip must now be switched OFF explicitly to reach the incoherent combination.)
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:RouteAllCommandNamespacesToInbox"] = "false",
        ["Whizbang:Routing:RetireSharedInbox"] = "true"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Inbox.UseNamespaceInboxes());

    using var provider = services.BuildServiceProvider();

    Assert.Throws<InvalidOperationException>(
      () => provider.GetRequiredService<IOptions<RoutingOptions>>());
    await Task.CompletedTask;
  }

  [Test]
  public async Task WithRouting_ConfigurationRetireSharedInboxFalse_NotRetiredAsync() {
    // An explicit "false" is the rollback rung: publishers stay flipped, the receiver keeps the
    // catch-all. (Absent no longer means "off" — since the topology arc's default flip, absent
    // means "follow the flip", i.e. retired. Locked in DefaultNamespaceTopologyTests.)
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "*",
        ["Whizbang:Routing:RetireSharedInbox"] = "false"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(_ => { });

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.SharedInboxRetired).IsFalse();
  }

  #endregion
}
