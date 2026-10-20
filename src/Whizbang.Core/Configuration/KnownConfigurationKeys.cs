// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace Whizbang.Core.Configuration;

/// <summary>
/// The configuration keys Whizbang reads, as recorded in the configuration key manifest
/// (<c>tests/Whizbang.Documentation.Tests/Baselines/configuration-keys.txt</c>), which is embedded in
/// Whizbang.Core at build time.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is recorded, not hand-written: <c>ConfigurationKeyManifestTests</c> registers every package
/// the way a host does, resolves every options type it configures, records each key the binders ask
/// for, and fails the build when the recording and the manifest differ. So the set here is exactly the
/// set the binders read, and it stays that way without anyone maintaining it. Reading it is a resource
/// stream and a text parse: no reflection over options types, safe under trimming and native AOT.
/// </para>
/// <para>
/// A <c>*</c> segment is a name the consumer chooses. Inside a key it stands for exactly one segment
/// (<c>Whizbang:CircuitBreakers:*:FailureThreshold</c>); at the end it stands for one or more, because a
/// dictionary or list entry may itself be an object (<c>Whizbang:PerspectiveRowRetention:Overrides:*</c>).
/// Comparison ignores case, as configuration does.
/// </para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed class KnownConfigurationKeys {
  /// <summary>The manifest's resource name inside Whizbang.Core (the <c>LogicalName</c> in the project file).</summary>
  internal const string RESOURCE_NAME = "Whizbang.Core.Configuration.configuration-keys.txt";

  private const string WILDCARD = "*";

  private static readonly Lazy<KnownConfigurationKeys> _library =
    new(() => FromResource(typeof(KnownConfigurationKeys).Assembly, RESOURCE_NAME));

  private readonly HashSet<string> _exact;
  private readonly string[][] _patterns;

  private KnownConfigurationKeys(HashSet<string> exact, string[][] patterns) {
    _exact = exact;
    _patterns = patterns;
  }

  /// <summary>Every key the library reads, parsed once per process from the embedded manifest.</summary>
  public static KnownConfigurationKeys Library => _library.Value;

  /// <summary>Reads a manifest embedded as a resource.</summary>
  /// <param name="assembly">The assembly holding the resource.</param>
  /// <param name="name">The resource name.</param>
  /// <returns>The keys the manifest lists.</returns>
  /// <exception cref="InvalidOperationException">The resource is not in the assembly.</exception>
  public static KnownConfigurationKeys FromResource(Assembly assembly, string name) {
    ArgumentNullException.ThrowIfNull(assembly);
    using var stream = assembly.GetManifestResourceStream(name)
      ?? throw new InvalidOperationException($"The configuration key manifest '{name}' is not embedded in the assembly.");
    using var reader = new StreamReader(stream);
    return Parse(reader.ReadToEnd());
  }

  /// <summary>Parses manifest text: one key per line; blank lines and lines starting with <c>#</c> are skipped.</summary>
  /// <param name="manifest">The manifest text.</param>
  /// <returns>The keys it lists.</returns>
  public static KnownConfigurationKeys Parse(string manifest) {
    ArgumentNullException.ThrowIfNull(manifest);
    var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var patterns = new List<string[]>();
    foreach (var raw in manifest.Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] == '#') {
        continue;
      }
      if (line.Contains(WILDCARD, StringComparison.Ordinal)) {
        patterns.Add(line.Split(':'));
      } else {
        exact.Add(line);
      }
    }
    return new KnownConfigurationKeys(exact, [.. patterns]);
  }

  /// <summary>Whether a configured key is one Whizbang reads.</summary>
  /// <param name="key">The full configuration path, segments separated by <c>:</c>.</param>
  /// <returns><see langword="true"/> when a binder reads it.</returns>
  public bool IsKnown(string key) {
    ArgumentNullException.ThrowIfNull(key);
    if (_exact.Contains(key)) {
      return true;
    }
    var segments = key.Split(':');
    return _patterns.Any(pattern => _matches(pattern, segments));
  }

  private static bool _matches(string[] pattern, string[] key) {
    var last = pattern.Length - 1;
    var trailing = pattern[last] == WILDCARD;
    // A trailing wildcard needs at least one segment in its place; any other pattern matches one for one.
    var fits = trailing ? key.Length > last : key.Length == pattern.Length;
    if (!fits) {
      return false;
    }
    var fixedSegments = trailing ? last : pattern.Length;
    for (var i = 0; i < fixedSegments; i++) {
      if (pattern[i] != WILDCARD && !string.Equals(pattern[i], key[i], StringComparison.OrdinalIgnoreCase)) {
        return false;
      }
    }
    return true;
  }
}
