-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 555_ReceivedStreamsAreResequencedInTheirOriginsOrder.sql (#1629): the window, the lock helper and the
-- apply are dropped. Streams already re-sequenced stay in their origins' order.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_resequence_stream(UUID, UUID[], INTEGER[], INTEGER[], TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_try_lock_streams(UUID[]);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_received_stream_order_window(UUID, INTEGER);
