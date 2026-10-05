// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TemplateUtilities = shared::Whizbang.Generators.Shared.Utilities.TemplateUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Snippet extraction is a linear scan. It used to be a lazy, single-line regex over the whole template,
/// run once per type under a one-second match timeout. On a loaded CI runner that timeout fired, the
/// generator threw <see cref="RegexMatchTimeoutException"/>, and it emitted no source at all, which failed
/// tests that had nothing to do with snippets. These tests pin the scan to what the regex returned, for
/// every region of every embedded template and for the edge cases the templates do not exercise.
/// </summary>
/// <tests>src/Whizbang.Generators.Shared/Utilities/TemplateUtilities.cs</tests>
public class TemplateSnippetScanTests {
  private static readonly System.Reflection.Assembly _generatorsAssembly = typeof(MessageRegistryGenerator).Assembly;

  // The implementation the scan replaced, kept here as the specification it must agree with.
  private static string _regexExtract(string template, string regionName) {
    var pattern = $@"(\s*)#region\s+{Regex.Escape(regionName)}[^\r\n]*[\r\n]+(.*?)[\r\n]+\s*#endregion";
    var match = Regex.Match(template, pattern, RegexOptions.Singleline, TimeSpan.FromMinutes(1));
    if (!match.Success) {
      return $"// ERROR: Snippet region '{regionName}' not found in t";
    }
    var indentation = match.Groups[1].Value.Replace("\r", "").Replace("\n", "");
    return TemplateUtilities.RemoveIndentation(match.Groups[2].Value, indentation);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryRegionOfEveryEmbeddedTemplate_ExtractsExactlyAsTheRegexDidAsync() {
    var compared = 0;
    foreach (var resource in _generatorsAssembly.GetManifestResourceNames().Where(r => r.EndsWith(".cs", StringComparison.Ordinal))) {
      await using var stream = _generatorsAssembly.GetManifestResourceStream(resource)!;
      using var reader = new StreamReader(stream);
      var template = await reader.ReadToEndAsync();
      var names = Regex.Matches(template, @"#region\s+(\w+)", RegexOptions.None, TimeSpan.FromMinutes(1))
        .Select(m => m.Groups[1].Value)
        .Distinct(StringComparer.Ordinal);
      foreach (var name in names) {
        await Assert.That(TemplateUtilities.ExtractSnippetFrom(template, name, "t")).IsEqualTo(_regexExtract(template, name))
          .Because($"region {name} of {resource} must extract as it did before");
        compared++;
      }
    }
    await Assert.That(compared).IsGreaterThan(100)
      .Because("the comparison has to run over the real templates, not an empty set");
  }

  [Test]
  [Arguments("  #region A\n  one\n    two\n  #endregion\n")]
  [Arguments("x\n\n  \t#region A trailing text\r\n\r\n  one\r\n  #endregion")]
  [Arguments("#region A\n\n#endregion")]
  [Arguments("#region A\none #endregion\ntwo\n#endregion")]
  [Arguments("#region AB\nwrong\n#endregion\n#region A\nright\n#endregion")]
  [Arguments("#regionA\nno\n#endregion\n#region A\nyes\n#endregion")]
  [Arguments("#region A")]
  [Arguments("#region A\nnever ends")]
  [Arguments("#region B\nother\n#endregion")]
  [Arguments("#region\n A\nname on the next line\n#endregion")]
  [Arguments("")]
  public async Task EdgeCases_ExtractExactlyAsTheRegexDidAsync(string template) {
    await Assert.That(TemplateUtilities.ExtractSnippetFrom(template, "A", "t")).IsEqualTo(_regexExtract(template, "A"));
  }
}
