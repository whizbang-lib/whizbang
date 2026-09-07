using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 tail: <see cref="MessageHeaders.Metadata"/>'s default-value initializer. The
/// only production construction site (<c>AsbMessageHeaderReader</c>) always sets every optional
/// header explicitly, so this default has never actually run in any test.
/// </summary>
[Category("Messaging")]
public class MessageHeadersCoverageTests {

  /// <summary>
  /// Operator impact: every consumer of <see cref="MessageHeaders.Metadata"/> iterates it without
  /// a null check (it is typed as non-nullable). If the default regressed to null, the first
  /// transport that constructs headers without custom metadata would NRE at dispatch time instead
  /// of a benign empty dictionary.
  /// </summary>
  [Test]
  public async Task Metadata_WhenNotSet_DefaultsToEmptyNotNullAsync() {
    var headers = new MessageHeaders {
      MessageId = MessageId.New(),
      EnvelopeTypeName = "MyApp.Events.OrderCreated, MyApp.Contracts",
      PayloadJson = "{}",
    };

    await Assert.That(headers.Metadata).IsNotNull();
    await Assert.That(headers.Metadata.Count).IsEqualTo(0)
      .Because("no ApplicationProperties metadata was supplied — the default must be an empty, "
             + "safely-iterable dictionary, never null");
  }
}
