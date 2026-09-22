using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Coverage-round-23 tests for <see cref="PerspectiveScope"/> targeting
/// <see cref="PerspectiveScope.SetExtension"/>'s update-in-place branch and
/// <see cref="PerspectiveScope.RemoveExtension"/>.
/// </summary>
/// <tests>PerspectiveScope</tests>
public class PerspectiveScopeCoverageTests {

  // A scope decides which tenant's/principal's data a projection sees. If updating an existing
  // extension key silently appended a duplicate instead of overwriting it, GetValue's
  // FirstOrDefault would keep returning the stale first value forever -- the scope would look
  // unchanged to every reader even though the caller believed they had updated it.
  [Test]
  public async Task SetExtension_ExistingKey_UpdatesValueInPlaceAsync() {
    // Arrange
    var scope = new PerspectiveScope();
    scope.SetExtension("region", "us-west");

    // Act
    scope.SetExtension("region", "us-east");

    // Assert
    await Assert.That(scope.Extensions.Count).IsEqualTo(1)
      .Because("updating an existing extension key must overwrite in place, not append a duplicate");
    await Assert.That(scope.GetValue("region")).IsEqualTo("us-east");
  }

  // A scope decides which tenant's/principal's data a projection sees. Removing an extension is
  // used when scope inheritance/merge logic strips a stale key; if removal silently no-oped, the
  // stale key would linger forever and keep attaching its old value to every subsequent read of
  // this scope.
  [Test]
  public async Task RemoveExtension_ExistingKey_RemovesAndReturnsTrueAsync() {
    // Arrange
    var scope = new PerspectiveScope();
    scope.SetExtension("region", "us-west");

    // Act
    var removed = scope.RemoveExtension("region");

    // Assert
    await Assert.That(removed).IsTrue()
      .Because("the key existed, so removal must report success");
    await Assert.That(scope.Extensions.Count).IsEqualTo(0);
    await Assert.That(scope.GetValue("region")).IsNull();
  }

  [Test]
  public async Task RemoveExtension_UnknownKey_ReturnsFalseAndLeavesScopeUnchangedAsync() {
    // Arrange
    var scope = new PerspectiveScope();
    scope.SetExtension("region", "us-west");

    // Act
    var removed = scope.RemoveExtension("does-not-exist");

    // Assert
    await Assert.That(removed).IsFalse()
      .Because("removing a key that was never set must not be reported as a successful removal");
    await Assert.That(scope.Extensions.Count).IsEqualTo(1);
    await Assert.That(scope.GetValue("region")).IsEqualTo("us-west");
  }
}
