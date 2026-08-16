using Microsoft.AspNetCore.Http;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// The mutable set of extra path prefixes exempt from the availability gate, beyond the ones the
/// host configured. Exists so a surface that must not share a failure domain with what it reports
/// on — the startup status endpoint above all — can register its own exemption when it is mapped,
/// instead of leaving that as a step the caller can forget.
/// </summary>
/// <remarks>
/// Registration happens at pipeline-build time (endpoint mapping); reads happen per request. The
/// snapshot-array swap keeps the read path allocation-free and lock-free.
/// </remarks>
/// <docs>resilience/database-availability-middleware</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/StartupStatusEndpointsTests.cs</tests>
public sealed class WhizbangAvailabilityExemptions {
  private readonly Lock _lock = new();
  private volatile PathString[] _paths = [];

  /// <summary>Adds a path prefix to the exempt set. Idempotent per prefix.</summary>
  public void Add(string pathPrefix) {
    ArgumentException.ThrowIfNullOrEmpty(pathPrefix);
    lock (_lock) {
      var candidate = new PathString(pathPrefix);
      foreach (var existing in _paths) {
        if (existing.Equals(candidate)) {
          return;
        }
      }
      _paths = [.. _paths, candidate];
    }
  }

  /// <summary>Whether <paramref name="path"/> falls under any registered exempt prefix.</summary>
  public bool IsExempt(PathString path) {
    foreach (var exempt in _paths) {
      if (path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }
    return false;
  }
}
