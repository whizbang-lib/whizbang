// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// Marks an assembly compiled with the Whizbang generators, so an assembly compiled against it can record it as one of
/// the generated assemblies it is built on (#2243). Emitted by the generator; never applied by hand.
/// </summary>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class WhizbangServiceAssemblyAttribute : Attribute;

/// <summary>
/// The build-time facts of every generated assembly loaded in the process, recorded by each assembly's generated
/// module initializer: what <see cref="ServiceAssemblyScope"/> decides a host's service from (#2243).
/// </summary>
/// <remarks>
/// Facts are names and a flag, so recording and reading them needs no reflection. An assembly that records its facts
/// again replaces its earlier record.
/// </remarks>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/ServiceScope/WhizbangServiceAssembliesTests.cs</tests>
public static class WhizbangServiceAssemblies {
  private static readonly Lock _lock = new();
  private static readonly Dictionary<string, ServiceAssemblyFacts> _facts = new(StringComparer.Ordinal);

  /// <summary>Records one generated assembly's facts. Called from generated code.</summary>
  /// <param name="assembly">The assembly's name.</param>
  /// <param name="isServiceRoot">Whether it is an executable or declares a <c>[WhizbangDbContext]</c>.</param>
  /// <param name="references">The generated assemblies it was compiled against.</param>
  public static void Register(string assembly, bool isServiceRoot, string[] references) {
    ArgumentNullException.ThrowIfNull(assembly);
    ArgumentNullException.ThrowIfNull(references);
    lock (_lock) {
      _facts[assembly] = new ServiceAssemblyFacts(assembly, isServiceRoot, [.. references]);
    }
  }

  /// <summary>Every assembly's facts recorded so far, by name.</summary>
  public static IReadOnlyDictionary<string, ServiceAssemblyFacts> Snapshot() {
    lock (_lock) {
      return new Dictionary<string, ServiceAssemblyFacts>(_facts, StringComparer.Ordinal);
    }
  }

  /// <summary>Replaces every recorded fact for a test, returning a handle that puts the previous facts back.</summary>
  internal static IDisposable ReplaceForTesting(params ServiceAssemblyFacts[] facts) {
    lock (_lock) {
      var saved = _facts.Values.ToArray();
      _facts.Clear();
      foreach (var fact in facts) {
        _facts[fact.Assembly] = fact;
      }
      return new Restore(saved);
    }
  }

  private sealed class Restore(ServiceAssemblyFacts[] saved) : IDisposable {
    public void Dispose() {
      lock (_lock) {
        _facts.Clear();
        foreach (var fact in saved) {
          _facts[fact.Assembly] = fact;
        }
      }
    }
  }
}
