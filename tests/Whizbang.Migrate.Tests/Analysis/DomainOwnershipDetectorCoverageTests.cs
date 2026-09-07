using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Coverage-round tests for DomainOwnershipDetector branches not exercised by
/// DomainOwnershipDetectorTests: a message type declared as a plain class rather than a
/// record.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/DomainOwnershipDetector.cs:*</tests>
public class DomainOwnershipDetectorCoverageTests {

  // Wolverine and Marten codebases sometimes model commands/events as plain classes rather
  // than records. If the class path of the message-type check were dark, domain detection
  // would silently miss every one of those message types in a codebase that doesn't happen to
  // use records, and under-report -- or entirely miss -- that service's domain ownership.
  [Test]
  public async Task DetectAsync_FindsDomainsFromClassesWithMessageSuffixes_NotJustRecordsAsync() {
    // Arrange - generic namespace forces extraction from the type name, same as the existing
    // record-based "RecognizesCommandSuffix" test, but for a plain class.
    const string sourceCode = """
      namespace MyApp.Contracts;

      public class PlaceOrderCommand {
        public Guid OrderId { get; set; }
      }
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Contracts/PlaceOrderCommand.cs");

    // Assert
    await Assert.That(result.HasDetections).IsTrue()
      .Because("a plain class ending in a message suffix must be recognized as a message "
             + "type, the same as a record ending in the same suffix would be");
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("order")
      .Because("the domain is extracted from the type name the same way regardless of "
             + "whether the type is a class or a record");
  }
}
