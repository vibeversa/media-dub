# The rehearsal rollout target (Task 043B). NOT a deployment image.
#
# `deploy/rollout/rehearsal/Dockerfile` exists for one reason: R5 requires a
# rollback to be REHEARSED, and a rehearsal needs two different builds of
# something that starts, answers its probes, and reports which build it is. The
# real API needs PostgreSQL, a broker, object storage, credentials and a schema
# to reach the point where its probes answer, which is a whole environment - and
# a rehearsal that needs a whole environment does not happen quarterly, it
# happens never.
#
# WHAT IT IS NOT: this image is never promoted, never tagged as a release, and
# never referenced by deploy/k8s. `scripts/check-rollout-window.mjs` does not
# read it, and nothing in the deploy order deploys it. It is test scaffolding
# that happens to be a container, and it lives under deploy/ only because that is
# where the rehearsal runner looks for it.
#
# WHY node AND NOT A BUSYBOX NETCAT LOOP
# ---------------------------------------
# The real Deployments declare `readOnlyRootFilesystem: true` and a numeric
# non-root uid, and a stand-in that only works as root would be rehearsing a
# weaker manifest than the one that ships. node needs no writable path outside
# the mounted /tmp, so the real securityContext applies unchanged.
FROM node:24-alpine

# The release this build serves at /version. It is an ARG so the two rehearsal
# revisions produce two DIFFERENT images: with a single build, `rollout undo`
# would be restoring a tag whose contents are the ones already running, and the
# check that the pods really went back would pass while proving nothing. That is
# not hypothetical - it is the first version of this Dockerfile, which took
# RELEASE as a build ARG that nothing consumed, and the rehearsal reported
# "serves rehearsal-unknown" for both revisions.
ARG REHEARSAL_RELEASE=rehearsal-unknown
ENV REHEARSAL_RELEASE=${REHEARSAL_RELEASE}

WORKDIR /app
COPY server.mjs ./server.mjs
COPY probe.mjs ./probe.mjs

# The stand-in for the EF migrations bundle the migration Job and the API's
# `wait-for-migrations` initContainer both run as `/app/efbundle`.
#
# It exists so the R1 rehearsal is real: the SAME initContainer, from the SAME
# committed manifest, with its real command path, blocks the rollout when this
# script exits non-zero. Nothing is patched to make it fail.
#
# `REHEARSAL_GATE` defaults to `pass` so the ordinary path needs no annotation.
RUN printf '%s\n' \
      '#!/bin/sh' \
      '# Stands in for the EF migrations bundle. See deploy/rollout/rehearsal/Dockerfile.' \
      'if [ "${REHEARSAL_GATE:-pass}" = "fail" ]; then' \
      '  echo "rehearsal: simulated migration failure" >&2' \
      '  exit 1' \
      'fi' \
      'echo "rehearsal: simulated migrations applied"' \
      'exit 0' \
      > /app/efbundle \
    && chmod 0755 /app/efbundle

# uid 1654 is the uid every backend workload in deploy/k8s runs as. Baking it
# here means the rehearsal does not have to relax `runAsUser` to start.
USER 1654:1654
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["node", "/app/server.mjs"]
