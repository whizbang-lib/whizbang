using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Covers <see cref="TagHookRegistration.DefaultFireAt"/> — the sibling of
/// <c>DefaultPriority</c>, which <c>TagHookRegistrationTests</c> already asserts directly, but
/// <c>DefaultFireAt</c> itself is never read anywhere in the suite.
/// </summary>
public class TagHookRegistrationCoverageTests {

  /// <summary>
  /// If this ever stopped being null, a consumer reading the documented default to decide "does
  /// this hook fire at every lifecycle stage or just one" would be told the wrong stage — the
  /// property exists specifically so that question has a discoverable, correct answer.
  /// </summary>
  [Test]
  public async Task TagHookRegistration_DefaultFireAt_IsNullAsync() {
    await Assert.That(TagHookRegistration.DefaultFireAt).IsNull()
      .Because("null means 'fire at all stages' — the documented default every hook gets unless "
             + "it explicitly narrows to one lifecycle stage");
  }
}
