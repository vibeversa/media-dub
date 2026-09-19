# mTLS and Network Policy Spec

## mTLS

Service-to-service mTLS is disabled by default for local development
(`Security:MtlsEnabled=false`). Production topologies that terminate service
traffic inside the cluster mesh set:

```json
{
  "Security": {
    "MtlsEnabled": true,
    "CaPath": "/certs/ca.crt",
    "CertPath": "/certs/service.pfx"
  }
}
```

Startup validation fails fast when mTLS is enabled without both paths
(`SecurityOptionsValidator`). Certificates are mounted from the secret manager
at deploy time, never baked into images. External provider endpoints
(Azure/OpenAI/Google) remain TLS-1.2+ server-authenticated HTTPS via the
provider allowlist; no client-certificate material is logged or hashed.

## Required Kubernetes NetworkPolicies (implemented by the deploy task)

- Default deny-all ingress and egress in the application namespace.
- Allow `api` to PostgreSQL (5432), RabbitMQ (5672), Redis (6379), MinIO/S3
  (9000): TCP only, namespace-local selectors.
- Allow each worker role (`control`, `media-*`, `ai`, `gpu`, `export`,
  `maintenance`) to the same four backends; workers accept no ingress.
- Deny all other egress except the provider-endpoint allowlist
  (`ProviderEndpointValidator` hosts) over 443, so a compromised worker
  cannot exfiltrate elsewhere.
- DNS (UDP 53) allowed to the cluster resolver only.

These policies are defense in depth behind JWT/RBAC, tenant scoping, RLS, and
ownership checks; they never replace them.
