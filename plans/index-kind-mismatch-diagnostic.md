# A declared index kind its field's type cannot carry

## The defect

`[Indexed(IndexKinds.Trigram)]` on anything but text is silently dropped.

`JsonIndexDiscovery.From` computes

```csharp
var trigram = (kind & KIND_TRIGRAM) != 0 && cast == JsonIndexCast.None;
```

`JsonIndexCast.None` is the text case, so for an `int` the cast is `Int4`, the trigram flag goes
false, and nothing objects. WHIZ303 does not fire either, because it asks only whether the type can
carry *an* index at all: `CastFor(int)` is non-null, so the analyzer returns before reporting.

When the author wrote only the trigram kind, the entry reaches `JsonIndexInfo` with
`Btree: false, Trigram: false`, and `CreateStatements` yields no statement for it. So the
declaration produces **no index and no diagnostic**. The author asked for something, and the answer
was silence.

The XML documentation on `IndexKinds.Trigram` already promises the opposite:

> Only meaningful for a string field, and reported as a diagnostic rather than silently ignored when
> asked for on anything else.

That sentence is false as written. Either the behavior or the sentence has to change, and the
behavior is the one worth having: a declaration that reads as a claim and does nothing is the exact
failure this whole surface exists to remove, arrived at from a third direction.

## Shape

A new diagnostic rather than an extension of WHIZ303, because it answers a different question.
WHIZ303 is "no index of any kind can be built over this field's stored form". This one is "an index
can be built, but not the kind you asked for". Merging them would make one message cover two fixes:
there, remove the declaration or promote the field; here, change the kind.

Reported when a declared kind cannot apply to the field's stored form. Today that is exactly
trigram on a non-text field. The rule is worth stating generally rather than as a special case, so
that a kind added later inherits it: each kind knows which casts it applies to, and a declaration
naming a kind outside that set is reported.

The message should name the kind, the field and what to write instead, because the fix is almost
always a one-word edit (`Trigram` to `Btree`), and an advisory that says only "this is wrong" makes
the reader go looking.

## What this is not

**Not a type-based default.** It is tempting to have `[Indexed]` infer the kind from the property
type, and it would be wrong for the one type where the choice is live. A string ordered on or
compared for equality wants a btree; the same string matched with `Contains`, `StartsWith` or
`EndsWith` wants a trigram; a string used both ways wants both. The type cannot distinguish those
because the *usage* does, and the usage is what the analyzer can already see.

That is why the current arrangement is right: `[Indexed]` means btree, and `_declaredIndexServes`
keeps WHIZ302 reporting until the kind the author declared actually serves the filter they wrote. The
advisory walks them to the kind. A type-based default would replace that guidance with a guess, and
guess wrong precisely where it matters.

So the gap is the rejection, not the default. Nothing should infer a kind; something should refuse a
kind that cannot work.

## Coverage note

Both halves need a test, and the second is the one that would rot: a fixture declaring trigram on a
non-text field asserts the diagnostic, and a fixture declaring it on text asserts silence *and* that
a trigram statement is actually emitted. Without the second, a change that made the diagnostic fire
for every trigram declaration would pass.

## Built as WHIZ305

`JsonIndexDeclarationAnalyzer.DeclaredCapabilityDoesNotApply`. Both halves are covered, split across
two suites on purpose: `JsonIndexDeclarationAnalyzerTests.ACapabilityThatApplies_IsNotReportedAsync`
asserts the silence, and the emission it has to be silent *about* is asserted by
`JsonIndexGenerationTests.ATrigramIndexUsesGinWithTheTrigramOperatorClassAsync` and
`AFieldComparedBothWaysGetsAnIndexForEachAsync`. The pair was checked by mutation: inverting the text
guard fails eight tests across both halves.

Two details the plan did not anticipate.

It covers **case folding as well as substring matching**, because the same argument applies to it
word for word: folding is a property of text, was dropped in silence for anything else, and the fix
is one word. One message names both when both are asked for, since the edit is the same.

It is **reported after WHIZ303 and never alongside it**. A field whose type can carry no index at all
has a different fix, promote or remove, and offering a capability to change as well would suggest an
edit that leaves the field still unindexable. The larger problem is the one that speaks.

The type-based default is still rejected, for the reason recorded above, and the mismatch diagnostic
is what makes rejecting it safe: nothing infers a kind, but a kind that cannot work is now refused
out loud instead of dropped.
