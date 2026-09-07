using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tail-of-round coverage for <see cref="TypeNameFormatter"/>: the fallback path in
/// <see cref="TypeNameFormatter.GetPayloadNamespace"/> and
/// <see cref="TypeNameFormatter.GetPayloadFullName"/> when a generic-envelope marker (<c>[[</c>)
/// is present but never closed (<c>]]</c> missing) — a truncated or hand-built envelope-type
/// string, not the well-formed generic wrapper both methods are primarily written for.
/// </summary>
/// <code-under-test>src/Whizbang.Core/TypeNameFormatter.cs</code-under-test>
public class TypeNameFormatterCoverageTests {

  /// <summary>
  /// A malformed envelope-type string (partial log capture, hand-built lookup key) must still
  /// resolve to a sane namespace rather than throwing or silently returning the wrong segment —
  /// metrics dimensioning and lens routing both key off this.
  /// </summary>
  [Test]
  public async Task GetPayloadNamespace_UnclosedGenericMarker_FallsBackToWholeStringAsync() {
    var ns = TypeNameFormatter.GetPayloadNamespace("MyApp.Chat.MyEvent[[Unclosed");

    await Assert.That(ns).IsEqualTo("MyApp.Chat")
      .Because("without a matching ']]' there is no inner payload type to extract, so this must "
             + "fall back to treating the whole string as an ordinary (non-generic) type name");
  }

  /// <summary>
  /// Same fallback, for the full-name variant used as a bounded metric dimension — an unclosed
  /// marker must not corrupt the dimension value or throw mid-dispatch.
  /// </summary>
  [Test]
  public async Task GetPayloadFullName_UnclosedGenericMarker_FallsBackToWholeStringAsync() {
    var full = TypeNameFormatter.GetPayloadFullName("MyApp.Chat.MyEvent[[Unclosed");

    await Assert.That(full).IsEqualTo("MyApp.Chat.MyEvent[[Unclosed")
      .Because("without a matching ']]' there is no inner payload type to extract, so this must "
             + "fall back to the whole string via GetFullName");
  }
}
