-- Whizbang Perspective Schema Snippets
-- Reusable SQL DDL blocks for perspective table generation

#region CREATE_TABLE_SNIPPET
-- Perspective: __CLASS_NAME__
-- Estimated size: ~__ESTIMATED_SIZE__ bytes
CREATE TABLE IF NOT EXISTS __TABLE_NAME__ (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  model_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  version BIGINT NOT NULL DEFAULT 0
);
#endregion

#region CREATE_INDEXES_SNIPPET
-- Indexes for __TABLE_NAME__
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___updated_at ON __TABLE_NAME__(updated_at);
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___metadata_gin ON __TABLE_NAME__ USING GIN (metadata jsonb_path_ops);

-- Retrofit any historical broken tenant index. The old snippet keyed on
-- 'tenant_id' but PerspectiveScope serializes TenantId as 't', so the index
-- never matched any row and queries fell back to seq_scan. Drop only when
-- the indexdef shows the broken expression so we don't disturb a correctly-
-- created same-named index on later startups.
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM pg_indexes
    WHERE schemaname = current_schema()
      AND indexname = 'ix___TABLE_NAME___tenant'
      AND indexdef LIKE '%scope->>''tenant_id''%'
  ) THEN
    EXECUTE 'DROP INDEX IF EXISTS ix___TABLE_NAME___tenant';
  END IF;
END $$;

-- Scope filter indexes: keys match PerspectiveScope's [JsonPropertyName] short
-- form (t/u/o/c). EFCoreFilterableLensQuery emits WHERE scope->>'t' = ? etc.,
-- and these btree functional indexes are what makes the planner pick index
-- scan instead of seq_scan on large perspective tables.
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___tenant ON __TABLE_NAME__((scope->>'t')) WHERE scope IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___user ON __TABLE_NAME__((scope->>'u')) WHERE scope IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___organization ON __TABLE_NAME__((scope->>'o')) WHERE scope IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___customer ON __TABLE_NAME__((scope->>'c')) WHERE scope IS NOT NULL;
#endregion
