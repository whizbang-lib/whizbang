// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="InboxDeserializeCache"/>. Slice 15 of plans/pump-then-process.md.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class InboxDeserializeCacheOptions {
  /// <summary>
  /// Killswitch — set to <c>false</c> to disable the deserialize cache (every dispatch
  /// re-deserializes from JSON, including the four lifecycle stages of a single message).
  /// Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Time-to-live for an entry. Default 2 minutes — covers transport redelivery and lease
  /// re-claim cycles for a typical busy service. Longer TTL = more memory, fewer re-parses;
  /// shorter TTL = the inverse.
  /// </summary>
  public int TtlMinutes { get; set; } = 2;

  /// <summary>
  /// Hard cap on resident entries. Default 10k — when exceeded, the oldest ~10% evict on
  /// the next insert. At a 2-min TTL the cap is generally moot unless sustained throughput
  /// exceeds ~80 messages/sec for the entire TTL window.
  /// </summary>
  public int MaxEntries { get; set; } = 10_000;

  /// <summary>
  /// Byte budget across resident entries, measured as the UTF-8 JSON each payload was parsed from. Default 64 MiB.
  /// </summary>
  /// <remarks>
  /// The entry cap alone let memory scale with message size: re-delivery bundles carry up to 192 KB each, so 10,000
  /// of them could hold about 1.9 GB (#1793). Past the budget the oldest entries are evicted, and a payload larger
  /// than the whole budget is not cached at all. The cache is a performance aid; an evicted entry is parsed again.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/configuration-reference</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDeserializeCacheByteBudgetTests.cs</tests>
  public long MaxBytes { get; set; } = InboxDeserializeCache.DEFAULT_MAX_BYTES;
}
