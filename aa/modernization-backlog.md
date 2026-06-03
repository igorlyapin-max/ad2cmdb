# Modernization Backlog

Date: 2026-06-03.

## Current Target

This iteration targets production readiness for the existing `adgroups2cmdbuild` service and `bootstrap-ad-groups` deployment tool, without broad refactoring.

Completion criteria:

- no open P0/P1 gaps from the baseline audit;
- P2/P3 gaps recorded with next action;
- local build/test gates pass or failures are explained.

## Baseline

Project type: .NET integration service plus one-shot deployment tool.

Inspected areas:

- service entry point, options validation, AD and CMDBuild clients;
- sync state, local lock, retry, shutdown, health/readiness/status endpoints;
- bootstrap tool;
- Dockerfile, GitLab CI, README, deployment docs and architecture maps;
- local test harness.

## Closed P1 Gaps

| ID | Gap | Evidence | Remediation |
| --- | --- | --- | --- |
| MOD-P1-001 | Production AD simple bind could run without LDAPS. | `src/adgroups2cmdbuild/Program.cs`; `tools/bootstrap-ad-groups/Program.cs`; default `ActiveDirectory:UseSsl=false` in base config. | Added production guard requiring `ActiveDirectory:UseSsl=true` in both service and bootstrap tool. |
| MOD-P1-002 | Production readiness could stay shallow for a dependency-heavy service. | `Readiness:CheckDependencies=false` default. | Added production guard requiring readiness enabled with dependency checks. |
| MOD-P1-003 | `AllowedHosts` guard only rejected a single exact `*`. | `src/adgroups2cmdbuild/Program.cs`. | Added wildcard detection inside semicolon-separated host lists. |
| MOD-P1-004 | One verbose AD debug log emitted raw login despite redaction contract. | `src/adgroups2cmdbuild/ActiveDirectory/ActiveDirectoryClient.cs`. | Routed the log value through `DebugOptions.FormatSensitive`. |
| MOD-P1-005 | Root docs described unrelated `cmdb2monitoring` services and contracts. | `DEPLOYMENT_LOCAL_REGISTRY.md`; `CMDBUILD_REST_API_INTEGRATION.md`. | Replaced both files with ad2cmdb-specific deployment and CMDBuild REST contract docs. |

## Closed P2/P3 Gaps

| ID | Priority | Gap | Evidence | Remediation |
| --- | --- | --- | --- | --- |
| MOD-P2-001 | P2 | Docker runtime image runs as root. | `deploy/dockerfiles/adgroups2cmdbuild.Dockerfile`. | Runtime stage now creates non-root `ad2cmdb` user/group with UID/GID `64100`, owns `/app/state`, and runs the service as `USER ad2cmdb`. |
| MOD-P2-002 | P2 | Operational API contract is documented in Markdown but not machine-readable. | `/health`, `/ready`, `/sync/status`. | Added `aa/contracts/operational-api.openapi.json` and a contract test that checks endpoint responses and required response schemas. |
| MOD-P2-004 | P2 | Bootstrap tool lacks retry/backoff for transient CMDBuild/LDAP failures. | `tools/bootstrap-ad-groups/Program.cs`. | Added bounded retry/backoff around CMDBuild role reads plus AD bind/search/create operations and extracted deterministic bootstrap naming/selection logic for tests. |
| MOD-P3-001 | P3 | Documentation versioning is manual. | Root and `aa/` documentation. | Decision recorded: documentation version remains explicit and release-coupled for now; automation is deferred until release tagging/publishing is formalized. |

## Remaining Backlog

| ID | Priority | Gap | Next action |
| --- | --- | --- | --- |
| MOD-P2-003 | P2 | Alert/dashboard definitions are recommendations only. | Add deployable alert rules or platform-specific dashboard artifacts when target monitoring platform is known. |

## Accepted Risk

The file-based state store remains accepted for this iteration because the service is explicitly documented for one active replica only.
Active-active operation requires a shared durable state design and is not part of this pass.

Deployable monitoring artifacts remain open because the target monitoring platform and rule format are not defined yet.
