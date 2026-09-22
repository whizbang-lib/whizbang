using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tail-of-round coverage for <see cref="NamespacePattern"/>: the guard against a
/// <see cref="System.Type"/> whose <c>FullName</c> is null or empty (an open generic type
/// parameter, e.g. the unbound <c>T</c> of <c>List&lt;T&gt;</c>).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/NamespacePattern.cs</code-under-test>
public class NamespacePatternCoverageTests {

  /// <summary>
  /// Auto-discovery walks assemblies for candidate message types and can encounter an unbound
  /// generic parameter (never a real message type) before it is filtered out elsewhere. Letting a
  /// null <c>FullName</c> reach the regex would throw and abort discovery for every other type in
  /// the batch — refusing the match up front keeps one malformed candidate from poisoning the scan.
  /// </summary>
  [Test]
  public async Task Matches_TypeWithNullFullName_ReturnsFalseAsync() {
    var pattern = new NamespacePattern("*");
    var openGenericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(openGenericParameter.FullName).IsNull()
      .Because("the test setup must actually exercise a null FullName, not merely assume one");

    var matched = pattern.Matches(openGenericParameter);

    await Assert.That(matched).IsFalse();
  }
}
