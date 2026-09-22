extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TypeNameUtilities = shared::Whizbang.Generators.Shared.Utilities.TypeNameUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The display form's argument guard: a generator that hands the helper a symbol it failed to
/// resolve gets a clear exception at the call, not a null reference from inside the formatter.
/// </summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/TypeNameUtilities.cs</code-under-test>
public class TypeNameUtilitiesGuardTests {
  [Test]
  public async Task Display_RejectsANullSymbolAsync() {
    await Assert.That(() => TypeNameUtilities.Display(null!)).Throws<ArgumentNullException>();
  }
}
