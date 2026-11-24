-- Migration 009 created the historical owner for foreign-key ownership and
-- assigned a built-in dashboard password. Keep the owner and clear only that
-- exact credential. The digest identifies the historical hash, not a password.
UPDATE tenant_users
SET password_hash = '',
    password_updated_at = NULL,
    updated_at = NOW()
WHERE id = '73000000-0000-0000-0000-000000000001'
  AND encode(sha256(convert_to(password_hash, 'UTF8')), 'hex') =
      'a9461eadfff2c4a78a6857be15d1e1b8779accd84711dae07b25a29c9359a3a6';
