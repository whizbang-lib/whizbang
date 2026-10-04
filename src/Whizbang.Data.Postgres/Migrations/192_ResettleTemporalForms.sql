-- Migration: 192_ResettleTemporalForms.sql
-- Date: 2026-10-04
-- Description: Let the stored-form pass look again at every perspective table (#1059).
--
--              Migration 153 records a table as settled once a pass at the microsecond form finds no
--              rendering left, so a later startup skips it without scanning. That pass only converted the
--              paths the rewrite knew about, and it did not know about a temporal that is a collection
--              element rather than a property of one: the mapped walk asked each property's own type,
--              which answers for the collection and not for what is in it. So a table holding such a
--              collection was scanned, found clean at the paths that were looked at, and settled with its
--              elements still renderings.
--
--              The reader accepts a rendering, so nothing failed and nothing reported it. What was lost is
--              the stored form: a rendering cannot be cast to int8, so those elements do not order or index
--              as numbers, which is the whole point of the canonical form.
--
--              The rewrite now derives that path, but a settled table is skipped before it is read, so the
--              fix alone would correct only rows written after it. Clearing settled_at lets the next
--              startup pass scan once more. temporal_form is deliberately untouched: the unit is already
--              correct for everything the earlier pass did convert, and re-running a form-2 pass over a
--              form-2 table converts renderings and leaves numbers alone.
--
--              Every table is cleared rather than the ones holding such a collection, because which tables
--              those are is a fact about the consumer's model types and not about the database, and this
--              file cannot see it. The cost is one extra pass over each perspective table, paid once; the
--              pass re-settles each table as it finds it clean.
-- Dependencies: 153 (wh_perspective_forms, settled_at)
-- Objects: wh_perspective_forms (data only)

-- <docs>fundamentals/perspectives/jsonb-containment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CanonicalTemporalRewriteTests.cs</tests>
UPDATE __SCHEMA__.wh_perspective_forms
   SET settled_at = NULL
 WHERE settled_at IS NOT NULL;
