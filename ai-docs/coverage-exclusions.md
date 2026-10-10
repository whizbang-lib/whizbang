# Coverage exclusions: when a line is a gap, and when it is a decision

Read this before adding `[ExcludeFromCodeCoverage]`, and when the quality gate lists a new line you
cannot see how to reach.

The target on new code is a **literal 100%**, of branches as well as lines: a fully executed line can
still carry a condition nobody took. The PR gate counts both: uncovered new lines, and uncovered new
hand-written branches (see "What 100% of branches means" below).

There is no band of "close enough" to settle into. Every line is either covered by a test or
deliberately outside the measurement for a reason someone wrote down, and telling those two apart is
the whole subject of this file: an uncovered line is a **question** before it is a verdict, and the
answer is usually a missing test rather than a line that deserves an exemption.

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

## What 100% of branches means

The target is every **hand-written** decision, as `plans/mcdc-coverage-program.md` puts it ("hand-written
code only"). The PR gate (`scripts/Find-UncoveredNewLines.ps1`) applies it to every line a branch adds:

- **Uncovered new line**: the line never ran.
- **Uncovered new branch**: the line ran, the collector recorded conditions on it, not every outcome
  was taken, **and** the statement that starts on the line contains a decision construct on any of its
  lines: `if` / `else if`, the conditional `?:`, `??`, `??=`, `?.` or `?[`, `&&`, `||`, `switch` /
  `case` / a switch-expression arm, `when`, `catch`, a `while` / `for` / `foreach` condition, or an `is`
  pattern test. Constructs inside string literals and comments do not count; constructs inside
  interpolation holes do.

Conditions on a statement with none of those constructs are **compiler-generated** and the gate does not
count them: the state-machine branches behind a bare `await`, the null checks of an object or collection
initializer, and similar. No test can target them as such, and covering the code around them already
proves what the author wrote. Counting them would make 100% unreachable for reasons that say nothing
about the code. The line is still measured: if it never runs it is an uncovered line like any other.

Two consequences worth knowing:

- A defensive null guard is a hand-written decision. Write `ArgumentNullException.ThrowIfNull(x)` in a
  statement, or `ArgumentGuard.NotNull(x)` in a Core field or property initializer, rather than
  `x ?? throw new ArgumentNullException(nameof(x))`. The exception is the same and the branch lives in
  the runtime, so it needs no per-constructor test.
- The rule reads the whole statement. The collector reports every condition of a statement on the
  statement's first line, so a decision on a continuation line (a `?.` starting the second line of a
  call chain, a `??` inside an object initializer, a `switch` expression in an argument) is counted on a
  line whose own text has none. Reading only that line once classified such a decision as
  compiler-generated and hid it (#1305). The statement runs to the `;` that ends it, or to the `{` that
  opens its block; an initializer's braces are part of it, and so is a `?.` split at the line break
  (`x?` then `.Member`). A lambda's body is a function of its own whose conditions the collector reports
  on the lambda's own lines, so a block body, and an expression body that starts on a continuation
  line, are not part of the statement. Reading stops at a multi-line string or block comment, whose text
  a per-line reader would take for code.

The rule is tested in `.github/scripts/tests/Find-UncoveredNewLines.Tests.ps1`. Change both together.
Those tests never read a stored coverage report: they build the small libraries under
`.github/scripts/tests/shapes/` and run each as two test processes under the real collector, with the
settings and versions CI uses (`New-ShapesCoverage.ps1`), so the gate is tested against what the
collector writes today. A new shape the gate must handle is added there as code, never as a report.
The collector cannot instrument on macOS, so run them on Linux (CI's "Test · Pipeline scripts" job does).

### Outcomes taken in different test processes

Every test process writes its own report, and a Cobertura line says how many outcomes of a decision
that process took, not which. Two processes that each took one outcome of an `if` look exactly like two
that took the same one, so the gate can never add them up. It keeps the best count any one process saw
(per condition, where the collector's detail says which condition is which), and recovers the rest from
the **binary** report (`*.coverage`) each process writes beside its Cobertura one. Those record every
block's hit and merge exactly (`dotnet-coverage merge`). The collector counts an outcome as taken when
the block it leads to ran, so once every block of a function ran in some process, every outcome in it
was taken, whichever process took it, and the gate counts those lines as covered. A decision whose two
outcomes run in two different suites is therefore covered as soon as the rest of its function is.

A function with a block no test ran does not end the proof. A statement that only branches within
itself (a `?.`, `??`, `?:`, `&&` or `||` with no `if`, loop, `switch`, `case`, `when`, `catch`,
`await`, `yield`, `using`, `try` or jump in it) sends each outcome either to a block of the statement or
to the code it falls through to. So when every block on the statement ran and the range after it in its
function ran too, every outcome was taken, whatever else in the function went untested. That is the case
of a `metrics?.Record(...)` whose null outcome ran in one suite and the other in another, inside a loop
with an unrelated untested branch (#1305). Each condition is checked against what the block data shows:

- a block on the statement that never ran (a `?.` no test took both ways) leaves it uncovered;
- an `if`, loop or `switch` sends its outcomes to blocks elsewhere in the function, and `await` adds the
  state machine's hidden blocks, so such a statement is never proven this way, even when every block on
  its own line ran;
- a statement whose following code never ran is not proven either: a `bomb?.Fail()` whose only path
  threw never reached the code its null outcome would have jumped to.

Anything else in a function with an unrun block keeps the reports' count: which of its outcomes is
missing cannot be told without the IL, so nothing is claimed.

One more piece of evidence is exact without the IL: an `if (...) {` whose body sits on lines of its
own. C# cannot jump into a block from outside it, so the body is entered only through the true
outcome: a report that ran any line inside the braces took true, and one that ran the decision with
one outcome and none of the body took false. Seen both ways across reports, the decision is covered.

The merged report is also what Sonar reads, so Sonar, the gate and the whole-library row agree.
Codecov still receives the per-process reports and merges them its own way.

### The whole-library gate

The PR comment's last row, **Whole library (gated)**, shows the coverage of all hand-written library
code on every PR, and the quality job fails while either half is not 100%: every line is run by some
test, and every hand-written decision is covered, not only the ones a PR adds, so the library stays at
100% once it is there and any gap a change uncovers (a refactor that splits a decision, a test removed)
is fixed in that change. Lines over everything under `src/` that is instrumented and not generated
(`src/Whizbang.Testing` is not instrumented; `*.g.cs`, `obj/` and the `.whizbang` cache are generated),
and outcomes of hand-written decisions by the same rule as above. For example, "Whole library: lines
99.9% (17 lines never run), hand-written branches 98.6% (382 outcomes untested)", and once nothing is
left, "Whole library: lines 100%, hand-written branches 100%, every line and every hand-written decision
in the library is covered". Percentages are truncated, so a gap never reads as 100%, and the line count
is printed beside the percentage because a library this size truncates 17 uncovered lines and 80 to the
same figure.

**A line carrying no decision still gates.** It is tempting to read the line half as a weaker signal
than the branch half, because an auto-property or a record's positional parameter has no logic in it.
What it has is a contract: a documented default, a value the record promises to carry, the body of a
default interface method a consumer reaches when it supplies no implementation of its own. An accessor
no test ever runs is a promise nothing holds anyone to, and the test that pins it is a claim about the
documented behavior rather than about the number. Assert the **documented** value, not a round-trip:
"set it and read it back" would pass for any auto-property and so proves nothing. A member excluded with
`[ExcludeFromCodeCoverage]` is absent from the reports, so it is counted by neither half. That makes every exclusion
a decision someone has to defend: follow the procedure above, put the reason in the attribute's
`Justification` and a comment, and, when the reason is an open issue, name the issue so the exclusion
goes with its fix. The list of what is left is `library-gap.txt` in the `pr-quality-gate` artifact
(`-LibraryGapOutFile`), and `pwsh scripts/Find-UncoveredNewLines.ps1 -FailOnWholeLibrary` runs the same
gate locally. `/pr-health` prints the same line and reports a gap as a failure.

---

## Reading the gate's list

Two things about the framework's own quality gate are worth knowing before acting on it.

It is **stricter than SonarCloud's**, counting its own uncovered-new-lines list plus every open
finding, informational ones included. SonarCloud's dashboard can therefore read clean while the gate
fails, and that is not a contradiction.

SonarCloud's coverage condition is also **not the standard**. The project is on the built-in "Sonar
way" gate, which fails new code below 80% and cannot be edited, so its verdict passes work this
repository does not accept. Read it as a second opinion; the number to satisfy is 100%, and the
uncovered-new-lines list plus the branch data behind it are what say whether you have.

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
