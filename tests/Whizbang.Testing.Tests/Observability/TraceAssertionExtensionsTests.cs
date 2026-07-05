using System.Diagnostics;
using Whizbang.Testing.Observability;

namespace Whizbang.Testing.Tests.Observability;

/// <summary>
/// Tests for <see cref="TraceAssertionExtensions"/> - collector-level assertion helpers,
/// baseline matching, and their failure messages.
/// </summary>
public class TraceAssertionExtensionsTests {
  private static string _uniqueSourceName() => $"WhizbangTest.{Guid.NewGuid():N}";

  private static ActivityContext _remoteParentContext() =>
    new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

  [Test]
  public async Task AssertHasSpans_WithSpans_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("op")) { }

    collector.AssertHasSpans();

    await Assert.That(collector.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AssertHasSpans_Empty_ThrowsAsync() {
    using var collector = new InMemorySpanCollector(_uniqueSourceName());

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertHasSpans());

    await Assert.That(ex!.Message).Contains("Expected at least one span");
  }

  [Test]
  public async Task AssertMinSpanCount_Satisfied_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("op-1")) { }
    using (source.StartActivity("op-2")) { }

    collector.AssertMinSpanCount(2);

    await Assert.That(collector.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AssertMinSpanCount_TooFew_ThrowsWithCountsAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("op")) { }

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertMinSpanCount(5));

    await Assert.That(ex!.Message).Contains("Expected at least 5 spans, but found 1");
  }

  [Test]
  public async Task AssertNoOrphanedSpans_CleanTrace_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("parent")) {
      using (source.StartActivity("child")) { }
    }

    collector.AssertNoOrphanedSpans();

    await Assert.That(collector.HasOrphanedSpans()).IsFalse();
  }

  [Test]
  public async Task AssertNoOrphanedSpans_WithOrphan_ThrowsNamingOrphanAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("lost-child", ActivityKind.Internal, _remoteParentContext())) { }

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertNoOrphanedSpans());

    await Assert.That(ex!.Message).Contains("orphaned spans");
    await Assert.That(ex.Message).Contains("'lost-child'");
  }

  [Test]
  public async Task AssertHasSpan_Existing_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("the-span")) { }

    collector.AssertHasSpan("the-span");

    await Assert.That(collector.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AssertHasSpan_Missing_ThrowsListingAvailableAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("present")) { }

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertHasSpan("absent"));

    await Assert.That(ex!.Message).Contains("Expected span 'absent' not found");
    await Assert.That(ex.Message).Contains("'present'");
  }

  [Test]
  public async Task AssertHasSpan_Missing_ManySpans_TruncatesListAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    for (var i = 0; i < 11; i++) {
      using (source.StartActivity($"span-{i}")) { }
    }

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertHasSpan("absent"));

    await Assert.That(ex!.Message).Contains("(showing 10 of 11)");
  }

  [Test]
  public async Task AssertHasSpanContaining_Match_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("Dispatch SomeCommand")) { }

    collector.AssertHasSpanContaining("SomeCommand");

    await Assert.That(collector.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AssertHasSpanContaining_NoMatch_ThrowsAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("something")) { }

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertHasSpanContaining("missing"));

    await Assert.That(ex!.Message).Contains("Expected span containing 'missing' not found in 1 captured spans");
  }

  [Test]
  public async Task AssertMatchesBaseline_MatchingSnapshot_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("root-op")) {
      using (source.StartActivity("child-op")) { }
    }
    var baseline = collector.BuildTree().ToSnapshot();

    collector.AssertMatchesBaseline(baseline);

    await Assert.That(collector.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AssertMatchesBaseline_Mismatch_ThrowsWithDifferencesAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("actual-op")) { }
    const string baseline = """
      {
        "name": "expected-op",
        "kind": "Internal",
        "status": "Unset"
      }
      """;

    var ex = Assert.Throws<TraceAssertionException>(() => collector.AssertMatchesBaseline(baseline));

    await Assert.That(ex!.Message).Contains("difference(s):");
    await Assert.That(ex.Message).Contains("expected-op");
    await Assert.That(ex.Message).Contains("actual-op");
  }

  [Test]
  public async Task AssertMatchesBaselineFileAsync_MissingFile_ThrowsFileNotFoundWithHintAsync() {
    using var collector = new InMemorySpanCollector(_uniqueSourceName());
    var missingPath = Path.Combine(Path.GetTempPath(), $"whizbang-missing-{Guid.NewGuid():N}.json");

    var ex = await Assert.ThrowsAsync<FileNotFoundException>(
      async () => await collector.AssertMatchesBaselineFileAsync(missingPath));

    await Assert.That(ex!.Message).Contains("Baseline file not found");
    await Assert.That(ex.Message).Contains("Generate it using");
  }

  [Test]
  public async Task AssertMatchesBaselineFileAsync_MatchingFile_DoesNotThrowAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("baseline-op")) { }

    var path = Path.Combine(Path.GetTempPath(), $"whizbang-baseline-{Guid.NewGuid():N}.json");
    try {
      await File.WriteAllTextAsync(path, collector.BuildTree().ToSnapshot());

      await collector.AssertMatchesBaselineFileAsync(path);

      await Assert.That(collector.Count).IsEqualTo(1);
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public async Task SaveBaselineAsync_CreatesDirectoryAndWritesSnapshotAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("saved-op")) { }

    var directory = Path.Combine(Path.GetTempPath(), $"whizbang-baselines-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "baseline.json");
    try {
      await collector.SaveBaselineAsync(path);

      var written = await File.ReadAllTextAsync(path);
      await Assert.That(written).Contains("saved-op");
      await Assert.That(written).IsEqualTo(collector.BuildTree().ToSnapshot());
    } finally {
      if (Directory.Exists(directory)) {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Test]
  public async Task GetSpan_Existing_ReturnsSpanAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("findable")) { }

    var span = collector.GetSpan("findable");

    await Assert.That(span.Name).IsEqualTo("findable");
  }

  [Test]
  public async Task GetSpan_Missing_ThrowsAsync() {
    using var collector = new InMemorySpanCollector(_uniqueSourceName());

    var ex = Assert.Throws<TraceAssertionException>(() => collector.GetSpan("ghost"));

    await Assert.That(ex!.Message).Contains("Span 'ghost' not found");
  }

  [Test]
  public async Task GetSingleRoot_OneRoot_ReturnsItAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("only-root")) {
      using (source.StartActivity("nested")) { }
    }

    var root = collector.GetSingleRoot();

    await Assert.That(root.Name).IsEqualTo("only-root");
  }

  [Test]
  public async Task GetSingleRoot_NoSpans_ThrowsAsync() {
    using var collector = new InMemorySpanCollector(_uniqueSourceName());

    var ex = Assert.Throws<TraceAssertionException>(() => collector.GetSingleRoot());

    await Assert.That(ex!.Message).Contains("No root spans found");
  }

  [Test]
  public async Task GetSingleRoot_MultipleRoots_ThrowsListingRootsAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);
    using (source.StartActivity("root-1")) { }
    using (source.StartActivity("root-2")) { }

    var ex = Assert.Throws<TraceAssertionException>(() => collector.GetSingleRoot());

    await Assert.That(ex!.Message).Contains("Expected single root span, but found 2");
    await Assert.That(ex.Message).Contains("'root-1'");
    await Assert.That(ex.Message).Contains("'root-2'");
  }

  [Test]
  public async Task Extensions_NullCollector_ThrowArgumentNullAsync() {
    InMemorySpanCollector collector = null!;

    var ex = Assert.Throws<ArgumentNullException>(() => collector.AssertHasSpans());

    await Assert.That(ex!.ParamName).IsEqualTo("collector");
  }
}
