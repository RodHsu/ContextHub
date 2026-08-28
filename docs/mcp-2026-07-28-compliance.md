# MCP 2026-07-28 Compliance and Security Review

Reviewed on 2026-08-12 against the official MCP `2026-07-28` specification, schema, release announcement, and frozen conformance requirements.

## Result

ContextHub implements the applicable `2026-07-28` stateless Streamable HTTP contract on `/mcp` and `/mcp-chat`. Legacy initialization remains a bounded client migration fallback; modern traffic does not create or depend on an MCP transport session.

The twelve-month policy applies to features entered in MCP's deprecated-features registry. It is not a guarantee that removed session transport behavior remains valid for twelve months. ContextHub therefore treats the legacy bridge fallback as a locally managed migration runway, not as part of the modern protocol contract.

## Compliance Matrix

| Requirement | ContextHub implementation | Verification |
| --- | --- | --- |
| Stateless request/response lifecycle | MCP SDK HTTP transports set `Stateless = true`; modern discovery replaces initialize/session state | `McpProtocolTests`, `ChatGptGatewayMcpTests`, `McpStdioBridgeTests` |
| Per-request self-description | `_meta` contains protocol version, client capabilities, and client info | Raw HTTP and stdio bridge tests |
| HTTP routing headers | `MCP-Protocol-Version` and `Mcp-Method` are sent on every modern request; `Mcp-Name` is sent for `tools/call`, `resources/read`, and `prompts/get` | Raw HTTP mismatch/missing-header tests and bridge request capture |
| Header value encoding | Unsafe, non-ASCII, whitespace-delimited, control, and sentinel-shaped routing values use the MCP Base64 sentinel form | Parameterized bridge tests |
| Version/header validation | Missing or mismatched modern routing metadata returns HTTP 400 with JSON-RPC `-32020` | Real Streamable HTTP black-box tests |
| No transport sessions | Modern responses do not issue `Mcp-Session-Id`; incoming session and replay headers are ignored | Raw HTTP black-box tests |
| Removed stream methods | `GET` and `DELETE` on the MCP endpoint return 405 | Raw HTTP black-box tests |
| Origin / DNS-rebinding defense | Requests without `Origin` are accepted; present origins must match configured HTTPS origins, otherwise 403 | MCP and gateway origin tests |
| Authentication per request | `/mcp` bearer-token policy and `/mcp-chat` OAuth bearer validation run on every request | API contract, MCP protocol, and gateway tests |
| Response streaming | A response may still use SSE within its one request; no long-lived server GET stream is used | Real Streamable HTTP tests |
| Server discovery | `server/discover` advertises supported versions and capabilities | Discovery and bridge negotiation tests |
| Legacy interoperability | Bridge negotiates modern first, falls back to legacy initialize, and only legacy mode retains an optional session id | Bridge fallback and retry tests |

Custom `x-mcp-header` mirroring is not applicable today because ContextHub publishes no tool, resource, or prompt schema with that annotation. If one is added, its request-header mapping must be added to contract tests before release.

The full upstream conformance catalog contains fixture-specific tools and content types. ContextHub runs the applicable stateless transport scenario and maintains product-specific black-box contract coverage rather than claiming unsupported fixture capabilities.

## Security Findings Remediated

| Severity | Finding | Remediation |
| --- | --- | --- |
| High | Direct `System.Security.Cryptography.Xml` dependency resolved to a version with known advisories | Pinned `10.0.11` privately; solution vulnerability listing reports no vulnerable direct or transitive NuGet packages |
| High | Services directly held the Docker daemon socket; a read-only bind does not make Docker API calls read-only | Added repo-built, digest-based, isolated HAProxy socket gateways. MCP receives read-only metrics endpoints; Dashboard additionally receives only container restart |
| High | Clearing ASP.NET trusted-proxy lists accepted spoofed `X-Forwarded-*` values | Retained secure loopback defaults and added explicit IP/CIDR allowlists with one-hop and symmetry enforcement |
| Medium | MCP endpoints did not validate browser `Origin` | Added exact configured-origin validation to both MCP endpoints |
| Medium | Modern bridge emitted raw routing values | Added MCP Base64 sentinel encoding and narrowed `Mcp-Name` to methods that require it |
| Medium | Production gateway metadata could fall back to attacker-controlled request host when public URLs were omitted | Production startup now requires absolute HTTPS public MCP and resource-metadata URLs |
| High / Critical | The original third-party socket proxy and pgvector helper binary contained fixable OS/Go findings | Rebuilt the proxy from a digest-pinned, upgraded Alpine base and rebuilt `gosu` with Go 1.26.5. Image identity and fresh scanner results must be reconciled again before release; this source review does not claim zero remaining image findings. |
| High | Legacy source, governance, evaluation, and suggested-action workflows were scoped only by project or object ID, allowing cross-tenant reads and mutations | Added tenant and owner columns, actor-scoped queries, ownership propagation to derived records and background jobs, migration `020_hub_workflow_ownership.sql`, and cross-tenant regression coverage |
| Medium | Import preview deduplication and memory-job lookup/listing could disclose another owner's keys or job state | Applied actor scope to preview queries, individual job reads, and dashboard job listings; authenticated job listings bypass the shared global snapshot cache |
| Medium | OAuth dynamic registration/token flows and Dashboard login lacked request throttling | Added IP-partitioned fixed-window limits for authorization, registration, token, and production Dashboard login endpoints |
| Medium | The storage migration preview rendered a database-derived column name through `MarkupString` | HTML-encoded the value before rendering |
| Medium | The default ChatGPT OAuth scope included instance-wide runtime-log access | Removed `logs:read` from the default OAuth actor scope; privileged callers must request it explicitly |

## Validation Evidence

- All 294 automated tests passed across unit, MCP protocol, API contract, ChatGPT gateway, integration, Compose smoke, and Dashboard suites.
- `dotnet build ContextHub.slnx --no-restore`, `dotnet format ContextHub.slnx --verify-no-changes --no-restore`, `git diff --check`, and `docker compose config --quiet` passed.
- NuGet direct and transitive vulnerability enumeration reported no known vulnerable packages. The only deprecated dependency reported was xUnit 2.9.3 in test projects; it is not part of the runtime image.
- Historical Trivy artifacts were not sufficiently consistent to establish a single authoritative zero-finding result for every final image. Re-run Trivy against the exact release image IDs and reconcile the resulting artifacts before release. Docker Scout could not run without an authenticated Docker account and was skipped; this review does not claim independent image-scanner confirmation.
- A local full-stack deployment reached healthy state for all nine Compose containers. Migration 020 and PostgreSQL collation refresh were verified from the running database.
- Raw HTTP probes verified modern `2026-07-28` discovery and tool-list requests without `Mcp-Session-Id`, legacy `2025-11-25` negotiation compatibility, and registration throttling (`400` for the first ten invalid requests, then `429`).

These results establish the reviewed source tree and local images as of 2026-08-12. They are not evidence that a remote environment has received this version or that a future vulnerability database will remain unchanged.

## Deployment Requirements

- Set `CONTEXTHUB_ALLOWED_MCP_ORIGIN` and `CHATGPT_GATEWAY_ALLOWED_MCP_ORIGIN` to the exact public origin, without a path.
- Set `TRUSTED_PROXY_NETWORK` only to the exact reverse-proxy network CIDR. Leave it blank when the app is not behind a trusted proxy.
- Keep both Docker proxy networks internal and never publish port `2375`.
- Keep the Docker proxy base-image digest pinned and review the policy files whenever Docker metrics or restart behavior changes.
- Use unique production PostgreSQL, Redis, dashboard, bootstrap-token, signing-key, and OAuth secrets supplied outside source control.

## Authoritative Sources

- [MCP 2026-07-28 release announcement](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- [Streamable HTTP transport specification](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/basic/transports/streamable-http.mdx)
- [MCP 2026-07-28 schema](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/schema/2026-07-28/schema.ts)
- [Official MCP conformance suite](https://github.com/modelcontextprotocol/conformance)
