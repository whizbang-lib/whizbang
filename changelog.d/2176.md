---
category: Fixed
subsection: Storage
---
- **A message discarded on receive is stored without its body.** A message the consumer skips on receive (no local
  consumer, directed at another service, an echo of this service's own event or command, an event of an unknown
  type) is still stored settled with its reason, so a redelivery of the same id is recognized as a duplicate, but
  its envelope and metadata are now stored empty (`'{}'`) instead of in full. Nothing read them, and on a
  subscriber that receives many types it does not consume they were most of the inbox's size until the
  completed-inbox purge removed them. The row keeps its id, handler, type, stream, scope, flags, priority, source
  and timestamps, and the reason in `wh_inbox_state.error`. Rows stored before the upgrade keep their bodies until
  the purge removes them. Migration 368; no schema change.
