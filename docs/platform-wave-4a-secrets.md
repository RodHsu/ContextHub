# Platform Wave 4A: Secrets, SSH, and Password Step-up

Wave 4A adds the server-side foundation for encrypted Secrets/Credentials, typed use leases, SSH certificate and bound-signer workflows, and fresh-password AAL1 step-up. It does not add TOTP, WebAuthn/FIDO, passkeys, passwordless authentication, or AgentExecution integration.

## Security boundaries

- Secret values enter only the version-upload REST endpoint as bounded binary request bodies. The application encrypts each version with a random AES-256-GCM DEK and wraps that DEK with the configured versioned Secret KEK. PostgreSQL stores ciphertext and wrapped keys only.
- The Secret KEK key domain is distinct from Managed Files. `SecretKeyAuthority` accepts 32-byte Linux key files with no group/other permissions. Missing keys, unsafe permissions, Windows mounted-file use, and AEAD failures fail closed.
- `Use` is not `Reveal`. There is no raw reveal/export REST or MCP surface. Capabilities bind actor, tenant/project, optional execution, secret version, purpose, target, authority revision, TTL, usage, concurrency, and revocation state.
- SSH private keys can only create `SshSigner` leases. The broker constructs and signs the canonical ContextHub SSH-authentication payload; callers cannot submit arbitrary bytes.
- SSH certificates are short lived. Renewal revalidates actor, project, Foundation rights, Secret policy, authority revision, capability, target, maximum session lifetime, and maximum renewals while serialized against CA-secret mutation. Revocations always create a KRL reconciliation record in Wave 4A.
- Password step-up is available only to an interactive human actor and binds the assertion to the authenticated token identifier, actor, purpose, optional resource, nonce, revision, TTL, and bounded uses. Password AAL1 never satisfies AAL2/AAL3 policy.

## Production configuration

Configure only file paths, never KEK bytes:

```text
SECRET_KEK_CURRENT_ID=v1
SECRET_KEK_V1_FILE=/run/secrets/contexthub-secret-kek-v1
```

The deployment must mount the key file independently into both API and worker containers as a read-only, owner-only Linux file. The repository deliberately does not provide or generate a production KEK. Keep every historical KEK mounted until all versions using it have been rewrapped and recovery has been verified.

The built-in SSH certificate issuer and high-assurance approval verifier are deny-by-default. Production must supply audited implementations before certificate issuance or AAL2-gated rotation/revocation is enabled. Wave 4A does not expose CA private material.

## Operations

- Migration `048_secrets_ssh_password_step_up.sql` creates the encrypted secret, authorization, lease, audit, step-up, SSH certificate, and revocation state.
- The worker expires stale leases and certificates and reports stale relations and pending KRL work using counts only.
- KEK rewrap is non-destructive and requires external AAL2 approval plus Foundation and Secret-policy authorization. Destructive KEK/SSH-CA operations remain disabled.
- Production deployment is separate from repository delivery and requires an operator-provided secure KEK mount and SSH issuer decision.
