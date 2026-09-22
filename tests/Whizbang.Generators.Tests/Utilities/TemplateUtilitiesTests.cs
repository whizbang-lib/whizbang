extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TemplateUtilities = shared::Whizbang.Generators.Shared.Utilities.TemplateUtilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for TemplateUtilities.ReplaceRegion — the #region/#endregion splice the
/// generators use to inject code into embedded templates.
/// </summary>
public class TemplateUtilitiesTests {

  [Test]
  public async Task ReplaceRegion_ReplacesTheRegionBodyAsync() {
    const string template = "class X {\n  #region Body\n  old();\n  #endregion\n}\n";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", "fresh();");

    await Assert.That(result).Contains("fresh();");
    await Assert.That(result).DoesNotContain("old();");
    await Assert.That(result).DoesNotContain("#region");
  }

  [Test]
  public async Task ReplaceRegion_KeepsTheRegionIndentationAsync() {
    const string template = "class X {\n    #region Body\n    old();\n    #endregion\n}\n";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", "fresh();");

    await Assert.That(result).Contains("    fresh();");
  }

  [Test]
  public async Task ReplaceRegion_UnknownRegion_ReturnsTemplateUnchangedAsync() {
    const string template = "class X {\n  #region Body\n  old();\n  #endregion\n}\n";

    var result = TemplateUtilities.ReplaceRegion(template, "NotThere", "fresh();");

    await Assert.That(result).IsEqualTo(template);
  }

  [Test]
  public async Task ReplaceRegion_RegionWithNoEndRegion_ReturnsTemplateUnchangedAsync() {
    // An unterminated region is a malformed template: splice nothing rather than
    // swallow the rest of the file.
    const string template = "class X {\n  #region Body\n  old();\n}\n";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", "fresh();");

    await Assert.That(result).IsEqualTo(template);
  }

  [Test]
  public async Task ReplaceRegion_ContentAfterEndRegionOnSameLine_IsPreservedAsync() {
    // Inline form: the region sits inside a statement, so whatever follows #endregion
    // on that line (here the terminating semicolon) has to survive the splice.
    const string template = "var x = #region Body\nold\n#endregion;\ntrailing\n";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", "fresh");

    await Assert.That(result).Contains("fresh");
    await Assert.That(result).Contains(";");
    await Assert.That(result).Contains("trailing");
    await Assert.That(result).DoesNotContain("#endregion");
  }

  [Test]
  public async Task ReplaceRegion_EndRegionAtEndOfTemplate_DoesNotOverrunAsync() {
    // No newline after #endregion: the line-ending consumer must stop at the end of
    // the string rather than reading past it.
    const string template = "class X {\n  #region Body\n  old();\n  #endregion";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", "fresh();");

    await Assert.That(result).Contains("fresh();");
    await Assert.That(result).DoesNotContain("#endregion");
    await Assert.That(result).DoesNotContain("old();");
  }

  [Test]
  public async Task ReplaceRegion_CrLfTemplate_ConsumesThePairAsync() {
    const string template = "class X {\r\n  #region Body\r\n  old();\r\n  #endregion\r\n}\r\n";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", "fresh();");

    await Assert.That(result).Contains("fresh();");
    await Assert.That(result).DoesNotContain("#endregion");
    await Assert.That(result).Contains("}");
  }

  [Test]
  public async Task ReplaceRegion_EmptyReplacement_RemovesTheRegionAsync() {
    const string template = "class X {\n  #region Body\n  old();\n  #endregion\n}\n";

    var result = TemplateUtilities.ReplaceRegion(template, "Body", string.Empty);

    await Assert.That(result).DoesNotContain("old();");
    await Assert.That(result).DoesNotContain("#region");
  }
}
