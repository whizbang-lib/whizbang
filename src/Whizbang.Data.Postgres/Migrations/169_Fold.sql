-- Migration: 169_Fold
-- Date: 2026-09-25
-- Description: The folding the framework applies to text for substring search: lowercase, and typographic
--   variants of quotes, dashes and spaces mapped to their plain ASCII form. A search field is indexed on
--   wh_fold(value) and queried with wh_fold(value) LIKE wh_fold_pattern(term), so the stored value and the
--   term are folded by the same function and cannot drift apart, and a trigram index on the folded value
--   serves the match. IMMUTABLE and SQL-bodied: PostgreSQL only builds an index over an immutable
--   expression, and the planner inlines a SQL body so the index expression is matched.
-- Dependencies: none
-- Objects: wh_fold, wh_fold_pattern
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ONE overload each: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_fold');
SELECT __SCHEMA__.drop_all_overloads('wh_fold_pattern');

-- Single quotes, primes (5), double quotes, double primes (5), hyphens and dashes (7), then the no-break
-- spaces (3), each mapped to its ASCII counterpart position for position.
-- <docs>fundamentals/perspectives/physical-fields#search</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/FoldFunctionTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/SearchQueryIntegrationTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fold(p_value TEXT) RETURNS TEXT AS $$
  SELECT translate(
    lower(p_value),
    U&'\2018\2019\201A\201B\2032\201C\201D\201E\201F\2033\2010\2011\2012\2013\2014\2015\2212\00A0\202F\2007',
    repeat('''', 5) || repeat('"', 5) || repeat('-', 7) || repeat(' ', 3));
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.wh_fold(TEXT) IS
  'Folds text for substring search: lowercase, and curly quotes, primes, dashes and no-break spaces mapped '
  'to plain ASCII. Used in both the trigram index expression and the query, so value and term fold alike.';

-- A contains-pattern for LIKE: the folded term with LIKE''s own wildcards escaped so they match literally,
-- wrapped in %. Backslash is LIKE''s default escape character.
-- <docs>fundamentals/perspectives/physical-fields#search</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/FoldFunctionTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/SearchQueryIntegrationTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fold_pattern(p_term TEXT) RETURNS TEXT AS $$
  SELECT '%' || replace(replace(replace(__SCHEMA__.wh_fold(p_term), '\', '\\'), '%', '\%'), '_', '\_') || '%';
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.wh_fold_pattern(TEXT) IS
  'The LIKE pattern that finds a term anywhere in a wh_fold-ed value: the folded term with %, _ and \ escaped, '
  'wrapped in %.';
