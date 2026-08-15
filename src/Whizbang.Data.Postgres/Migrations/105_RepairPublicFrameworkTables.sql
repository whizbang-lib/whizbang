-- Migration: 105_RepairPublicFrameworkTables
-- Date:      2026-08-14
-- Purpose:   Carry framework state out of `public` for deployments that were already running when
--            wh_log / wh_settings / wh_dead_letters / wh_dead_letter_summary were unqualified.
--
-- Why:       Those four were created bare, so they resolved through search_path and landed in
--            `public` rather than the service schema. Now that they are __SCHEMA__-qualified, a
--            non-public deployment gets a brand-new EMPTY table on the next startup while its real
--            rows sit in public — settings silently revert to defaults and the DLQ appears drained.
--            This migration moves the state across so the qualification is invisible to operators.
--
-- Shape:     Purely ADDITIVE. Every copy is INSERT ... ON CONFLICT DO NOTHING, so re-running is a
--            no-op and a value already set service-locally always wins over the inherited one. The
--            public copies are deliberately LEFT IN PLACE: on a shared database another service may
--            still be reading them, and dropping a table other software is using is not a decision a
--            startup migration gets to make. Pruning them is an operator step, once every service on
--            the database is past this migration.
--
-- Scope:     Only two of the four are copied.
--              wh_settings           — copied. Configuration must follow the service, or it silently
--                                      reverts to defaults (debug_mode, the retention knobs).
--              wh_dead_letters       — copied. Poison messages must follow the service, or recovery
--                                      flows lose sight of them. On a genuinely shared database this
--                                      duplicates rows into each service's schema, which preserves
--                                      exactly today's visibility (all services already see them
--                                      all); isolation begins for dead letters created after the cut.
--              wh_dead_letter_summary— NOT copied. It is a derived rollup that refresh_dead_letter_
--                                      summary() rebuilds from wh_dead_letters on the normal
--                                      maintenance cycle.
--              wh_log                — NOT copied. Diagnostics, potentially large, and nothing reads
--                                      it operationally. Copying risks a long transaction inside
--                                      startup for no functional gain; the history stays readable in
--                                      public.
--
-- Dependencies: 028 (wh_log, wh_settings), 050 (wh_dead_letters), 053 (wh_dead_letter_summary)

DO $$
DECLARE
  v_target_schema TEXT;
  v_cols          TEXT;
  v_copied        BIGINT := 0;
BEGIN
  -- Resolve the schema our own table actually lives in. Deliberately NOT current_schema(), which
  -- reports the CALLER's search_path and would say 'public' here regardless of where the table is.
  -- Going through regclass also absorbs the two runners' differing __SCHEMA__ substitution (Dapper
  -- injects the raw name, the EF Core generator injects the quoted form).
  SELECT n.nspname
    INTO v_target_schema
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE c.oid = '__SCHEMA__.wh_settings'::regclass;

  IF v_target_schema = 'public' THEN
    -- Single-schema deployment: source and target are the same table. Nothing to carry.
    RETURN;
  END IF;

  -- ── wh_settings ────────────────────────────────────────────────────────────────────────────
  IF to_regclass('public.wh_settings') IS NOT NULL THEN
    -- Intersect the column sets rather than hard-coding them: a database that stopped receiving
    -- migrations mid-history may hold an older shape, and a copy that throws would block startup.
    SELECT string_agg(quote_ident(src.column_name), ', ' ORDER BY src.ordinal_position)
      INTO v_cols
      FROM information_schema.columns src
     WHERE src.table_schema = 'public'
       AND src.table_name = 'wh_settings'
       AND EXISTS (SELECT 1
                     FROM information_schema.columns tgt
                    WHERE tgt.table_schema = v_target_schema
                      AND tgt.table_name = 'wh_settings'
                      AND tgt.column_name = src.column_name);

    IF v_cols IS NOT NULL THEN
      EXECUTE format(
        'INSERT INTO %I.wh_settings (%s) SELECT %s FROM public.wh_settings
           ON CONFLICT (setting_key) DO NOTHING',
        v_target_schema, v_cols, v_cols);
      GET DIAGNOSTICS v_copied = ROW_COUNT;
      IF v_copied > 0 THEN
        RAISE NOTICE 'Carried % setting(s) from public.wh_settings into %.wh_settings',
          v_copied, v_target_schema;
      END IF;
    END IF;
  END IF;

  -- ── wh_dead_letters ────────────────────────────────────────────────────────────────────────
  IF to_regclass('public.wh_dead_letters') IS NOT NULL THEN
    SELECT string_agg(quote_ident(src.column_name), ', ' ORDER BY src.ordinal_position)
      INTO v_cols
      FROM information_schema.columns src
     WHERE src.table_schema = 'public'
       AND src.table_name = 'wh_dead_letters'
       AND EXISTS (SELECT 1
                     FROM information_schema.columns tgt
                    WHERE tgt.table_schema = v_target_schema
                      AND tgt.table_name = 'wh_dead_letters'
                      AND tgt.column_name = src.column_name);

    IF v_cols IS NOT NULL THEN
      EXECUTE format(
        'INSERT INTO %I.wh_dead_letters (%s) SELECT %s FROM public.wh_dead_letters
           ON CONFLICT (dead_letter_id) DO NOTHING',
        v_target_schema, v_cols, v_cols);
      GET DIAGNOSTICS v_copied = ROW_COUNT;
      IF v_copied > 0 THEN
        RAISE NOTICE 'Carried % dead letter(s) from public.wh_dead_letters into %.wh_dead_letters. '
          'The public copy is left in place; prune it once every service on this database has '
          'applied migration 105.', v_copied, v_target_schema;
      END IF;
    END IF;
  END IF;
END $$;
