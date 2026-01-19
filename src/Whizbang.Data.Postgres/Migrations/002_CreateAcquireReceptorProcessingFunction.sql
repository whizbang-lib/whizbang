-- =============================================
-- Create acquire_receptor_processing_work function
-- Creates processing records when receptors need to process an event
-- =============================================

CREATE OR REPLACE FUNCTION __SCHEMA__.acquire_receptor_processing_work(
  p_event_id UUID,
  p_receptor_names TEXT[]
)
RETURNS TABLE (
  id UUID,
  receptor_name TEXT,
  status SMALLINT
)
LANGUAGE plpgsql
AS $$
DECLARE
  v_receptor_name TEXT;
  v_new_id UUID;
BEGIN
  -- For each receptor, create a processing record if it doesn't exist
  FOREACH v_receptor_name IN ARRAY p_receptor_names
  LOOP
    -- Generate a new UUIDv7 for this processing record
    v_new_id := gen_random_uuid();

    -- Insert processing record (ignore if already exists due to unique constraint)
    INSERT INTO wh_receptor_processing (
      id,
      event_id,
      receptor_name,
      status,
      attempts,
      started_at
    )
    VALUES (
      v_new_id,
      p_event_id,
      v_receptor_name,
      0,  -- Processing status
      1,  -- First attempt
      NOW()
    )
    ON CONFLICT (event_id, receptor_name) DO NOTHING;

    -- Return the processing record (either newly created or existing)
    RETURN QUERY
    SELECT
      rp.id,
      rp.receptor_name,
      rp.status
    FROM wh_receptor_processing rp
    WHERE rp.event_id = p_event_id
      AND rp.receptor_name = v_receptor_name;
  END LOOP;

  RETURN;
END;
$$;
