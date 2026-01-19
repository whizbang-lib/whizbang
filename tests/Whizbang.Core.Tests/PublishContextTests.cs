using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for PublishContext enumeration and behavior.
/// Ensures dual publishing patterns are properly supported.
/// </summary>
[Category("Core")]
public class PublishContextTests {
  [Test]
  public async Task PublishContext_HasImmediate_ValueAsync() {
    // Arrange & Act
    var context = PublishContext.Immediate;

    // Assert
    await Assert.That((int)context).IsEqualTo(0);
  }

  [Test]
  public async Task PublishContext_HasPostCommit_ValueAsync() {
    // Arrange & Act
    var context = PublishContext.PostCommit;

    // Assert
    await Assert.That((int)context).IsEqualTo(1);
  }

  [Test]
  public async Task PublishContext_Immediate_IsNotEqualTo_PostCommitAsync() {
    // Arrange
    var immediate = PublishContext.Immediate;
    var postCommit = PublishContext.PostCommit;

    // Assert
    await Assert.That(immediate).IsNotEqualTo(postCommit);
  }

  [Test]
  public async Task PublishContext_ToString_ReturnsCorrectNamesAsync() {
    // Arrange & Act
    var immediateName = PublishContext.Immediate.ToString();
    var postCommitName = PublishContext.PostCommit.ToString();

    // Assert
    await Assert.That(immediateName).IsEqualTo("Immediate");
    await Assert.That(postCommitName).IsEqualTo("PostCommit");
  }
}
