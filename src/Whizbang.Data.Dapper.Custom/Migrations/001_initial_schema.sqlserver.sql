-- Whizbang Messaging Infrastructure - SQL Server Migration
-- Version: 001
-- Description: Initial schema for inbox, outbox, request/response store, event store, and sequences

-- Inbox table for message deduplication (ExactlyOnce receiving)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[whizbang_inbox]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[whizbang_inbox] (
        message_id UNIQUEIDENTIFIER PRIMARY KEY,
        handler_name NVARCHAR(500) NOT NULL,
        processed_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE INDEX ix_whizbang_inbox_processed_at ON [dbo].[whizbang_inbox](processed_at);
END
GO

-- Outbox table for transactional outbox pattern (ExactlyOnce sending)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[whizbang_outbox]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[whizbang_outbox] (
        message_id UNIQUEIDENTIFIER PRIMARY KEY,
        destination NVARCHAR(500) NOT NULL,
        payload VARBINARY(MAX) NOT NULL,
        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        published_at DATETIMEOFFSET NULL
    );

    CREATE INDEX ix_whizbang_outbox_published_at ON [dbo].[whizbang_outbox](published_at) WHERE published_at IS NULL;
END
GO

-- Request/Response store for request-response pattern on pub/sub transports
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[whizbang_request_response]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[whizbang_request_response] (
        correlation_id UNIQUEIDENTIFIER PRIMARY KEY,
        request_id UNIQUEIDENTIFIER NOT NULL,
        response_envelope NVARCHAR(MAX) NULL,
        expires_at DATETIMEOFFSET NOT NULL,
        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE INDEX ix_whizbang_request_response_expires_at ON [dbo].[whizbang_request_response](expires_at);
END
GO

-- Event store for streaming/replay capability
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[whizbang_event_store]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[whizbang_event_store] (
        id BIGINT IDENTITY(1,1) PRIMARY KEY,
        stream_key NVARCHAR(500) NOT NULL,
        sequence_number BIGINT NOT NULL,
        envelope NVARCHAR(MAX) NOT NULL,
        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT uq_whizbang_event_store_stream_sequence UNIQUE (stream_key, sequence_number)
    );

    CREATE INDEX ix_whizbang_event_store_stream_key ON [dbo].[whizbang_event_store](stream_key, sequence_number);
END
GO

-- Sequence provider for monotonic sequence generation
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[whizbang_sequences]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[whizbang_sequences] (
        sequence_key NVARCHAR(500) PRIMARY KEY,
        current_value BIGINT NOT NULL DEFAULT 0,
        last_updated_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );
END
GO
