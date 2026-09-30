#!/usr/bin/env bash
# The hosting gate (Task 043, instruction 1 / Testing).
#
# WHAT IT ASSERTS
# ---------------
# The response headers of the ACTUAL built image, and the topology that keeps a
# browser away from the data stores. Both are read from a running container
# rather than from a config file, because that is the only form in which they can
# be believed:
#
#   * nginx `add_header` inheritance depends on WHICH `location` block matched. A
#     config that says `add_header Cache-Control "no-cache"` for `/index.html`
#     serves a cacheable document to `/` if the block ordering is wrong, and the
#     file reads correctly the whole time.
#   * A policy file can claim `policyTypes: [Ingress, Egress]` and a default-deny
#     can coexist with an allow that opens the wrong port to the wrong peer. Only
#     a resolved policy says what traffic is actually permitted.
#
# So: build the image, run it, curl a header matrix against it; then parse the
# NetworkPolicies and assert the shape. The static tier (Docker unavailable) is
# reported as `HOSTING_IMAGE_UNVERIFIED` rather than as a pass - see the note in
# "Tiers" below, which is the one place this script does not fail closed.
#
# MACHINE-READABLE OUTPUT
# -----------------------
#   HOSTING_GATE_RESULT reason=<REASON> status=<PASS|FAIL|SKIP> exit=<n>
#
#   OK                          every tier passed
#   HEADER_ASSERTION_FAILED     a served response did not match the policy
#   TOPOLOGY_VIOLATION          a NetworkPolicy permits a forbidden path
#   IMAGE_BUILD_FAILED          the image could not be built
#   HOSTING_INPUT_MISSING       a required file could not be read
#   HOSTING_TOOL_UNAVAILABLE    a required tool is absent (docker, node, python3)
#
#   plus a per-tier verdict line, so a run with one green tier and one red one is
#   legible without parsing the prose.
#
# TIERS
# -----
#   docker  build + run the image, assert the header matrix. Skipped with
#           HOSTING_IMAGE_UNVERIFIED when docker is absent.
#   static  read deploy/cdn/origin.json, deploy/nginx/default.conf and
#           deploy/k8s/networkpolicies.yaml, and assert the policy shape.
#
# The static tier is the one that can run anywhere and it is NOT optional: a
# missing policy file is HOSTING_INPUT_MISSING, a failure. The docker tier skips
# because it is a tool-availability decision rather than a check-availability
# one, which is the same distinction `deploy/verify.sh` draws for kubectl. The
# distinction matters: the static tier skipping would mean a repository with no
# hosting policy reports a green hosting gate, and nobody would notice until the
# deploy.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IMAGE_TAG="${HOSTING_IMAGE_TAG:-dubbing-frontend-hosting-test}"
CONTAINER_NAME="${HOSTING_CONTAINER_NAME:-dubbing-frontend-hosting-test}"
CONTAINER_PORT="${HOSTING_CONTAINER_PORT:-18080}"
KEEP_CONTAINER="${HOSTING_KEEP_CONTAINER:-0}"

REASONS_OK="OK"
REASON_HEADER="HEADER_ASSERTION_FAILED"
REASON_TOPOLOGY="TOPOLOGY_VIOLATION"
REASON_BUILD="IMAGE_BUILD_FAILED"
REASON_INPUT="HOSTING_INPUT_MISSING"
REASON_TOOL="HOSTING_TOOL_UNAVAILABLE"
REASON_UNVERIFIED="HOSTING_IMAGE_UNVERIFIED"

TIER_DOCKER="SKIP"
TIER_STATIC="SKIP"
DOCKER_NOTE="not attempted"
STATIC_NOTE="not attempted"
REASON="$REASONS_OK"

PASS=0
FAIL=0

ok()   { PASS=$((PASS + 1)); printf '  ok    %s\n' "$1"; }
bad()  { FAIL=$((FAIL + 1)); printf '  FAIL  %s\n' "$1"; }
note() { printf '  ..    %s\n' "$1"; }
have() { command -v "$1" >/dev/null 2>&1; }

# ============================================================================
# helpers
# ============================================================================

# The header matrix. Each row is `path|expected-status|header|expected-substring`.
# Read as DATA rather than as a series of hand-written `if` blocks so that adding
# a case is one line, and so the whole matrix is visible in one place.
#
# The fourth field is a substring, not an equality, because `Cache-Control` and
# `Content-Security-Policy` legitimately carry several directives and a
# hand-maintained full-string comparison is a test that fails on a harmless edit
# and is then "fixed" by being deleted.
#
# ONLY PATHS THAT ALWAYS EXIST ARE HERE. A hashed asset, an unhashed asset and
# `/version.json` all depend on what a given build emitted, so they are
# discovered from the running container further down. Hard-coding
# `/assets/index-ABC123.js` is a matrix that goes stale on the next release and is
# then either deleted or "fixed" by loosening the expectation - and a hard-coded
# `/favicon.svg` was exactly that: no Vite build in this repository emits one, so
# the row asserted a 404 where the policy says 200 and the gate was red for a
# reason that had nothing to do with the policy.
readonly HEADER_MATRIX=(
  '/|200|Cache-Control|no-cache'
  '/index.html|200|Cache-Control|no-cache'
  # The client-router deep link. The single most valuable row: it is the only one
  # that fails if the SPA fallback is removed, and a removed fallback is invisible
  # until somebody shares a link.
  '/projects/prj_01HZYABCDEFG/workspace|200|Cache-Control|no-cache'
  # `/api/` is a different origin's job.
  '/api/v1/projects|404|Cache-Control|no-store'
)

# Asserted on EVERY response, whatever the path. A header that is only present on
# the document is a header that is absent on the 404, which is the response a
# scanner and a browser both see when something goes wrong.
readonly ALWAYS_HEADERS=(
  'Content-Security-Policy|script-src'
  'Content-Security-Policy|object-src'
  'Referrer-Policy|no-referrer'
  'X-Content-Type-Options|nosniff'
  'X-Frame-Options|DENY'
)

# Substrings that must appear in NO response.
readonly FORBIDDEN_HEADERS=(
  'Content-Security-Policy|unsafe-inline'
  'Content-Security-Policy|unsafe-eval'
  'Server|nginx/'
)

# Extract one header's value from a captured response. The LAST occurrence wins,
# because a proxy chain or a redirect appends headers and the final response's
# value is the one the browser sees.
header_value() { # file name
  tr -d '\r' < "$1" | awk -v want="$2" '
    { colon = index($0, ":"); if (colon == 0) next;
      if (substr($0, 1, colon - 1) == want) { value = substr($0, colon + 2) } }
    END { print value }'
}

# The status of the LAST response in a captured header block. `1xx`/`2xx` from an
# intermediate hop is not the answer; curl records each, and reading the last line
# is what makes a redirect chain report its destination.
status_of() { # file
  tr -d '\r' < "$1" | awk '/^HTTP\// { code = $2 } END { print code }'
}

# Fetch a response and read its status, one header, and a body prefix — all from
# the SAME request. Three separate curls would triple the traffic and could
# interleave, which is exactly the class of bug this gate is looking for.
#
# Sets three globals rather than printing lines to stdout. The first version
# printed three lines and the callers read them with
# `IFS=$'\n' read -r status value body < <(probe ...)` — and `read` consumes ONE
# line, so `value` and `body` were always empty. Every Cache-Control assertion
# then failed with "header is ''" against an origin that was serving the header
# correctly, and the obvious conclusion was that the ORIGIN was broken.
#
# It is a silent-plumbing failure with the shape of a product failure, which is
# why the fix is globals rather than a cleverer read.
PROBE_STATUS=''
PROBE_VALUE=''
PROBE_PREFIX=''
probe() { # base path header
  curl --silent --max-time 10 \
    --output "$PROBE_BODY_FILE" --dump-header "$PROBE_HEADERS_FILE" \
    "$1$2" >/dev/null 2>&1 || true
  PROBE_STATUS="$(status_of "$PROBE_HEADERS_FILE")"
  PROBE_VALUE="$(header_value "$PROBE_HEADERS_FILE" "$3")"
  PROBE_PREFIX="$(head -c 80 "$PROBE_BODY_FILE" 2>/dev/null | tr '\n' ' ')"
}

# Fetch with an explicit Accept-Encoding and report the Content-Encoding the
# server chose. Separate from `probe` because the header under test is itself the
# answer to "did the server honour the request", which is a different question
# from "what did it send".
probe_encoding() { # base path
  curl --silent --max-time 10 --output /dev/null --dump-header "$PROBE_HEADERS_FILE" \
    --header 'Accept-Encoding: gzip' "$1$2" >/dev/null 2>&1 || true
  header_value "$PROBE_HEADERS_FILE" "Content-Encoding"
}

# ============================================================================
# the static tier - policy shape, runnable anywhere
# ============================================================================
run_static_tier() {
  local origin_json="$ROOT/deploy/cdn/origin.json"
  local nginx_conf="$ROOT/deploy/nginx/default.conf"
  local policies="$ROOT/deploy/k8s/networkpolicies.yaml"
  local snippet="$ROOT/deploy/nginx/security-headers.conf"
  local frontend_env="$ROOT/frontend/src/config/env.ts"
  local api_headers="$ROOT/src/DubbingPlatform.Api/Middleware/SecurityHeadersMiddleware.cs"

  local missing=()
  for required in "$origin_json" "$nginx_conf" "$snippet" "$policies" "$frontend_env" "$api_headers"; do
    if [ ! -f "$required" ]; then missing+=("$required"); fi
  done
  if [ "${#missing[@]}" -ne 0 ]; then
    for path in "${missing[@]}"; do bad "missing required file: ${path#"$ROOT"/}"; done
    TIER_STATIC="FAIL"
    REASON="$REASON_INPUT"
    STATIC_NOTE="${#missing[@]} required file(s) absent"
    return
  fi
  ok "all seven required policy files are present"

  if ! have node; then
    bad "node is required for the static tier (the policy rules live in tools/hosting-policy.mjs)"
    TIER_STATIC="FAIL"
    REASON="$REASON_TOOL"
    STATIC_NOTE="node not installed"
    return
  fi

  # The decision layer's own verdict over the real files. Unit-tested in
  # tools/hosting-policy.test.mjs; this is the same functions reading the
  # committed artefacts rather than inline fixtures.
  local verdict
  verdict="$(node --input-type=module -e '
    import { readFileSync } from "node:fs";
    import { evaluateHosting } from "./tools/hosting-policy.mjs";

    const read = (path) => readFileSync(path, "utf8");
    const origin = JSON.parse(read("deploy/cdn/origin.json"));

    // The API header set, extracted from the middleware source rather than
    // transcribed. A transcribed copy in the test would agree with itself and
    // prove nothing; this reads the constants the API actually sends.
    const middleware = read("src/DubbingPlatform.Api/Middleware/SecurityHeadersMiddleware.cs");
    const constant = (name) => {
      const match = new RegExp(name + String.raw`\s*=\s*\n?\s*"([^"]*)"`).exec(middleware);
      return match === null ? undefined : match[1];
    };
    const apiHeaders = {
      "Content-Security-Policy": constant("ContentSecurityPolicy"),
      "Referrer-Policy": constant("ReferrerPolicy"),
      "X-Content-Type-Options": "nosniff",
      "X-Frame-Options": "DENY",
    };

    const result = evaluateHosting({
      originConfig: origin,
      nginxText: read("deploy/nginx/default.conf"),
      securitySnippet: read("deploy/nginx/security-headers.conf"),
      apiSecurityHeaders: apiHeaders,
      responses: [],
    });
    for (const problem of result.problems) console.log("  FAIL  " + problem);
    console.log(JSON.stringify({ ok: result.ok, count: result.problems.length }));
  ' 2>&1)" || true

  local json_line
  json_line="$(printf '%s\n' "$verdict" | tail -n 1)"
  if printf '%s\n' "$verdict" | grep -q '^  FAIL'; then
    printf '%s\n' "$verdict" | grep '^  FAIL'
    FAIL=$((FAIL + $(printf '%s\n' "$verdict" | grep -c '^  FAIL')))
    bad "the hosting policy over the committed files is not valid"
    TIER_STATIC="FAIL"
    REASON="$REASON_HEADER"
    STATIC_NOTE="policy invalid"
  else
    ok "the hosting policy over the committed files is valid ($(printf '%s' "$json_line" | sed 's/.*"count"://;s/}//') problem(s))"
    TIER_STATIC="PASS"
    STATIC_NOTE="policy valid"
  fi

  # --- topology (R4) --------------------------------------------------------
  # Parsed rather than grepped. A grep cannot answer "is there a default deny" -
  # it answers "does the word appear", which a comment satisfies. The assertions
  # below each name the failure they prevent.
  if ! have python3; then
    bad "python3 is required to parse the NetworkPolicies (a grep cannot answer whether a default-deny exists)"
    [ "$TIER_STATIC" = "PASS" ] && TIER_STATIC="FAIL"
    REASON="$REASON_TOOL"
    STATIC_NOTE="${STATIC_NOTE}; python3 missing"
    return
  fi

  # The topology analysis runs in a SEPARATE file rather than a heredoc. A heredoc
  # inside a `$( )` in a script that also uses `set -e` has a failure mode that is
  # invisible: a Python SyntaxError writes a traceback to stderr, the command
  # substitution captures it, `grep -c '^FAIL'` finds zero, and the tier reports
  # a PASS having asserted nothing. That is the same class of silent pass this
  # task exists to prevent, produced by the gate's own plumbing.
  local topo_script
  topo_script="$(dirname "${BASH_SOURCE[0]}")/hosting-topology.py"
  if [ ! -f "$topo_script" ]; then
    bad "deploy/tests/hosting-topology.py is missing, so the topology was not checked at all"
    TIER_STATIC="FAIL"
    REASON="$REASON_INPUT"
    STATIC_NOTE="topology analyser missing"
    return
  fi

  # The analyser's exit code is honoured, and a non-zero is a FAILURE rather
  # than an empty result. A traceback on stderr plus `grep -c '^FAIL'` returning
  # zero is how an analyser that checked nothing reports a pass.
  local topo topo_status
  set +e
  topo="$(python3 "$topo_script" "$ROOT/deploy/k8s" 2>&1)"
  topo_status=$?
  set -e

  if [ "$topo_status" -ne 0 ]; then
    printf '%s\n' "$topo" | sed 's/^/        /'
    bad "the topology analyser exited $topo_status. It did not complete, so the topology was not checked."
    TIER_STATIC="FAIL"
    REASON="$REASON_TOOL"
    STATIC_NOTE="topology analyser failed"
    return
  fi

  local topo_failures
  topo_failures="$(printf '%s\n' "$topo" | grep -c '^FAIL:' || true)"
  if [ "$topo_failures" -gt 0 ]; then
    printf '%s\n' "$topo" | grep '^FAIL:' | sed 's/^FAIL: /  FAIL  /'
    FAIL=$((FAIL + topo_failures))
    bad "the network topology permits a forbidden path ($topo_failures problem(s))"
    TIER_STATIC="FAIL"
    REASON="$REASON_TOPOLOGY"
    STATIC_NOTE="topology violation"
  else
    printf '%s\n' "$topo" | grep '^ok' | sed 's/^ok  */  ok    /'
    ok "the network topology enforces Browser->CDN->Static->API with no path to a datastore"
    STATIC_NOTE="${STATIC_NOTE}; topology ok"
  fi
}

# ============================================================================
# the docker tier - the actual headers from the actual image
# ============================================================================
run_docker_tier() {
  if ! have docker; then
    TIER_DOCKER="SKIP"
    DOCKER_NOTE="docker not installed; the header matrix was not verified against a running origin"
    return
  fi
  if ! docker info >/dev/null 2>&1; then
    # A present-but-unusable docker is a different situation from an absent one,
    # and it is what a developer without Docker Desktop running gets. The
    # distinction is printed so nobody reads the SKIP as "checked and fine".
    TIER_DOCKER="SKIP"
    DOCKER_NOTE="docker is installed but no daemon is reachable; the header matrix was not verified"
    return
  fi

  # `--file Dockerfile.frontend` with the repository root as the context, exactly
  # as the release pipeline builds it. A gate that built a different image from
  # a different context would be verifying something nobody ships.
  note "building $IMAGE_TAG (this is the same command the release pipeline runs)"
  if ! docker build --file "$ROOT/Dockerfile.frontend" --tag "$IMAGE_TAG" "$ROOT" >"$BUILD_LOG" 2>&1; then
    bad "docker build -f Dockerfile.frontend . failed; last 20 lines:"
    tail -20 "$BUILD_LOG" | sed 's/^/        /'
    TIER_DOCKER="FAIL"
    REASON="$REASON_BUILD"
    DOCKER_NOTE="image build failed"
    return
  fi
  ok "docker build -f Dockerfile.frontend ."

  # The image serves the document, and `/version.json` is written into the build
  # output by deploy/config-inject.sh. When it is absent (a local build that did
  # not inject), the tier reports the header for the document and marks the
  # version document as unverifiable rather than asserting a 200 it cannot have.
  note "starting the origin on 127.0.0.1:$CONTAINER_PORT"
  docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  if ! docker run --detach --name "$CONTAINER_NAME" \
      --publish "127.0.0.1:$CONTAINER_PORT:8080" "$IMAGE_TAG" >/dev/null 2>&1; then
    bad "the origin container did not start"
    TIER_DOCKER="FAIL"
    REASON="$REASON_BUILD"
    DOCKER_NOTE="container did not start"
    return
  fi

  # Wait on the healthcheck rather than a fixed sleep. A fixed sleep is either
  # too short on a slow machine (a flaky gate) or wasteful on a fast one.
  local ready=0
  for _ in $(seq 1 30); do
    if curl --silent --output /dev/null --max-time 2 "http://127.0.0.1:$CONTAINER_PORT/" 2>/dev/null; then
      ready=1
      break
    fi
    sleep 1
  done
  if [ "$ready" != "1" ]; then
    bad "the origin did not answer on 127.0.0.1:$CONTAINER_PORT within 30s; container log:"
    docker logs --tail 20 "$CONTAINER_NAME" 2>&1 | sed 's/^/        /'
    docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
    TIER_DOCKER="FAIL"
    DOCKER_NOTE="origin never became ready"
    REASON="$REASON_HEADER"
    return
  fi
  ok "the origin is serving"

  local base="http://127.0.0.1:$CONTAINER_PORT"
  local tier_failures=0

  # --- the matrix ------------------------------------------------------------
  local row path want_status header want_substring
  for row in "${HEADER_MATRIX[@]}"; do
    IFS='|' read -r path want_status header want_substring <<< "$row"
    probe "$base" "$path" "$header"
    if [ "$PROBE_STATUS" != "$want_status" ]; then
      bad "GET $path -> $PROBE_STATUS (expected $want_status)"
      tier_failures=$((tier_failures + 1))
      continue
    fi
    if ! printf '%s' "$PROBE_VALUE" | grep -qF -- "$want_substring"; then
      bad "GET $path: $header is '$PROBE_VALUE' (expected to contain '$want_substring')"
      tier_failures=$((tier_failures + 1))
      continue
    fi
    ok "GET $path -> $want_status, $header contains '$want_substring'"
  done

  # --- a missing hashed asset is a 404 and NOT the document -------------------
  # The bug this whole tier exists to catch. `index-DOESNOTEXIST-0000` cannot
  # collide with a real content hash.
  probe "$base" "/assets/index-DOESNOTEXIST-0000.js" "Content-Type"
  if [ "$PROBE_STATUS" = "200" ] && printf '%s' "$PROBE_PREFIX" | grep -qi '<!doctype html'; then
    bad "a missing hashed asset returned the HTML document (200 text/html). The browser would try to" \
        "execute it as JavaScript and the resulting SyntaxError names the frontend, not the CDN."
    tier_failures=$((tier_failures + 1))
  else
    ok "a missing hashed asset is $PROBE_STATUS, not the HTML document"
  fi

  # --- a source map is refused -------------------------------------------------
  probe "$base" "/assets/index-DOESNOTEXIST-0000.js.map" "Content-Type"
  if [ "$PROBE_STATUS" != "404" ]; then
    bad "a source map request returned $PROBE_STATUS (expected 404); a published map is a published copy of the source"
    tier_failures=$((tier_failures + 1))
  else
    ok "a source map request is 404"
  fi

  # --- /api/ is not this origin's business -------------------------------------
  probe "$base" "/api/v1/projects" "Content-Type"
  if [ "$PROBE_STATUS" != "404" ]; then
    bad "GET /api/v1/projects returned $PROBE_STATUS from the STATIC origin (expected 404). The API is a" \
        "separate host; a 200 of the SPA document here makes the client parse HTML as JSON."
    tier_failures=$((tier_failures + 1))
  else
    ok "GET /api/v1/projects is 404 on the static origin"
  fi

  # --- security headers on every response --------------------------------------
  # Checked on the document AND on a 404. `add_header … always` exists precisely
  # because nginx drops headers on error responses by default, and a header that
  # is missing from the 404 is missing from the response a user sees when
  # something has gone wrong.
  local entry name want
  for entry in "${ALWAYS_HEADERS[@]}"; do
    IFS='|' read -r name want <<< "$entry"
    probe "$base" "/" "$name"
    if [ -z "$PROBE_VALUE" ]; then
      bad "$name is absent from a 200 response"
      tier_failures=$((tier_failures + 1))
    elif ! printf '%s' "$PROBE_VALUE" | grep -qF -- "$want"; then
      bad "$name is '$PROBE_VALUE' on a 200 (expected to contain '$want')"
      tier_failures=$((tier_failures + 1))
    else
      ok "$name contains '$want' on a 200"
    fi
  done

  # --- forbidden directives -----------------------------------------------------
  for entry in "${FORBIDDEN_HEADERS[@]}"; do
    IFS='|' read -r name want <<< "$entry"
    probe "$base" "/" "$name"
    if printf '%s' "$PROBE_VALUE" | grep -qF -- "$want"; then
      bad "$name contains '$want'"
      tier_failures=$((tier_failures + 1))
    else
      ok "$name does not contain '$want'"
    fi
  done

  # --- a real hashed asset: immutable, and gzip when asked ----------------------
  # The asset name is read from the RUNNING CONTAINER's own dist, not from the
  # developer's working tree and not from a guess. The container is the thing
  # being verified, so it is also the only authority on what it contains: a
  # locally-present `frontend/dist` may be a different build entirely.
  local hashed_asset
  hashed_asset="$(docker exec "$CONTAINER_NAME" sh -c 'ls -1 /usr/share/nginx/html/assets 2>/dev/null | grep -E "\.js$" | head -n 1' 2>/dev/null | tr -d '\r' || true)"
  if [ -n "$hashed_asset" ]; then
    probe "$base" "/assets/$hashed_asset" "Cache-Control"
    if printf '%s' "$PROBE_VALUE" | grep -qF -- 'immutable' \
       && printf '%s' "$PROBE_VALUE" | grep -qF -- '31536000'; then
      ok "a hashed asset is served immutable for a year ($hashed_asset)"
    else
      bad "a hashed asset has Cache-Control '$PROBE_VALUE' ($hashed_asset); expected immutable for a year."
      bad "  A mutable cache header on a content-hashed name means a browser revalidates an asset whose"
      bad "  bytes never change - and a CDN that evicts it re-fetches it for every user."
      tier_failures=$((tier_failures + 1))
    fi

    # `gzip_min_length` is 1024 and a Vite index.html is comfortably under it, so
    # asserting compression on the document would assert nothing and read as
    # coverage. The entry chunk is hundreds of kilobytes.
    local asset_encoding
    asset_encoding="$(probe_encoding "$base" "/assets/$hashed_asset")"
    if [ "$asset_encoding" = "gzip" ]; then
      ok "a hashed asset is served gzip-encoded with Accept-Encoding: gzip"
    else
      bad "a hashed asset is not gzip-encoded (Content-Encoding: '${asset_encoding:-none}') despite Accept-Encoding: gzip"
      tier_failures=$((tier_failures + 1))
    fi
  else
    bad "the running container serves no JavaScript under /usr/share/nginx/html/assets. A Vite build"
    bad "  always emits the entry chunk there, so an empty directory means the build output is not what"
    bad "  was copied - and every assertion about the asset cache classes has just checked nothing."
    tier_failures=$((tier_failures + 1))
  fi

  # --- the runtime version document -------------------------------------------
  # `/version.json` is written into the build output by deploy/config-inject.sh,
  # so its presence depends on whether the image was built with an injection. A
  # missing one is reported, not failed: the local build path legitimately has
  # none, and the release build's assertion is `config-inject.sh` failing rather
  # than this.
  probe "$base" "/version.json" "Cache-Control"
  if [ "$PROBE_STATUS" = "200" ]; then
    if printf '%s' "$PROBE_VALUE" | grep -qF -- 'no-cache'; then
      ok "/version.json is served and uncached"
    else
      bad "/version.json has Cache-Control '$PROBE_VALUE'; a cached version document answers yesterday's question"
      tier_failures=$((tier_failures + 1))
    fi
  elif [ "$PROBE_STATUS" = "404" ]; then
    # A 404 and not the HTML document is the correct refusal: a build that
    # predates config-inject.sh must not be answered with HTML the client will
    # try to parse as JSON.
    if printf '%s' "$PROBE_PREFIX" | grep -qi '<!doctype html'; then
      bad "/version.json is a 404 but served the HTML document. The client parses it as JSON and the"
      bad "  resulting SyntaxError names the frontend rather than the CDN."
      tier_failures=$((tier_failures + 1))
    else
      note "/version.json is 404, as expected for an image built without deploy/config-inject.sh. The"
      note "  release path injects it, and config-inject.sh fails rather than shipping without it."
    fi
  else
    bad "/version.json returned $PROBE_STATUS (expected 200 or 404)"
    tier_failures=$((tier_failures + 1))
  fi

  # --- HTTPS redirect is a CDN responsibility ---------------------------------
  # Asserted as a configuration property, not over HTTP: the origin serves plain
  # HTTP on 8080 and cannot redirect to https for a host it does not terminate.
  # The redirect is in deploy/cdn/viewer-request.security-headers.js, and the
  # requirement is that it exists and uses `request.uri` (which keeps the query
  # string) rather than `request.url` (which drops it).
  local cdn_fn="$ROOT/deploy/cdn/viewer-request.security-headers.js"
  if [ -f "$cdn_fn" ]; then
    if grep -q 'cloudfront-forwarded-proto' "$cdn_fn" && grep -q 'HTTPS + request.headers.host.value + request.uri' "$cdn_fn"; then
      ok "the CDN viewer-request function redirects http to https using request.uri (query string preserved)"
    else
      bad "the CDN viewer-request function does not redirect, or rebuilds the URL without request.uri."
      bad "  Rebuilding from request.url silently drops every query parameter, so a deep link with a"
      bad "  ?access_token= loses it and the user lands somewhere else with no error."
      tier_failures=$((tier_failures + 1))
    fi
  else
    bad "deploy/cdn/viewer-request.security-headers.js is missing, so no HTTPS redirect is configured"
    tier_failures=$((tier_failures + 1))
  fi

  if [ "$KEEP_CONTAINER" != "1" ]; then
    docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  else
    note "keeping $CONTAINER_NAME (HOSTING_KEEP_CONTAINER=1)"
  fi

  if [ "$tier_failures" -gt 0 ]; then
    TIER_DOCKER="FAIL"
    REASON="$REASON_HEADER"
    DOCKER_NOTE="$tier_failures header assertion(s) failed"
  else
    TIER_DOCKER="PASS"
    DOCKER_NOTE="headers verified against the running image"
  fi
}

# ============================================================================
# main
# ============================================================================
BUILD_LOG="$(mktemp -t hosting-build.XXXXXX 2>/dev/null || echo "${TMPDIR:-/tmp}/hosting-build.$$.log")"
PROBE_HEADERS_FILE="$(mktemp -t hosting-headers.XXXXXX 2>/dev/null || echo "${TMPDIR:-/tmp}/hosting-headers.$$")"
PROBE_BODY_FILE="$(mktemp -t hosting-body.XXXXXX 2>/dev/null || echo "${TMPDIR:-/tmp}/hosting-body.$$")"
cleanup() {
  [ "$KEEP_CONTAINER" = "1" ] || docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  rm -f "$BUILD_LOG" "$PROBE_HEADERS_FILE" "$PROBE_BODY_FILE" 2>/dev/null || true
}
trap cleanup EXIT

printf '== static tier: policy shape ==\n'
run_static_tier
printf '  static tier: %s (%s)\n' "$TIER_STATIC" "$STATIC_NOTE"

printf '\n== docker tier: the actual response headers ==\n'
run_docker_tier
printf '  docker tier: %s (%s)\n' "$TIER_DOCKER" "$DOCKER_NOTE"

printf '\n== hosting gate result ==\n'
printf '  passed: %d, failed: %d\n' "$PASS" "$FAIL"
printf '  static: %s, docker: %s\n' "$TIER_STATIC" "$TIER_DOCKER"

if [ "$FAIL" -gt 0 ]; then
  STATUS="FAIL"
  EXIT_CODE=1
elif [ "$TIER_DOCKER" = "SKIP" ]; then
  # The one place this script does not fail closed, and it is a SKIP with a
  # reason rather than a pass. A green line that means "docker was not installed"
  # is the failure mode `deploy/verify.sh` §2 warns about, so the reason is on the
  # result line and the header matrix is explicitly named as unverified.
  STATUS="SKIP"
  REASON="$REASON_UNVERIFIED"
  EXIT_CODE=0
  printf '\n  NOTE: the header matrix was NOT verified against a running origin.\n'
  printf '  Run this on a host with a working Docker daemon, or in CI, before a release.\n'
else
  STATUS="PASS"
  EXIT_CODE=0
fi

printf 'HOSTING_GATE_RESULT reason=%s status=%s exit=%d static=%s docker=%s\n' \
  "$REASON" "$STATUS" "$EXIT_CODE" "$TIER_STATIC" "$TIER_DOCKER"
exit "$EXIT_CODE"
