# Platform Wave 4B: MFA and Assurance

Wave 4B adds interactive-human TOTP, one-time recovery codes, WebAuthn/FIDO2 security keys, platform authenticators, passkeys, and centrally enforced AAL2/AAL3 step-up. It does not add AgentExecution resource integration, dashboard redesign, legacy cutover, or production deployment.

## Assurance and recovery boundaries

- Password verification issues AAL1, TOTP and one-time recovery codes issue AAL2, and user-verified WebAuthn/FIDO2 or passkey ceremonies issue AAL3.
- Every assertion is bound to the authenticated session, actor, purpose, optional resource, nonce, revision, expiry, and bounded use count. A lower-assurance factor cannot satisfy a higher-assurance requirement.
- Enrolling another factor requires the highest assurance already active for that user. The first factor requires AAL1, an existing TOTP factor raises the requirement to AAL2, and any active WebAuthn credential raises it to AAL3.
- Every factor-authority mutation advances a durable per-user revision under a PostgreSQL transaction advisory lock. Enrollment and authentication completion revalidate the current factor topology, policy revision, and the exact consumed step-up assertion; stale work must be restarted and performs no factor mutation.
- Factor removal requires factor-appropriate step-up. Account recovery is fail-closed behind the external high-assurance approval authority; the built-in verifier denies every request.
- Service identities and agents cannot enroll, verify, recover, or remove MFA factors. Wave 4B exposes these operations only through authenticated REST endpoints for an interactive human session.

## Factor-secret and replay boundaries

- TOTP seeds are generated server-side, returned only during enrollment, and stored through the existing Secret envelope-encryption authority. PostgreSQL does not store a plaintext seed.
- TOTP verification accepts at most one configured adjacent time step and records the last accepted counter under a serializable lock. Reusing the same or an older counter fails.
- Recovery codes use random values and persist only an individual salt and PBKDF2-SHA256 hash. Each code is one-time, and regeneration revokes every unused code from the previous set before returning a new set once.
- WebAuthn stores credential identifiers, public keys, counters, transports, and backup metadata only. It never receives or stores an authenticator private key.
- WebAuthn registration and authentication require user verification and bind the one-time challenge to the exact HTTPS origin, relying-party ID, authenticated session, ceremony purpose, optional resource, expiry, and stored options hash. Replayed or expired ceremonies and counter rollback fail closed.
- MFA security events record actor, factor identifier, method, assurance, action, purpose/resource hashes, and outcome without recording seeds, recovery codes, challenges, or credential responses.

## Production configuration

Configure the relying party to the exact public host serving the authenticated ceremony. Multiple origins can be supplied through normal .NET configuration indexing when a deployment intentionally serves more than one exact HTTPS origin.

```text
MFA_TOTP_ISSUER=ContextHub
MFA_TOTP_ALLOWED_DRIFT_STEPS=1
MFA_POLICY_REVISION=wave4b-v1
WEBAUTHN_RP_ID=context-hub.example.com
WEBAUTHN_RP_NAME=ContextHub
WEBAUTHN_ORIGIN=https://context-hub.example.com
```

An empty RP ID/origin, a non-HTTPS origin, origin mismatch, RP mismatch, challenge mismatch, missing user verification, or invalid signature rejects the ceremony. Do not use wildcard origins. A change to the public hostname requires an explicit WebAuthn RP/origin migration plan because existing credentials are scoped to their relying party.

Increase `MFA_POLICY_REVISION` whenever deployed MFA assurance policy changes. A revision change durably invalidates older pending enrollments, WebAuthn ceremonies, and step-up assertions for each user on their next MFA operation.

## Operations

- Migration `049_mfa_totp_webauthn.sql` adds TOTP factors, recovery-code hashes, WebAuthn public credentials, one-time ceremonies, MFA audit events, durable user-scoped MFA authority revisions, and AAL-aware step-up constraints.
- The REST surface is under `/api/step-up`: TOTP enrollment/confirmation/verification, recovery verification/regeneration, WebAuthn registration/authentication options and completion, and factor removal.
- Browser clients must pass WebAuthn option and credential JSON without changing binary encodings. The server remains authoritative for challenge, RP, origin, user verification, ownership, expiry, replay, and sign-counter validation.
- Production deployment is separate from repository delivery and is not part of Wave 4B.

Run the verification gates:

```powershell
dotnet test ContextHub.slnx
dotnet format ContextHub.slnx --verify-no-changes
docker compose config --quiet
```
