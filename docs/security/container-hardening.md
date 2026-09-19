# Container Hardening Checklist

Applies to every image (`Dockerfile.api`, `Dockerfile.worker.*`,
`Dockerfile.maintenance`).

- [ ] Non-root runtime user (`USER app`; verified in Dockerfiles).
- [ ] Read-only root filesystem compatible: the app writes only to `/tmp`
      (`TMPDIR=/tmp`; artifact buffering spills to the temp path and cleans up
      on completion). Deploy with `readOnlyRootFilesystem: true` plus a
      writable `emptyDir` mounted at `/tmp`.
- [ ] Restricted writable volumes: only `/tmp` is writable; no host mounts.
- [ ] No unnecessary egress: image contains no shell-out helpers beyond the
      app plus `curl` for the compose healthcheck; production NetworkPolicies
      deny egress except the provider allowlist (see `mtls.md`).
- [ ] Resource limits: every workload declares CPU/memory requests and limits
      (API and GPU roles sized per the deploy task); `Media:MaxConcurrentMediaJobs`
      plus the FFmpeg concurrency gate bound media CPU inside the container.
- [ ] Image scanning (Trivy), SBOM (anchore), and signing (cosign) are required
      in CI (wired by the CI/CD task); unsigned or high-severity images do not
      promote.
- [ ] No secrets in layers: connection strings and provider keys arrive via
      environment/secret manager at runtime; `curl` healthchecks carry no
      credentials; diagnostics endpoints (`/metrics`) are scraped inside the
      cluster only.
- [ ] `DOTNET_EnableDiagnostics=0` in production images.
