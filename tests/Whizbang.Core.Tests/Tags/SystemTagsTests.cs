using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="SystemTags"/> — the framework-reserved tag vocabulary.
/// The "sys-" prefix is reserved: startup validation rejects application-declared tag
/// attributes that mint new sys-* tags, so framework and application tag namespaces
/// can never collide.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class SystemTagsTests {
  [Test]
  public async Task Audit_IsSysAuditAsync() {
    var audit = SystemTags.AUDIT;

    await Assert.That(audit).IsEqualTo("sys-audit");
  }

  [Test]
  public async Task ReservedPrefix_IsSysDashAsync() {
    var prefix = SystemTags.RESERVED_PREFIX;

    await Assert.That(prefix).IsEqualTo("sys-");
  }

  [Test]
  public async Task Audit_CarriesTheReservedPrefixAsync() {
    // Every framework tag must live inside the reserved namespace — a framework tag outside
    // "sys-" would be claimable by applications and defeat the collision guarantee.
    await Assert.That(SystemTags.AUDIT.StartsWith(SystemTags.RESERVED_PREFIX, StringComparison.Ordinal)).IsTrue();
  }

  [Test]
  public async Task IsFrameworkTag_KnowsAuditAsync() {
    await Assert.That(SystemTags.IsFrameworkTag(SystemTags.AUDIT)).IsTrue();
  }

  [Test]
  public async Task IsFrameworkTag_RejectsUnknownSysTagAsync() {
    await Assert.That(SystemTags.IsFrameworkTag("sys-mine")).IsFalse()
      .Because("only tags the framework itself ships are members of the SystemTags set");
  }

  [Test]
  public async Task IsFrameworkTag_RejectsUnprefixedTagAsync() {
    await Assert.That(SystemTags.IsFrameworkTag("audit")).IsFalse();
  }
}
