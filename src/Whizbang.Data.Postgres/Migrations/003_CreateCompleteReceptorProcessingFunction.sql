-- =============================================
-- Create complete_receptor_processing_work function
-- Updates processing status when a receptor completes (or fails) processing an event
-- =============================================

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_receptor_processing_work(
  p_event_id UUID,
  p_receptor_name TEXT,
  p_status SMALLINT,
  p_error TEXT DEFAULT NULL
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_rows_updated INT;
BEGIN
  -- Update the processing record
  UPDATE wh_receptor_processing
  SET
    status = p_status,
    processed_at = NOW(),
    error = p_error
  WHERE event_id = p_event_id
    AND receptor_name = p_receptor_name;

  GET DIAGNOSTICS v_rows_updated = ROW_COUNT;

  -- Return true if a row was updated, false otherwise
  RETURN v_rows_updated > 0;
END;
$$;
