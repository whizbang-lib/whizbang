// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>What one generated assembly records about itself at build time (#2243).</summary>
/// <param name="Assembly">The assembly's name.</param>
/// <param name="IsServiceRoot">
/// Whether the assembly is a service root: an executable, or an assembly that declares a <c>[WhizbangDbContext]</c>.
/// </param>
/// <param name="References">The generated assemblies it was compiled against, by name.</param>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
public sealed record ServiceAssemblyFacts(string Assembly, bool IsServiceRoot, IReadOnlyList<string> References);

/// <summary>
/// Which generated assemblies make up a host's service, so a host gets its own service's generated registrations and
/// not those of every service loaded in the process (#2243).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule: a host excludes another service's assemblies, and only those.</strong> Every generated assembly
/// records at build time whether it is a <em>service root</em> (an executable, or an assembly that declares a
/// <c>[WhizbangDbContext]</c>) and which generated assemblies it was compiled against
/// (<see cref="ServiceAssemblyFacts"/>). A host <em>anchors</em> on the assembly of each DbContext it registers
/// (<c>WithEFCore&lt;TDbContext&gt;()</c>), or on an assembly it names itself
/// (<see cref="WhizbangServiceScopeExtensions.AddWhizbangServiceAnchor{TMarker}"/>). Then:
/// </para>
/// <list type="bullet">
/// <item>The host's own assemblies are every anchor, everything an anchor is built on, and every root built on an
/// anchor (the executable above a data assembly, a composition root, a test assembly).</item>
/// <item>Another service is a root that is not the host's own; it and everything it is built on are excluded, unless
/// they are also the host's own (a shared contracts assembly).</item>
/// <item>Everything else stays: a helper assembly no other service is built on, an assembly that recorded no facts,
/// a registration with no assembly name.</item>
/// </list>
/// <para>
/// In a process that holds one service nothing is ever excluded, wherever its DbContext lives, so a split service
/// keeps every assembly (#2146, #2191-#2201). A process that builds a host per service (an integration-test process,
/// a modular monolith) gives each host its own. A host that anchors on nothing, or on an assembly that recorded no
/// facts, is unscoped and keeps every assembly, as before.
/// </para>
/// <para>
/// Why the DbContext and not the assembly that calls <c>AddWhizbang()</c>: a test process composes every host from
/// the one test assembly, which references every service, so the caller cannot tell the services apart; the DbContext
/// a host registers always names its service. Why the rule excludes rather than includes: a single-service process
/// must never lose an assembly, and only another service's root can say an assembly is someone else's.
/// </para>
/// <para>Everything here is computed from names recorded at build time: no reflection.</para>
/// </remarks>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceAssemblyScopeTests.cs</tests>
public sealed class ServiceAssemblyScope {
  private readonly HashSet<string> _foreign;

  // #2283: a scope computed from the process's recorded facts classifies an assembly whose facts arrive later by the
  // same rule; _known holds the assemblies classified when the scope was computed, whose classification never changes.
  private readonly bool _followsLateAssemblies;
  private readonly HashSet<string> _known;
  private LateAssemblies? _late;

  private ServiceAssemblyScope(
      IReadOnlyList<string> anchors, HashSet<string> foreign,
      IReadOnlyList<string> included, IReadOnlyList<string> excluded, string? unscopedReason,
      bool followsLateAssemblies = false) {
    Anchors = anchors;
    _foreign = foreign;
    Included = included;
    Excluded = excluded;
    UnscopedReason = unscopedReason;
    _followsLateAssemblies = followsLateAssemblies && unscopedReason is null;
    _known = new HashSet<string>(included.Concat(excluded), StringComparer.Ordinal);
  }

  /// <summary>The scope of a host that names no service: every assembly is included.</summary>
  public static ServiceAssemblyScope Unscoped { get; } = _unscoped([], [], "the host registers no DbContext and names no service");

  /// <summary>Whether the scope excludes anything: <see langword="false"/> for an unscoped host.</summary>
  public bool IsScoped => UnscopedReason is null;

  /// <summary>Why the host keeps every assembly, or <see langword="null"/> when it is scoped.</summary>
  public string? UnscopedReason { get; }

  /// <summary>The assemblies the host anchors on, sorted.</summary>
  public IReadOnlyList<string> Anchors { get; }

  /// <summary>The assemblies with recorded facts that the host includes, sorted.</summary>
  public IReadOnlyList<string> Included { get; }

  /// <summary>The assemblies with recorded facts that belong to another service, sorted.</summary>
  public IReadOnlyList<string> Excluded { get; }

  /// <summary>Computes the scope of a host anchored on <paramref name="anchors"/> from every assembly's facts.</summary>
  /// <param name="anchors">The assemblies the host anchors on.</param>
  /// <param name="facts">Every generated assembly's facts, by name.</param>
  public static ServiceAssemblyScope Compute(
      IEnumerable<string> anchors, IReadOnlyDictionary<string, ServiceAssemblyFacts> facts) =>
    _compute(anchors, facts, followsLateAssemblies: false);

  /// <summary>
  /// The scope of a host anchored on <paramref name="anchors"/>, from the facts recorded in the process so far; an
  /// assembly that records its facts later is classified by the same rule when it does (#2283), so a service whose code
  /// loads after the host was anchored is never counted as the host's own.
  /// </summary>
  /// <param name="anchors">The assemblies the host anchors on.</param>
  /// <tests>tests/Whizbang.Core.Tests/ServiceScope/LateLoadedServiceAssemblyTests.cs</tests>
  internal static ServiceAssemblyScope ComputeForProcess(IEnumerable<string> anchors) =>
    _compute(anchors, WhizbangServiceAssemblies.Snapshot(), followsLateAssemblies: true);

  private static ServiceAssemblyScope _compute(
      IEnumerable<string> anchors, IReadOnlyDictionary<string, ServiceAssemblyFacts> facts, bool followsLateAssemblies) {
    ArgumentNullException.ThrowIfNull(anchors);
    ArgumentNullException.ThrowIfNull(facts);
    var anchorList = anchors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var known = facts.Keys.Order(StringComparer.Ordinal).ToArray();
    if (anchorList.Length == 0) {
      return _unscoped(anchorList, known, "the host registers no DbContext and names no service");
    }
    var unknown = anchorList.Where(a => !facts.ContainsKey(a)).ToArray();
    if (unknown.Length > 0) {
      return _unscoped(anchorList, known,
        $"no build-time facts are recorded for {string.Join(", ", unknown)} (compiled without the generator, or not loaded yet)");
    }

    var closures = facts.Keys.ToDictionary(name => name, name => _closure(name, facts), StringComparer.Ordinal);
    var own = new HashSet<string>(StringComparer.Ordinal);
    foreach (var anchor in anchorList) {
      own.UnionWith(closures[anchor]);
    }
    var foreign = new HashSet<string>(StringComparer.Ordinal);
    foreach (var root in facts.Values.Where(f => f.IsServiceRoot).Select(f => f.Assembly)) {
      if (anchorList.Any(closures[root].Contains)) {
        own.Add(root);
      } else {
        foreign.UnionWith(closures[root]);
      }
    }
    foreign.ExceptWith(own);
    return new ServiceAssemblyScope(
      anchorList, foreign,
      [.. known.Where(name => !foreign.Contains(name))],
      [.. known.Where(foreign.Contains)],
      unscopedReason: null,
      followsLateAssemblies);
  }

  /// <summary>
  /// The scope of the host <paramref name="services"/> describes: anchored on every anchor registered in it, from the
  /// facts recorded in the process so far.
  /// </summary>
  /// <param name="services">The host's service collection.</param>
  public static ServiceAssemblyScope For(IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    return ComputeForProcess(WhizbangServiceScopeExtensions.AnchorsIn(services));
  }

  /// <summary>
  /// Whether the host includes <paramref name="assembly"/>'s registrations. An assembly with no name, or one that
  /// recorded no facts, is always included: it is not known to be another service's.
  /// </summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <remarks>
  /// A host's scope classifies an assembly that recorded its facts after the scope was computed when it is first asked
  /// about (#2283), by the rule above: it is excluded when it is another service's root or only another service is built
  /// on it, and included when the host is built on it or nothing says it is another's. An assembly classified when the
  /// scope was computed keeps its classification, so registrations already made under it stay consistent.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/ServiceScope/LateLoadedServiceAssemblyTests.cs</tests>
  public bool Includes(string? assembly) => assembly is null || !(_foreign.Contains(assembly) || _isLateForeign(assembly));

  /// <inheritdoc/>
  public override string ToString() => IsScoped
    ? $"anchored on [{string.Join(", ", Anchors)}]; included [{string.Join(", ", Included)}]; excluded [{string.Join(", ", Excluded)}]"
    : $"unscoped ({UnscopedReason}); included [{string.Join(", ", Included)}]";

  private bool _isLateForeign(string assembly) {
    if (!_followsLateAssemblies || _known.Contains(assembly)) {
      return false;
    }
    // Recomputed once per change to the recorded facts, never per lookup.
    var version = WhizbangServiceAssemblies.Version;
    var late = Volatile.Read(ref _late);
    if (late is null || late.Version != version) {
      late = new LateAssemblies(version, _compute(Anchors, WhizbangServiceAssemblies.Snapshot(), followsLateAssemblies: false)._foreign);
      Volatile.Write(ref _late, late);
    }
    return late.Foreign.Contains(assembly);
  }

  // Which assemblies the rule excludes over the facts recorded at one version of the process's record.
  private sealed record LateAssemblies(int Version, HashSet<string> Foreign);

  private static ServiceAssemblyScope _unscoped(string[] anchors, string[] known, string reason) =>
    new(anchors, [], known, [], reason);

  private static HashSet<string> _closure(string start, IReadOnlyDictionary<string, ServiceAssemblyFacts> facts) {
    var seen = new HashSet<string>(StringComparer.Ordinal) { start };
    var pending = new Stack<string>();
    pending.Push(start);
    while (pending.Count > 0) {
      if (!facts.TryGetValue(pending.Pop(), out var current)) {
        continue;
      }
      foreach (var reference in current.References.Where(seen.Add)) {
        pending.Push(reference);
      }
    }
    return seen;
  }
}
