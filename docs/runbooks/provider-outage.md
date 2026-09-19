# Provider Outage

## Symptoms

- Alert `ProviderErrorRateHigh` (`provider.errors / provider.calls > 5%`).
- Provider-health dashboard shows one provider red; `IsHealthy=false`.

## Checks

```bash
curl -f http://localhost:8080/metrics | grep -E "provider_calls|provider_errors"
curl -H "Authorization: Bearer $ADMIN_JWT" \
  "http://localhost:8080/api/v1/admin/provider-executions/prov_<hex>"
```

PromQL: `rate(provider_errors_total[15m]) by (provider)`.

## Remediation

1. Confirm fallback engaged (secondary provider `route_selected_total` rises).
2. If all providers fail, pause dispatch (set route priority to mock) to
   preserve budgets.
3. Rotate keys only via secret manager (never commit `CHANGE_ME` values);
   validate with `ProviderHealthTracker.ValidateConfig`.
4. Re-enable after error rate < 5% for 15m.

## Escalation

L2 (1h) → L3 provider (4h) when vendor-side. See `escalation.md`.
