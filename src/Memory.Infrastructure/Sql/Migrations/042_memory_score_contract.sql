DO $$
DECLARE
    malformed_memory_items BIGINT;
    malformed_conversation_insights BIGINT;
BEGIN
    SELECT COUNT(*)
    INTO malformed_memory_items
    FROM memory_items
    WHERE importance < 0 OR importance > 1
       OR confidence < 0 OR confidence > 1;

    SELECT COUNT(*)
    INTO malformed_conversation_insights
    FROM conversation_insights
    WHERE importance < 0 OR importance > 1
       OR confidence < 0 OR confidence > 1;

    IF malformed_memory_items > 0 OR malformed_conversation_insights > 0 THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'Migration 042_memory_score_contract.sql cannot apply because historical memory scores are outside canonical [0,1].',
            DETAIL = format(
                'memory_items=%s; conversation_insights=%s',
                malformed_memory_items,
                malformed_conversation_insights),
            HINT = 'Audit and explicitly reconcile malformed rows before retrying; this migration never normalizes or deletes stored values.';
    END IF;
END $$;

ALTER TABLE memory_items
    ADD CONSTRAINT ck_memory_items_importance_normalized
        CHECK (importance >= 0 AND importance <= 1),
    ADD CONSTRAINT ck_memory_items_confidence_normalized
        CHECK (confidence >= 0 AND confidence <= 1);

ALTER TABLE conversation_insights
    ADD CONSTRAINT ck_conversation_insights_importance_normalized
        CHECK (importance >= 0 AND importance <= 1),
    ADD CONSTRAINT ck_conversation_insights_confidence_normalized
        CHECK (confidence >= 0 AND confidence <= 1);
