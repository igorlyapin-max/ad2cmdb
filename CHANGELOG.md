# Changelog

## Unreleased

- Added per-user CMDBuild failure isolation so one failed user operation no longer stops the whole sync batch.
- Added `failedUsers` to sync summaries and partial-failure reporting in `/sync/status`.
- Added CMDBuild transient retry settings.
- Added local sync instance lock, state backup, and backup recovery for the file-based state store.
- Added production guards for LDAP certificate bypass, CMDBuild HTTPS, and wildcard `AllowedHosts`.
- Added `/ready` readiness endpoint and fixed-window rate limiting for status endpoints.
- Added GitLab CI and a lightweight local test harness.
- Replaced stale `PROJECT_DOCUMENTATION.md` content from the unrelated `cmdb2monitoring` project.
