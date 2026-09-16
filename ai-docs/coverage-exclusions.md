# Coverage exclusions: when a line is a gap, and when it is a decision

Read this before adding `[ExcludeFromCodeCoverage]`, and when the quality gate lists a new line you
cannot see how to reach.

The target on new lines is near-100%, not literal 100%. That difference is the whole subject of this
file: an uncovered line is a **question** before it is a verdict, and the answer is usually a missing
test rather than a line that deserves an exemption.

---

## The order to try

Work down this list. Most lines are answered by the first step, and reaching the third should feel
like a surprise.

### 1. Reach the line with a real test

Ask what state the code would have to be in for that line to run, and whether that state is one the
framework can actually be in. A line that looks unreachable is usually reachable through a collaborator
answering differently: a store that refuses, a server that returns a state nobody planned for, an empty
collection, a second caller arriving first. Those are the tests worth having anyway, because each one
is a claim about behavior rather than a claim about coverage.

If the line is one arm of a classification, the other arm is almost always the valuable test. A search
that finds nothing, an aggregate carrying only faults the framework did not cause, a predicate that
says no: those decide what the caller does next, and getting them wrong is a real defect rather than a
coverage number.

### 2. If the line is unreachable through its public path, assert the contract directly

Some guards protect a call rather than a caller. A precondition inside a private helper can be
unreachable because every call site already guarantees it, and the guard is still right, because the
thing it protects is expensive or destructive.

When that happens, expose the **narrowest seam that already exists** and assert the contract itself.
Prefer a seam the file already uses for testing over inventing a new one.

The worked example, from the perspective worker's failed-batch recovery: the helper that hands a failed
batch's leases back opens with an empty-input guard. It cannot be reached through the consumer loop,
which skips any cycle carrying neither work nor a drain signal, so both recovery call sites always pass
a non-empty list. The guard is kept, because an empty release is still a round-trip to the database
that has just failed, and the recovery path runs precisely when the database is in trouble. So the
helper became `internal`, which is the seam that file already uses for its batch entry point, and a
test asserts the contract: asked to release nothing, it asks the store for nothing. Both the helper's
`<remarks>` and the test say why it is asserted that way rather than driven through the loop.

That is a better outcome than an exclusion in two ways. The contract is now pinned, so a later change
that makes the empty case reachable finds a test waiting for it, and nothing about the member's real
behavior stops being measured.

### 3. Only then, an exclusion, member-level and justified

`[ExcludeFromCodeCoverage]` is **member-level**. Applying it to a member that has any tested behavior
suppresses that behavior's coverage along with the line you were worried about, and the report then
reads as though the member were fine. That is why it is the last resort rather than the convenient one:
the cost is not the annotation, it is everything the annotation hides.

An exclusion that genuinely is right carries a comment saying **why the line cannot be reached**, in
terms of the invariant that makes it unreachable, so that a reader can tell whether the invariant still
holds. "Not covered" is not a reason. "The loop guarantees a non-empty batch before any of this runs"
is.

---

## What never to do

- **Never delete code to make a line disappear.** A defensive guard whose branch no test can reach is
  still the thing standing between a future call site and the failure it guards. Deleting it converts a
  coverage question into a latent defect, and the coverage report then looks better than the code is.
- **Never widen an exclusion beyond the one member in question.** No type-level or assembly-level
  exclusion for a single line. If the annotation would cover more than the member you reasoned about,
  it is the wrong tool.
- **Never assert something weaker so a line gets touched.** A test written to move a number, rather
  than to describe behavior, passes whether or not the behavior is right, which is the failure mode the
  AI-agent guide catalogs.
- **Never treat the gate's list as the definition of done.** The gate counts lines. A line can be
  covered by a test that proves nothing, and a member can be fully covered and still wrong.

---

## Reading the gate's list

Two things about the framework's own quality gate are worth knowing before acting on it.

It is **stricter than SonarCloud's**, counting its own uncovered-new-lines list plus every open
finding, informational ones included. SonarCloud's dashboard can therefore read clean while the gate
fails, and that is not a contradiction.

Its list is only meaningful once **every** test job has passed. A failed or canceled test job means
that job's coverage artifact never arrived, and every line the tests in it would have covered reads as
uncovered. A sudden list of dozens of lines in one file, all of them in a file whose tests live in the
shard that just failed, is that artifact and not a real gap. Fix the failing job first, then read the
list again.

---

## See also

- [tdd-strict.md](tdd-strict.md) - the cycle that produces covered lines as a side effect, and the
  coverage requirement in its own words
- [testing-tunit.md](testing-tunit.md) - assertion patterns, and pinning a limitation that justified a
  decision
- AI-agent guide (docs site: `contributors/ai-agent-guide`) - the shapes of test that pass without
  testing anything, and how to measure coverage truthfully
