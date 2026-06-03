# Monitoring Artifacts

These artifacts provide deployable starting points for `adgroups2cmdbuild` monitoring.
They assume the service exposes its operational API at `http://adgroups2cmdbuild:8080`.

## Files

| File | Purpose |
| --- | --- |
| `zabbix-adgroups2cmdbuild-template.yaml` | Zabbix template with HTTP checks, sync status items, and triggers |
| `prometheus-json-exporter-adgroups2cmdbuild.yaml` | Prometheus json_exporter module for `/sync/status` |
| `prometheus-adgroups2cmdbuild-rules.yaml` | Prometheus alert rules for health, readiness, stale sync, and partial failures |
| `grafana-adgroups2cmdbuild-dashboard.json` | Grafana dashboard for service status and sync outcomes |

## Expected Signals

- `/health` returns HTTP `200` and JSON `status=ok`.
- `/ready` returns HTTP `200` when the service is operational.
- `/sync/status` exposes `isRunning`, `lastCompletedUtc`, `lastSucceeded`, and `lastSummary.failedUsers`.
- Logs remain available through console/stdout and optionally ELK.

## Rollout Notes

- For Zabbix, import the template and set `{$AD2CMDB.URL}` on the host or template.
- For Prometheus, configure blackbox exporter probes for `/health` and `/ready`, and json_exporter for `/sync/status`.
- Adjust stale-sync thresholds to match `Sync:IntervalSeconds`; defaults assume a 5 minute sync interval.
