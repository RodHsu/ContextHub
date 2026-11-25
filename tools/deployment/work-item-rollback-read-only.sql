\set ON_ERROR_STOP on
\if :{?work_item_rollback_mode}
\else
\set work_item_rollback_mode ''
\endif

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL search_path = pg_catalog;
SELECT set_config('contexthub.work_item_rollback_mode', :'work_item_rollback_mode', true);

-- Lock both tables before inspecting the guard or changing it. A partial or
-- unexpected guard requires operator review and is never overwritten.
LOCK TABLE public.project_work_items, public.project_work_item_checklist_items
    IN SHARE ROW EXCLUSIVE MODE;

DO $manage$
DECLARE
    mode text := current_setting('contexthub.work_item_rollback_mode');
    guard_body constant text := $guard$
BEGIN
    RAISE EXCEPTION 'Work Item editing is read-only during rollback' USING ERRCODE = '55000';
END
$guard$;
    guard_function oid := to_regprocedure('public.reject_work_item_writes_during_rollback()');
    named_triggers integer;
    verified_triggers integer;
BEGIN
    IF mode NOT IN ('enable', 'verify', 'disable') THEN
        RAISE EXCEPTION 'Pass work_item_rollback_mode=enable, verify, or disable.';
    END IF;

    -- pg_trigger.tgtype 62 means BEFORE INSERT/UPDATE/DELETE/TRUNCATE, statement level.
    SELECT count(*),
           count(*) FILTER (WHERE tgfoid = guard_function AND tgtype = 62
                             AND tgenabled = 'A' AND tgnargs = 0 AND NOT tgisinternal
                             AND tgqual IS NULL AND tgnewtable IS NULL AND tgoldtable IS NULL)
    INTO named_triggers, verified_triggers
    FROM pg_trigger
    WHERE tgname = 'work_item_rollback_read_only'
      AND tgrelid IN ('public.project_work_items'::regclass,
                      'public.project_work_item_checklist_items'::regclass);

    IF guard_function IS NULL AND named_triggers = 0 THEN
        IF mode = 'verify' THEN
            RAISE EXCEPTION 'The Work Item rollback guard is absent.';
        ELSIF mode = 'enable' THEN
            EXECUTE format(
                'CREATE FUNCTION public.reject_work_item_writes_during_rollback() RETURNS trigger
                 LANGUAGE plpgsql SET search_path = pg_catalog AS %L', guard_body);
            CREATE TRIGGER work_item_rollback_read_only BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                ON public.project_work_items FOR EACH STATEMENT
                EXECUTE FUNCTION public.reject_work_item_writes_during_rollback();
            CREATE TRIGGER work_item_rollback_read_only BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                ON public.project_work_item_checklist_items FOR EACH STATEMENT
                EXECUTE FUNCTION public.reject_work_item_writes_during_rollback();
            ALTER TABLE public.project_work_items ENABLE ALWAYS TRIGGER work_item_rollback_read_only;
            ALTER TABLE public.project_work_item_checklist_items ENABLE ALWAYS TRIGGER work_item_rollback_read_only;
        END IF;
        RETURN;
    END IF;

    IF named_triggers <> 2 OR verified_triggers <> 2
       OR (SELECT count(*) FROM pg_trigger WHERE tgfoid = guard_function) <> 2
       OR NOT EXISTS (
        SELECT 1
        FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang
        WHERE p.oid = guard_function AND p.prosrc = guard_body
          AND p.prorettype = 'pg_catalog.trigger'::regtype
          AND l.lanname = 'plpgsql' AND NOT p.prosecdef
          AND p.proconfig = ARRAY['search_path=pg_catalog']
    ) THEN
        RAISE EXCEPTION 'Unexpected Work Item rollback guard. Inspect both tables and the guard function.';
    END IF;

    IF mode = 'disable' THEN
        DROP TRIGGER work_item_rollback_read_only ON public.project_work_items;
        DROP TRIGGER work_item_rollback_read_only ON public.project_work_item_checklist_items;
        DROP FUNCTION public.reject_work_item_writes_during_rollback();
    END IF;
END
$manage$;

COMMIT;
