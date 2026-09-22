using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers the <see cref="TypeDefinitionInfo"/> positional record declared in
/// <c>IWorkCoordinator.cs</c> — a stored type-definition fingerprint row loaded on startup so the
/// reconciler can diff the code's current definitions against what was last persisted.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
public class TypeDefinitionInfoCoverageTests {

  [Test]
  public async Task Constructor_AllPositionalArguments_RoundTripEveryPropertyAsync() {
    // The reconciler diffs stored definitions by EventType and reacts to a changed SchemaVersion
    // to decide whether upcasting is needed. A constructor regression that dropped or transposed
    // either would misfile a type's history or apply the wrong upcaster chain on read.
    var info = new TypeDefinitionInfo(
      DefinitionId: 7,
      EventType: "MyApp.Events.OrderCreated",
      SettingsHashHex: "abc123",
      SchemaHashHex: "def456",
      SchemaVersion: 3);

    await Assert.That(info.DefinitionId).IsEqualTo(7);
    await Assert.That(info.EventType).IsEqualTo("MyApp.Events.OrderCreated");
    await Assert.That(info.SettingsHashHex).IsEqualTo("abc123");
    await Assert.That(info.SchemaHashHex).IsEqualTo("def456");
    await Assert.That(info.SchemaVersion).IsEqualTo(3);
  }
}
