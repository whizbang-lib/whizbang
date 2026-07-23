using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the turnkey default: <see cref="SchemaInitializationOptions.NonBlockingSchemaInit"/> is
/// <see langword="true"/> out of the box, so a long startup migration doesn't block the port / roll
/// the pod back by default. Opt out by setting it false.
/// </summary>
public class SchemaInitializationOptionsTests {
  [Test]
  public async Task NonBlockingSchemaInit_TrueByDefault_TurnkeyAsync() {
    await Assert.That(new SchemaInitializationOptions().NonBlockingSchemaInit).IsTrue();
  }

  [Test]
  public async Task MigrationTimeout_NullByDefaultAsync() {
    await Assert.That(new SchemaInitializationOptions().MigrationTimeout).IsNull();
  }
}
