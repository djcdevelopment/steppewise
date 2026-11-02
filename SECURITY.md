# Security Policy

## Scope and Trust Boundary (`v0.1.0`)

Steppewise `v0.1.0` is a **local loopback reference host** (`dev` and `qa` profiles only) for deterministic Microsoft Agent Framework workflow execution, append-only event evidence, and SHA-256-bound human review:

- **Loopback-only listener**: Kestrel binds strictly to `IPAddress.Loopback` (`127.0.0.1`). Requests with non-loopback `Host` headers (`localhost` or `127.0.0.1` required) or cross-origin non-`GET` `Origin` headers are rejected with `HTTP 403`.
- **Production profile refused**: Setting `STEPPEWISE_ENV=prod` throws `InvalidOperationException` at startup until authentication and production policy qualification are implemented.
- **Reviewer attribution is not authentication**: Review decisions record `actor` and `actorSource` (`self-reported` when supplied in the request body, or `local-process-user` from `Environment.UserName`). Do not expose the loopback port to untrusted networks or multi-tenant hosts.
- **Deterministic dependency lock**: NuGet dependencies are pinned in `src/Steppewise/packages.lock.json` and restored with `RestoreLockedMode=true`.

## Supported Versions

| Version | Supported |
|---|---|
| `0.1.x` | Yes |

## Reporting a Vulnerability

Please report security issues privately through [GitHub Security Advisories](https://github.com/djcdevelopment/steppewise/security/advisories/new) rather than opening a public issue. Include reproduction steps, environment details, and the affected commit hash (`GET /api/info`).
