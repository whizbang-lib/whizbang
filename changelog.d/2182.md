---
category: Fixed
subsection: Audit
---
- **Stream-integrity ledger rows recorded under an origin that does not own the event type are forgotten
  (#ISSUE).** Before comparisons ignored a non-owner's digests, a service that held events it had only received
  reported them as its own, and consumers recorded deficit rows under that service for another service's types and
  for their own. Those rows were never compared again, because an origin's answer leaves out every type it does not
  own, so they could neither heal nor clear; with automatic repair on they were marked unhealable, and they held
  `whizbang.stream_integrity.unhealed_buckets` up although the consumer held exactly the events the owner reports.
  The integrity audit now forgets them each cycle by the same ownership rule the comparison uses, and that rule now
  also treats a type in one of the service's own owned domains as no other origin's to report. Rows already on a
  ledger go on the first audit cycle after upgrade; nothing to configure.
