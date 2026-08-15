using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>
/// FNV-1a 64-bit over the UTF-8 bytes of a string — the framework's process-stable string hash.
/// </summary>
/// <remarks>
/// Every advisory-lock key family derives from this rather than from <see cref="string.GetHashCode()"/>,
/// which .NET seeds randomly per process: two instances hashing the same name would compute different
/// keys, each acquire "the" lock, and the lock would exclude nothing. Postgres <c>hashtext</c> is
/// likewise avoided at the managed layer — it agrees across instances but its stability across server
/// versions is undocumented. Because the value is durable coordination state shared between instances
/// (and between library versions during a rolling deploy), <b>this algorithm must never change</b>.
/// </remarks>
internal static class Fnv1a64 {
  private const ulong OFFSET_BASIS = 14695981039346656037UL;
  private const ulong PRIME = 1099511628211UL;

  /// <summary>
  /// Hashes <paramref name="value"/> to a signed 64-bit key suitable for <c>pg_advisory_lock(bigint)</c>
  /// and friends, which accept the full signed range.
  /// </summary>
  internal static long Compute(string value) {
    var hash = OFFSET_BASIS;
    foreach (var b in Encoding.UTF8.GetBytes(value)) {
      hash ^= b;
      hash *= PRIME;
    }
    return unchecked((long)hash);
  }
}
