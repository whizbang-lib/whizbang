-- Migration 001: Create compute_partition function for consistent hashing
-- Date: 2025-12-07
-- Description: Helper function to compute partition number from stream_id using consistent hashing

CREATE OR REPLACE FUNCTION __SCHEMA__.compute_partition(p_stream_id UUID, p_partition_count INTEGER DEFAULT 10000)
RETURNS INTEGER AS $$
BEGIN
  -- Use hashtext on UUID string for consistent hashing
  -- Modulo to get partition number (0 to partition_count-1)
  -- Returns NULL if stream_id is NULL (IMMUTABLE functions cannot use random())
  IF p_stream_id IS NULL THEN
    RETURN NULL;
  END IF;

  RETURN (abs(hashtext(p_stream_id::TEXT)) % p_partition_count)::INTEGER;
END;
$$ LANGUAGE plpgsql IMMUTABLE;
