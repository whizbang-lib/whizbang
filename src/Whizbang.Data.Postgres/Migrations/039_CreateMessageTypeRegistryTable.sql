-- Migration 039: Create message_type_registry table
--
-- Universal catalog of every registered IMessage / IPerspectiveFor<> type with
-- optional [PinnedId] metadata. Backs the Stable Type Identity System.
-- Populated at startup by IMessageTypeRegistryPopulator; drift is detected &
-- reconciled by IEventTypeRenameTool.
--
-- Also emitted by PostgresSchemaBuilder.BuildInfrastructureSchema for fresh DBs,
-- but this migration guarantees the table (and its partial unique index on
-- pinned_id) exists on existing DBs whose hash-based schema reconciler would
-- otherwise skip re-running the infra DDL.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_message_type_registry (
    type_id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    clr_type_name VARCHAR(500) NOT NULL,
    pinned_id UUID NULL,
    kind VARCHAR(50) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_message_type_registry_clr_type_name UNIQUE (clr_type_name)
);

-- Partial unique index: enforce "no two types share a pinned_id" while allowing
-- many rows with null pinned_id (unpinned types).
CREATE UNIQUE INDEX IF NOT EXISTS ix_message_type_registry_pinned_id
    ON __SCHEMA__.wh_message_type_registry (pinned_id)
    WHERE pinned_id IS NOT NULL;
