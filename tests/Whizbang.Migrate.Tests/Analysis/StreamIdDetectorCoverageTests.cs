using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Coverage-round tests for <see cref="StreamIdDetector"/> targeting the early return in
/// record-parameter analysis when an event-named record has no primary constructor (a
/// body-style record rather than a positional one).
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/StreamIdDetector.cs:124</tests>
public class StreamIdDetectorCoverageTests {

  // _isLikelyEventRecord recognizes a record by its "*Created"/"*Event"/etc. name suffix even
  // when it has no primary constructor. If the parameter-list guard in
  // _analyzeRecordParameters stopped skipping that case cleanly, a body-style event record
  // would either crash detection for the whole file or need special-casing -- and either way, a
  // developer using body-style records instead of positional ones would get no ID-detection
  // recommendation at all in the migration wizard.
  [Test]
  public async Task DetectAsync_EventNamedRecordWithoutPrimaryConstructor_IsSkippedNotCrashedAsync() {
    // Arrange -- body-style record: matches the "Created" name suffix, but ParameterList is null.
    const string sourceCode = """
      public record OrderCreated {
        public Guid StreamId { get; init; }
      }
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderCreated.cs");

    // Assert -- the only record in the file is body-style, so nothing is detected from it
    await Assert.That(result.HasDetections).IsFalse()
      .Because("a body-style event record has no ParameterList to scan; the detector must skip "
             + "it cleanly instead of throwing on the missing parameter list");
    await Assert.That(result.DetectedProperties).IsEmpty();
  }
}
