---
category: Fixed
subsection: Audit
---
- **A type proven complete forgets the per-stream ledger rows of it too (#ISSUE).** A type-level comparison that proves
  a (tenant, type) complete over its whole history (the same proof that heals the type's own row since #2207) now also
  deletes every per-stream integrity ledger row of that origin, tenant and type, which equal roll-ups prove are
  phantoms, and records each on `whizbang.stream_integrity.bucket_heal_seconds`. Such rows kept
  `whizbang.stream_integrity.unhealed_buckets` up and kept the paced drain asking origins for events already held. A
  type with a stream genuinely missing has a roll-up that disagrees, so none of its rows is removed.
