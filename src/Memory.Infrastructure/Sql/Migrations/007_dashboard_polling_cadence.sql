UPDATE instance_settings
SET value_json = jsonb_set(
    jsonb_set(
        jsonb_set(
            jsonb_set(
                jsonb_set(
                    jsonb_set(
                        jsonb_set(
                            jsonb_set(
                                jsonb_set(
                                    jsonb_set(
                                        jsonb_set(
                                            jsonb_set(
                                                jsonb_set(
                                                    value_json,
                                                    '{snapshotPolling}',
                                                    COALESCE(value_json -> 'snapshotPolling', '{}'::jsonb),
                                                    true),
                                                '{snapshotPolling,statusCoreSeconds}',
                                                CASE
                                                    WHEN value_json #>> '{snapshotPolling,statusCoreSeconds}' = '30' THEN '5'::jsonb
                                                    ELSE COALESCE(value_json #> '{snapshotPolling,statusCoreSeconds}', '5'::jsonb)
                                                END,
                                                true),
                                            '{snapshotPolling,embeddingRuntimeSeconds}',
                                            CASE
                                                WHEN value_json #>> '{snapshotPolling,embeddingRuntimeSeconds}' = '30' THEN '5'::jsonb
                                                ELSE COALESCE(value_json #> '{snapshotPolling,embeddingRuntimeSeconds}', '5'::jsonb)
                                            END,
                                            true),
                                        '{snapshotPolling,dependenciesHealthSeconds}',
                                        CASE
                                            WHEN value_json #>> '{snapshotPolling,dependenciesHealthSeconds}' = '10' THEN '5'::jsonb
                                            ELSE COALESCE(value_json #> '{snapshotPolling,dependenciesHealthSeconds}', '5'::jsonb)
                                        END,
                                        true),
                                    '{snapshotPolling,dockerHostSeconds}',
                                    CASE
                                        WHEN value_json #>> '{snapshotPolling,dockerHostSeconds}' = '30' THEN '5'::jsonb
                                        ELSE COALESCE(value_json #> '{snapshotPolling,dockerHostSeconds}', '5'::jsonb)
                                    END,
                                    true),
                                '{snapshotPolling,dependencyResourcesSeconds}',
                                COALESCE(value_json #> '{snapshotPolling,dependencyResourcesSeconds}', '5'::jsonb),
                                true),
                            '{snapshotPolling,recentOperationsSeconds}',
                            COALESCE(value_json #> '{snapshotPolling,recentOperationsSeconds}', '5'::jsonb),
                            true),
                        '{snapshotPolling,resourceChartSeconds}',
                        CASE
                            WHEN value_json #>> '{snapshotPolling,resourceChartSeconds}' IN ('1', '3') THEN '5'::jsonb
                            ELSE COALESCE(value_json #> '{snapshotPolling,resourceChartSeconds}', '5'::jsonb)
                        END,
                        true),
                    '{overviewPollingSeconds}',
                    CASE
                        WHEN value_json #>> '{overviewPollingSeconds}' = '10' THEN '5'::jsonb
                        ELSE COALESCE(value_json #> '{overviewPollingSeconds}', '5'::jsonb)
                    END,
                    true),
                '{metricsPollingSeconds}',
                CASE
                    WHEN value_json #>> '{metricsPollingSeconds}' = '3' THEN '5'::jsonb
                    ELSE COALESCE(value_json #> '{metricsPollingSeconds}', '5'::jsonb)
                END,
                true),
            '{jobsPollingSeconds}',
            CASE
                WHEN value_json #>> '{jobsPollingSeconds}' = '8' THEN '5'::jsonb
                ELSE COALESCE(value_json #> '{jobsPollingSeconds}', '5'::jsonb)
            END,
            true),
        '{logsPollingSeconds}',
        CASE
            WHEN value_json #>> '{logsPollingSeconds}' = '10' THEN '5'::jsonb
            ELSE COALESCE(value_json #> '{logsPollingSeconds}', '5'::jsonb)
        END,
        true),
    '{performancePollingSeconds}',
    CASE
        WHEN value_json #>> '{performancePollingSeconds}' = '30' THEN '5'::jsonb
        ELSE COALESCE(value_json #> '{performancePollingSeconds}', '5'::jsonb)
    END,
    true),
    revision = revision + 1,
    updated_at = NOW(),
    updated_by = 'migration:007_dashboard_polling_cadence'
WHERE setting_key = 'behavior'
  AND (
      NOT (value_json ? 'snapshotPolling')
      OR value_json #>> '{snapshotPolling,statusCoreSeconds}' = '30'
      OR value_json #>> '{snapshotPolling,embeddingRuntimeSeconds}' = '30'
      OR value_json #>> '{snapshotPolling,dependenciesHealthSeconds}' = '10'
      OR value_json #>> '{snapshotPolling,dockerHostSeconds}' = '30'
      OR value_json #>> '{snapshotPolling,resourceChartSeconds}' IN ('1', '3')
      OR value_json #>> '{overviewPollingSeconds}' = '10'
      OR value_json #>> '{metricsPollingSeconds}' = '3'
      OR value_json #>> '{jobsPollingSeconds}' = '8'
      OR value_json #>> '{logsPollingSeconds}' = '10'
      OR value_json #>> '{performancePollingSeconds}' = '30'
  );
