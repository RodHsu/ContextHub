ALTER TABLE managed_object_chunks
    ADD COLUMN locator_format TEXT NOT NULL DEFAULT 'LegacyIndex',
    ADD CONSTRAINT ck_managed_object_chunks_locator_format
        CHECK (locator_format IN ('LegacyIndex', 'ImmutableAttempt'));
