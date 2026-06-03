# Changelog

## Unreleased

- Added per-user CMDBuild failure isolation so one failed user operation no longer stops the whole sync batch.
- Added `failedUsers` to sync summaries and partial-failure reporting in `/sync/status`.
- Added CMDBuild transient retry settings.
- Added Active Directory transient retry settings and shared retry backoff jitter.
- Added graceful shutdown waiting for an active sync run before cancellation.
- Added local sync instance lock, state backup, and backup recovery for the file-based state store.
- Added production guards for LDAP certificate bypass, CMDBuild HTTPS, and wildcard `AllowedHosts`.
- Strengthened production guards to require LDAPS for AD simple bind, dependency readiness, and wildcard checks inside `AllowedHosts` lists.
- Applied the same production transport guards to `bootstrap-ad-groups`.
- Added non-root Docker runtime user for the service image.
- Added OpenAPI contract for `/health`, `/ready`, and `/sync/status`.
- Added transient retry/backoff to `bootstrap-ad-groups` CMDBuild role reads and LDAP bind/search/create operations.
- Extracted deterministic bootstrap AD group selection and naming logic for focused tests.
- Fixed one verbose AD debug log path that emitted a raw login despite default sensitive-value redaction.
- Added `/ready` readiness endpoint and fixed-window rate limiting for status endpoints.
- Added GitLab CI and a lightweight local test harness.
- Hardened GitLab CI with validate checks and blocking vulnerable package checks.
- Replaced stale `PROJECT_DOCUMENTATION.md` content from the unrelated `cmdb2monitoring` project.
- Replaced stale root deployment/API docs from the unrelated `cmdb2monitoring` project.
