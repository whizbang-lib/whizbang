using Whizbang.Core.Perspectives;

namespace Whizbang.Testing;

/// <summary>
/// Test seam over the stream-group registry: generated module initializers register memberships
/// process-wide, so tests that populate the registry directly must be able to clear it without
/// widening the production surface.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
public static class StreamGroupRegistryTestSeam {
  /// <summary>Removes every registered membership.</summary>
  public static void Clear() => PerspectiveStreamGroupRegistry.Clear();
}
