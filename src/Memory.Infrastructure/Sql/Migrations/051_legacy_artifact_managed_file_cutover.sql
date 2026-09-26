CREATE SCHEMA IF NOT EXISTS audit;

CREATE OR REPLACE FUNCTION audit.try_parse_jsonb(value text) RETURNS jsonb AS $$
BEGIN
    RETURN value::jsonb;
EXCEPTION WHEN others THEN
    RETURN NULL;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION audit.strip_provider_locator_keys(value jsonb) RETURNS jsonb AS $$
DECLARE
    result jsonb;
BEGIN
    IF value IS NULL THEN
        RETURN '{}'::jsonb;
    END IF;
    IF jsonb_typeof(value) = 'object' THEN
        SELECT COALESCE(jsonb_object_agg(entry.key, audit.strip_provider_locator_keys(entry.value)), '{}'::jsonb)
        INTO result
        FROM jsonb_each(value) AS entry
        WHERE lower(entry.key) NOT IN (
            'provider', 'bucket', 'container', 'key', 'objectkey', 'storageid', 'uri', 'url', 'endpoint',
            'accountid', 'credential', 'accesskey', 'secretkey', 'presignedurl', 'directurl');
        RETURN result;
    END IF;
    IF jsonb_typeof(value) = 'array' THEN
        SELECT COALESCE(jsonb_agg(audit.strip_provider_locator_keys(entry.value)), '[]'::jsonb)
        INTO result
        FROM jsonb_array_elements(value) AS entry;
        RETURN result;
    END IF;
    RETURN value;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION audit.is_public_artifact_metadata_safe(value text) RETURNS boolean AS $$
DECLARE
    metadata jsonb := audit.try_parse_jsonb(value);
    kind text;
BEGIN
    IF metadata IS NULL OR jsonb_typeof(metadata) <> 'object' THEN
        RETURN false;
    END IF;
    kind := metadata->>'kind';
    IF kind NOT IN ('Summary', 'Snippet', 'FileReference') THEN
        RETURN false;
    END IF;
    IF metadata ? 'objectRef' OR metadata @? '$.**.keyvalue() ? (@.key == "provider" || @.key == "bucket" || @.key == "container" || @.key == "key" || @.key == "objectKey" || @.key == "storageId" || @.key == "uri" || @.key == "url" || @.key == "endpoint" || @.key == "accountId" || @.key == "credential" || @.key == "accessKey" || @.key == "secretKey" || @.key == "presignedUrl" || @.key == "directUrl")' THEN
        RETURN false;
    END IF;
    IF kind = 'FileReference' THEN
        RETURN COALESCE(metadata->>'fileId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
           AND COALESCE(metadata->>'fileVersionId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
    END IF;
    RETURN NOT (metadata ? 'fileId') AND NOT (metadata ? 'fileVersionId');
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE TABLE IF NOT EXISTS audit.legacy_artifact_cutover_mappings
(
    memory_id uuid PRIMARY KEY REFERENCES memory_items(id) ON DELETE RESTRICT,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    project_id text NOT NULL,
    legacy_kind text NOT NULL,
    old_status text NOT NULL,
    old_metadata_text text NOT NULL,
    old_metadata_sha256 text NOT NULL,
    mapped_file_id uuid NULL REFERENCES file_assets(id) ON DELETE RESTRICT,
    mapped_file_version_id uuid NULL REFERENCES file_versions(id) ON DELETE RESTRICT,
    resolution text NOT NULL,
    migrated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_legacy_artifact_cutover_hash CHECK (old_metadata_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_legacy_artifact_cutover_resolution CHECK (resolution IN ('Pending', 'Mapped', 'ExternalObjectArchived', 'UnmappedArchived', 'MalformedArchived')),
    CONSTRAINT ck_legacy_artifact_cutover_mapping CHECK ((resolution = 'Mapped') = (mapped_file_id IS NOT NULL AND mapped_file_version_id IS NOT NULL))
);
REVOKE ALL ON TABLE audit.legacy_artifact_cutover_mappings FROM PUBLIC;

INSERT INTO audit.legacy_artifact_cutover_mappings
    (memory_id, tenant_id, owner_user_id, project_id, legacy_kind, old_status, old_metadata_text,
     old_metadata_sha256, resolution)
SELECT mi.id,
       mi.tenant_id,
       mi.owner_user_id,
       mi.project_id,
       COALESCE(audit.try_parse_jsonb(mi.metadata_json)->>'kind', 'Malformed'),
       mi.status,
       mi.metadata_json,
       encode(sha256(convert_to(mi.metadata_json, 'UTF8')), 'hex'),
       'Pending'
FROM memory_items mi
WHERE mi.source_type = 'project-artifact-exchange'
  AND mi.status = 'Active'
  AND (
      audit.try_parse_jsonb(mi.metadata_json) IS NULL OR
      COALESCE(audit.try_parse_jsonb(mi.metadata_json)->>'kind', '') NOT IN ('Summary', 'Snippet') OR
      audit.try_parse_jsonb(mi.metadata_json) ? 'objectRef')
ON CONFLICT (memory_id) DO NOTHING;

DO $$
DECLARE
    ambiguous_count integer;
BEGIN
    WITH candidate_versions AS (
        SELECT DISTINCT mapping.memory_id, fv.id AS file_version_id
        FROM audit.legacy_artifact_cutover_mappings mapping
        JOIN file_assets fa
          ON fa.project_id = mapping.project_id
         AND fa.tenant_id IS NOT DISTINCT FROM mapping.tenant_id
         AND fa.owner_user_id IS NOT DISTINCT FROM mapping.owner_user_id
        JOIN file_versions fv ON fv.file_asset_id = fa.id
        CROSS JOIN LATERAL audit.try_parse_jsonb(mapping.old_metadata_text) metadata
        WHERE mapping.legacy_kind = 'FileReference'
          AND (
              (COALESCE(metadata->>'fileId', '') ~* '^[0-9a-f-]{36}$'
               AND COALESCE(metadata->>'fileVersionId', '') ~* '^[0-9a-f-]{36}$'
               AND fa.id = (metadata->>'fileId')::uuid
               AND fv.id = (metadata->>'fileVersionId')::uuid)
              OR
              (COALESCE(metadata->'objectRef'->>'sha256', '') ~* '^[0-9a-f]{64}$'
               AND fv.content_sha256 = lower(metadata->'objectRef'->>'sha256')))
    ), ambiguous AS (
        SELECT memory_id FROM candidate_versions GROUP BY memory_id HAVING count(*) > 1
    )
    SELECT count(*) INTO ambiguous_count FROM ambiguous;
    IF ambiguous_count > 0 THEN
        RAISE EXCEPTION 'Legacy Artifact cutover found % ambiguous FileReference mapping(s); migration is fail-closed.', ambiguous_count;
    END IF;
END $$;

WITH candidate_versions AS (
    SELECT DISTINCT mapping.memory_id, fa.id AS file_id, fv.id AS file_version_id
    FROM audit.legacy_artifact_cutover_mappings mapping
    JOIN file_assets fa
      ON fa.project_id = mapping.project_id
     AND fa.tenant_id IS NOT DISTINCT FROM mapping.tenant_id
     AND fa.owner_user_id IS NOT DISTINCT FROM mapping.owner_user_id
    JOIN file_versions fv ON fv.file_asset_id = fa.id
    CROSS JOIN LATERAL audit.try_parse_jsonb(mapping.old_metadata_text) metadata
    WHERE mapping.legacy_kind = 'FileReference'
      AND (
          (COALESCE(metadata->>'fileId', '') ~* '^[0-9a-f-]{36}$'
           AND COALESCE(metadata->>'fileVersionId', '') ~* '^[0-9a-f-]{36}$'
           AND fa.id = (metadata->>'fileId')::uuid
           AND fv.id = (metadata->>'fileVersionId')::uuid)
          OR
          (COALESCE(metadata->'objectRef'->>'sha256', '') ~* '^[0-9a-f]{64}$'
           AND fv.content_sha256 = lower(metadata->'objectRef'->>'sha256')))
)
UPDATE audit.legacy_artifact_cutover_mappings mapping
SET mapped_file_id = candidate.file_id,
    mapped_file_version_id = candidate.file_version_id,
    resolution = 'Mapped',
    migrated_at = now()
FROM candidate_versions candidate
WHERE mapping.memory_id = candidate.memory_id
  AND mapping.resolution = 'Pending';

UPDATE memory_items mi
SET metadata_json = jsonb_build_object(
        'artifactExchange', true,
        'kind', 'FileReference',
        'sourceSystem', COALESCE(audit.try_parse_jsonb(mapping.old_metadata_text)->>'sourceSystem', ''),
        'fileId', mapping.mapped_file_id,
        'fileVersionId', mapping.mapped_file_version_id,
        'metadata', audit.strip_provider_locator_keys(COALESCE(audit.try_parse_jsonb(mapping.old_metadata_text)->'metadata', '{}'::jsonb)))::text,
    content = 'ContextHub managed file reference.',
    tags = array_append(array_remove(array_remove(mi.tags, 'artifact-kind:externalobject'), 'artifact-kind:filereference'), 'artifact-kind:filereference'),
    updated_at = now()
FROM audit.legacy_artifact_cutover_mappings mapping
WHERE mi.id = mapping.memory_id
  AND mapping.resolution = 'Mapped';

UPDATE audit.legacy_artifact_cutover_mappings
SET resolution = CASE
        WHEN legacy_kind = 'ExternalObject' THEN 'ExternalObjectArchived'
        WHEN legacy_kind = 'FileReference' THEN 'UnmappedArchived'
        ELSE 'MalformedArchived'
    END,
    migrated_at = now()
WHERE resolution = 'Pending';

UPDATE memory_items mi
SET status = 'Archived',
    tags = array_append(mi.tags, 'artifact-cutover-archived'),
    updated_at = now()
FROM audit.legacy_artifact_cutover_mappings mapping
WHERE mi.id = mapping.memory_id
  AND mapping.resolution IN ('ExternalObjectArchived', 'UnmappedArchived', 'MalformedArchived')
  AND mi.status = 'Active';

UPDATE memory_items mi
SET metadata_json = jsonb_build_object(
        'artifactExchange', true,
        'kind', audit.try_parse_jsonb(mi.metadata_json)->>'kind',
        'sourceSystem', COALESCE(audit.try_parse_jsonb(mi.metadata_json)->>'sourceSystem', ''),
        'metadata', audit.strip_provider_locator_keys(COALESCE(audit.try_parse_jsonb(mi.metadata_json)->'metadata', '{}'::jsonb)))::text,
    updated_at = now()
WHERE mi.source_type = 'project-artifact-exchange'
  AND mi.status = 'Active'
  AND audit.try_parse_jsonb(mi.metadata_json)->>'kind' IN ('Summary', 'Snippet');

ALTER TABLE memory_items DROP CONSTRAINT IF EXISTS ck_memory_items_public_artifact_contract;
ALTER TABLE memory_items ADD CONSTRAINT ck_memory_items_public_artifact_contract CHECK (
    source_type <> 'project-artifact-exchange' OR status <> 'Active' OR audit.is_public_artifact_metadata_safe(metadata_json));

COMMENT ON TABLE audit.legacy_artifact_cutover_mappings IS
    'Restricted rollback-only evidence for the Wave 7A legacy Artifact/ObjectRef cutover. Never expose through API, MCP, UI, telemetry, or agent contracts.';
