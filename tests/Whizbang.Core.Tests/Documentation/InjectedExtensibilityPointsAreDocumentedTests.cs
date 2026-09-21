using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Documentation;

/// <summary>
/// Every interface the framework registers as a replaceable service is an extensibility point a
/// consumer may override, so each one must say what it does and where that is documented. The
/// set is computed from source rather than listed here: any interface that appears as the service
/// type of a TryAdd/Add registration under src/ is in scope, so a new seam cannot be added without
/// its documentation being checked.
/// </summary>
/// <remarks>
/// Two tests, deliberately split. The first asserts a &lt;docs&gt; tag is present and runs
/// everywhere. The second asserts the tag's TARGET page exists and can only run where the docs
/// repository is checked out beside this one, because the pages live in a separate repository;
/// it reports rather than fails when that checkout is absent, so CI still enforces the first half.
/// </remarks>
public class InjectedExtensibilityPointsAreDocumentedTests {
  private static readonly Regex _serviceType = new(
    @"(?:Try)?Add(?:Keyed)?(?:Singleton|Scoped|Transient)<(?:[A-Za-z_][\w.]*\.)?(I[A-Z]\w*)[,>]", RegexOptions.Compiled);
  private static readonly Regex _docsTag = new(@"<docs>([^<]+)</docs>", RegexOptions.Compiled);

  private static string _repoRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Whizbang.slnx not found above the test binary.");
  }

  /// <summary>Interface name -> declaring file, for every interface registered as a DI service under src/.</summary>
  private static Dictionary<string, string> _registeredInterfaces(string root) {
    var src = Path.Combine(root, "src");
    var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
               && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
               && !f.Contains(".whizbang")).ToList();
    var registered = new HashSet<string>(StringComparer.Ordinal);
    foreach (var f in files) {
      foreach (Match m in _serviceType.Matches(File.ReadAllText(f))) {
        registered.Add(m.Groups[1].Value);
      }
    }
    var declaring = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var f in files) {
      var text = File.ReadAllText(f);
      foreach (var name in registered) {
        if (Regex.IsMatch(text, $@"\binterface {Regex.Escape(name)}\b") && !declaring.ContainsKey(name)) {
          declaring[name] = f;
        }
      }
    }
    return declaring;
  }

  private static string? _docsTagFor(string file, string name) {
    var text = File.ReadAllText(file);
    var decl = Regex.Match(text, $@"((?:[ \t]*///[^\n]*\n)*)[ \t]*(?:public|internal) (?:partial )?interface {Regex.Escape(name)}\b");
    if (!decl.Success) {
      return null;
    }
    var tag = _docsTag.Match(decl.Groups[1].Value);
    return tag.Success ? tag.Groups[1].Value.Trim() : null;
  }

  [Test]
  public async Task EveryRegisteredInterface_CarriesADocsTagAsync() {
    var root = _repoRoot();
    var missing = _registeredInterfaces(root)
      .Where(kv => _docsTagFor(kv.Value, kv.Key) is null)
      .Select(kv => $"{kv.Key}  ({Path.GetRelativePath(root, kv.Value)})")
      .Order(StringComparer.Ordinal)
      .ToList();

    await Assert.That(missing).IsEmpty()
      .Because("an interface the framework registers as a replaceable service is an extensibility point; "
        + "a consumer deciding whether to override it needs to be told what it does and where to read more, "
        + "so its declaration must carry a <docs> tag");
  }

  [Test]
  public async Task EveryDocsTag_PointsAtAPageThatExistsAsync() {
    var root = _repoRoot();
    var docsRoot = Path.GetFullPath(Path.Combine(root, "..", "whizbang-lib.github.io", "src", "assets", "docs"));
    if (!Directory.Exists(docsRoot)) {
      Console.WriteLine($"docs repository not checked out at {docsRoot}; target existence not verified here");
      return;
    }
    // A tag is resolved against every top-level docs folder (versioned, drafts, proposals) rather than
    // a hard-coded list, because that is how the site's own map generator finds them.
    var bases = Directory.EnumerateDirectories(docsRoot).ToList();
    var dangling = new List<string>();
    foreach (var (name, file) in _registeredInterfaces(root).OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
      var tag = _docsTagFor(file, name);
      if (tag is null) {
        continue;   // the first test owns that failure
      }
      var page = tag.Split('#')[0].Trim('/') + ".md";
      // A tag names a page inside one of the docs folders ("fundamentals/events/events" lives under
      // v1.0.0/ or drafts/), or, for a proposal, the page itself ("pre-destruction-seam").
      if (!File.Exists(Path.Combine(docsRoot, page)) && !bases.Any(b => File.Exists(Path.Combine(b, page)))) {
        dangling.Add($"{name}: <docs>{tag}</docs>");
      }
    }

    await Assert.That(dangling).IsEmpty()
      .Because("a <docs> tag that points at a page nobody wrote sends a consumer to a 404 while the code "
        + "claims to be documented; the tag must name a page that exists under some docs folder");
  }
}
