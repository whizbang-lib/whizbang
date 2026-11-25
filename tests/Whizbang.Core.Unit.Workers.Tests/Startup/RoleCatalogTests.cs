// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2594: every role is declared once. The framework's own roles are declared in the catalog itself, a host's through
/// <see cref="RoleCatalogServiceCollectionExtensions.AddWhizbangRole"/>, and a family of roles by its common start. A
/// role the catalog does not declare is unknown to it, so the elector refuses to vote for it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/RoleCatalog.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/RoleCatalogServiceCollectionExtensions.cs</code-under-test>
[Category("Startup")]
public class RoleCatalogTests {
  [Test]
  public async Task TheFrameworkRoles_AreDeclared_WithTheirKindsAsync() {
    var catalog = new RoleCatalog([]);

    await Assert.That(catalog.Find(StartupDuties.MIGRATOR)?.Kind).IsEqualTo(RoleKind.Episodic);
    await Assert.That(catalog.Find(StartupDuties.MAINTAINER)?.Kind).IsEqualTo(RoleKind.Standing);
    await Assert.That(catalog.Find(CommitOrderStamperOptions.ROLE)?.Kind).IsEqualTo(RoleKind.Standing);
    await Assert.That(catalog.Find(PartitionAssignerOptions.ROLE)?.Kind).IsEqualTo(RoleKind.Standing);
    await Assert.That(catalog.Find(PerspectiveRebuildDuty.For("orders"))?.Kind).IsEqualTo(RoleKind.Episodic)
      .Because("every table's rebuild duty is declared once, by the family's common start");
    await Assert.That(catalog.Declarations.Count).IsEqualTo(RoleCatalog.FrameworkRoles.Count);
  }

  [Test]
  public async Task AnUndeclaredRole_IsNotDeclaredAsync() {
    var catalog = new RoleCatalog([]);

    await Assert.That(catalog.Declares("nightly-report")).IsFalse();
    await Assert.That(catalog.Find("nightly-report")).IsNull();
    await Assert.That(catalog.Declares(PerspectiveRebuildDuty.DUTY_PREFIX.TrimEnd(':'))).IsFalse()
      .Because("a prefix declares the roles that start with it, not a shorter name");
  }

  [Test]
  public async Task AHostsRole_IsDeclared_AndAnExactNameWinsOverAPrefixAsync() {
    var catalog = new RoleCatalog([
      new RoleDeclaration("report:", RoleKind.Episodic, isPrefix: true),
      new RoleDeclaration("report:nightly", RoleKind.Standing),
    ]);

    await Assert.That(catalog.Find("report:nightly")?.Kind).IsEqualTo(RoleKind.Standing);
    await Assert.That(catalog.Find("report:weekly")?.Kind).IsEqualTo(RoleKind.Episodic);
    await Assert.That(catalog.Declares("report:weekly")).IsTrue();
  }

  [Test]
  public async Task ARoleDeclaredTwice_WithOneKind_IsOneDeclaration_AndWithTwoKinds_IsRefusedAsync() {
    var twice = new RoleCatalog([new RoleDeclaration("x", RoleKind.Standing), new RoleDeclaration("x", RoleKind.Standing)]);
    await Assert.That(twice.Declarations.Count(d => d.Name == "x")).IsEqualTo(1);

    await Assert.That(() => new RoleCatalog([new RoleDeclaration(StartupDuties.MIGRATOR, RoleKind.Standing)]))
      .Throws<InvalidOperationException>()
      .Because("one role is held one way; two kinds for one name is a configuration that cannot work");
    await Assert.That(() => new RoleCatalog([new RoleDeclaration(PerspectiveRebuildDuty.DUTY_PREFIX, RoleKind.Episodic)]))
      .Throws<InvalidOperationException>().WithMessageContaining("as a family held Episodic and as a role held Episodic")
      .Because("a name is a role or the start of a family, never both");
  }

  [Test]
  public async Task ADeclaration_RefusesAnEmptyName_AndTheCatalogRefusesNullsAsync() {
    await Assert.That(() => new RoleDeclaration(" ", RoleKind.Standing)).Throws<ArgumentException>();
    await Assert.That(() => new RoleCatalog(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new RoleCatalog([]).Find(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddWhizbangRole_DeclaresIt_AndTheHolderLoopAndHealthManageItAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangRole("nightly-report", RoleKind.Standing);
    services.AddWhizbangRolePrefix("report:", RoleKind.Episodic);
    services.AddWhizbangRole("nightly-export", RoleKind.Episodic);
    await using var provider = services.BuildServiceProvider();

    var catalog = provider.GetRequiredService<RoleCatalog>();
    await Assert.That(catalog.Find("nightly-report")?.Kind).IsEqualTo(RoleKind.Standing);
    await Assert.That(catalog.Declares("report:x")).IsTrue();
    await Assert.That(catalog.Declares(StartupDuties.MAINTAINER)).IsTrue().Because("the framework's roles are always declared");
    var options = provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
    await Assert.That(options.Manages("nightly-report")).IsTrue()
      .Because("one registration declares the role and puts it in the roster the holder loop and health read");
    await Assert.That(options.Manages("report:")).IsFalse().Because("a family is held per member, never as itself");
    await Assert.That(catalog.Declares("nightly-export")).IsTrue();
    await Assert.That(options.Manages("nightly-export")).IsFalse()
      .Because("an episodic role is held only by the work that asks for it, so its vacancy is never a fault");
  }

  [Test]
  public async Task AddWhizbangRole_RefusesNullsAndEmptyNamesAsync() {
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangRole("x", RoleKind.Standing)).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangRole("", RoleKind.Standing)).Throws<ArgumentException>();
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangRolePrefix("x:", RoleKind.Episodic)).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangRolePrefix("", RoleKind.Episodic)).Throws<ArgumentException>();
  }
}
