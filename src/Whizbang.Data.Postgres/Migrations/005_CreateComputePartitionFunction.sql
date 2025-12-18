-- Migration 007: Create compute_partition function for consistent hashing
-- Date: 2025-12-07
-- Description: Helper function to compute partition number from stream_id using consistent hashing

CREATE OR REPLACE FUNCTION compute_partition(p_stream_id UUID, p_partition_count INTEGER DEFAULT 10000)
RETURNS INTEGER AS $$
BEGIN
  -- Use hashtext on UUID string for consistent hashing
  -- Modulo to get partition number (0 to partition_count-1)
  -- Handle NULL stream_id by using random partition (for non-event messages)
  IF p_stream_id IS NULL THEN
    RETURN floor(random() * p_partition_count)::INTEGER;
  END IF;

  RETURN (abs(hashtext(p_stream_id::TEXT)) % p_partition_count)::INTEGER;
END;
$$ LANGUAGE plpgsql IMMUTABLE;
