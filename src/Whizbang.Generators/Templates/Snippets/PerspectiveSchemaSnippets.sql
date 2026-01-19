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
CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___tenant ON __TABLE_NAME__((scope->>'tenant_id')) WHERE scope IS NOT NULL;
#endregion
