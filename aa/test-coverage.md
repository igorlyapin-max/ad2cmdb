# Test Coverage

Date: 2026-06-03.

This project uses a lightweight executable test harness instead of external NuGet test packages.
Coverage is tracked by behavior and contract area, not by line percentage.

## Automated Coverage

| Area | Covered by tests |
| --- | --- |
| Sync behavior | Per-user failure isolation, missing AD group, missing CMDBuild role, disabled creation, dry-run, deprovisioning |
| State | Backup recovery and save/no-save decisions |
| Runtime startup | Production guard failures, safe production startup, development health/readiness without dependencies |
| Operational API | `/health`, `/ready`, `/sync/status` runtime JSON and OpenAPI required fields |
| CMDBuild REST | Retry status behavior, create/update/disable request contract, unmanaged group preservation |
| Resilience | Exponential backoff cap and transient retry selection |
| Shutdown | Graceful wait and forced cancellation after grace period |
| Debug/observability | Sensitive-value redaction, ELK options, ELK HTTP event shape |
| Secrets | `*Secret` companion references, PAM compatibility env mapping, Indeed PAM AAPM HTTP response parsing |
| Bootstrap tool | Help output, role selection precedence, AD naming/DN escaping logic |
| Runtime packaging | Static Dockerfile non-root policy check |
| Monitoring artifacts | Zabbix and Prometheus/Grafana artifact presence and key alerts |

## Manual Or Environment Coverage

These areas require real external systems or platform-specific access and are documented as smoke checks:

- Real LDAP/LDAPS bind/search/range-read against Microsoft Active Directory.
- Real CMDBuild REST v3 role/user reads and user create/update/disable operations.
- Real PAM/AAPM endpoint behavior beyond local HTTP contract simulation.
- Full Docker image build when Docker daemon and NuGet access are available.
- Imported Zabbix template behavior and Prometheus/Grafana rule execution in the target monitoring stack.

## Current Gates

```bash
bash -n scripts/dotnet
bash -n scripts/bootstrap-ad-groups.sh
./scripts/dotnet build src/adgroups2cmdbuild/adgroups2cmdbuild.csproj -v minimal
./scripts/dotnet build tools/bootstrap-ad-groups/bootstrap-ad-groups.csproj -v minimal
./scripts/dotnet run --project tests/adgroups2cmdbuild.tests/adgroups2cmdbuild.tests.csproj
./scripts/bootstrap-ad-groups.sh --help
git diff --check
```
