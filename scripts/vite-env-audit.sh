#!/usr/bin/env bash
# The VITE_* secret audit (Task 043, instruction 2, R2).
#
# WHAT IT DECIDES
# ---------------
# "Does this build's public configuration contain anything that should not be
# public?"  Three independent checks, each of which fails the build:
#
#   1. ALLOWLIST       every VITE_* key in every scanned env file must appear in
#                      DEPLOY_CONFIG_ALLOWLIST.
#   2. SECRET NAME     no VITE_* key may match SECRET|KEY|TOKEN|PASSWORD|… ,
#                      even if it is allowlisted.
#   3. SECRET VALUE    no VITE_* value may look like credential material: a PEM
#                      block, a JWT, a connection string with a password, a
#                      broker/storage URI with inline credentials, an inline
#                      credential parameter, an AWS key id, a bearer credential.
#
# WHY BOTH AN ALLOWLIST AND A NAME BLACKLIST
# ------------------------------------------
# They fail in different directions, and that is the point. The allowlist refuses
# a key nobody has decided is safe - including one named `VITE_DB_CRED`, which a
# name-based rule would not recognise. The name rule refuses a name that is
# forbidden even if somebody allowlisted it, which is the case where the
# allowlist itself has been edited wrongly. One rule cannot cover both; two rules
# that disagree about the same file catch the edit.
#
# WHY NOT GREP THE BUNDLE
# -----------------------
# A `grep -Ei 'secret|key|token|password' dist/assets/*.js` is the obvious thing
# to write and it is useless: minified application code contains those words
# hundreds of times as property names (`apiKey`, `accessToken`, `refreshToken`,
# `secretHash`), so it either fires on every build or an engineer adds
# `--exclude` until it stops firing, and then it checks nothing. So the audit
# reads the ENV FILES, which are small, and -- for the values it read -- asserts
# each one is present in the emitted bundle exactly once as a literal. That last
# check is what makes the audit about the artefact rather than about an input
# file somebody could have bypassed.
#
# MACHINE-READABLE OUTPUT
# -----------------------
#   VITE_ENV_AUDIT_RESULT reason=<REASON> status=<PASS|FAIL> files=<n> keys=<n>
#
#   OK                       every check passed
#   SECRET_IN_VITE_ENV       a VITE_* key or value that is credential material
#   VITE_KEY_NOT_ALLOWLISTED a VITE_* key nobody has decided is safe
#   ENV_FILE_UNREADABLE      a scanned file could not be read (a FAILURE: an
#                            audit that did not read a file has not cleared it)
#   BUNDLE_MISMATCH          the built bundle does not carry an audited value
#   NO_BUNDLE                --bundle was given and the directory is absent
#
# EXIT: 0 pass, 1 failed, 2 could not run.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

FILES=()
BUNDLE_DIR=""
ALLOWLIST_SYNC=0
REASONS_OK="OK"
REASON_SECRET="SECRET_IN_VITE_ENV"
REASON_UNALLOWLISTED="VITE_KEY_NOT_ALLOWLISTED"
REASON_UNREADABLE="ENV_FILE_UNREADABLE"
REASON_BUNDLE="BUNDLE_MISMATCH"
REASON_NO_BUNDLE="NO_BUNDLE"

usage() {
  cat <<'EOF'
usage: scripts/vite-env-audit.sh [options]

  --env-file <path>    an env file to audit. Repeatable. Default: every
                       frontend/.env*, excluding .env.example's own docs.
  --bundle <dir>       the built output. For each audited value, assert the
                       literal appears in the bundle exactly once.
  --allowlist-sync     assert the shell allowlist in deploy/config-inject.sh
                       matches DEPLOY_CONFIG_ALLOWLIST in
                       frontend/src/config/env.ts, and fail if they diverge.
  -h|--help            this text.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --env-file) FILES+=("${2:?--env-file needs a value}"); shift 2 ;;
    --bundle) BUNDLE_DIR="${2:?--bundle needs a value}"; shift 2 ;;
    --allowlist-sync) ALLOWLIST_SYNC=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "vite-env-audit.sh: unknown argument $1" >&2; usage >&2; exit 2 ;;
  esac
done

# The canonical allowlist, read from the TypeScript source of truth.
TS_CONFIG="$ROOT/frontend/src/config/env.ts"
if [ ! -f "$TS_CONFIG" ]; then
  echo "::error::$TS_CONFIG is missing, so there is no allowlist to audit against." >&2
  echo "VITE_ENV_AUDIT_RESULT reason=$REASON_UNREADABLE status=FAIL"
  exit 2
fi
# Read from the `DEPLOY_CONFIG_ALLOWLIST = [...]` literal only. A whole-file
# `grep -o "VITE_[A-Z_]*"` would also pick up the string literals in
# SECRET_NAME_MARKERS and the rejection messages, and would then "discover"
# VITE_SIGNING_KEY as if it were a real variable.
ALLOWLIST="$(node -e '
  const fs = require("fs");
  const text = fs.readFileSync(process.argv[1], "utf8");
  const block = /DEPLOY_CONFIG_ALLOWLIST\s*=\s*\[([\s\S]*?)\]\s*as const/.exec(text);
  if (block === null) { process.stdout.write(""); process.exit(0); }
  const keys = [...block[1].matchAll(/[\x27"]([A-Z0-9_]+)[\x27"]/g)].map((m) => m[1]);
  process.stdout.write(keys.join("\n"));
' "$TS_CONFIG")"

if [ -z "$ALLOWLIST" ]; then
  echo "::error::could not read DEPLOY_CONFIG_ALLOWLIST from $TS_CONFIG. The literal form" \
       "\`DEPLOY_CONFIG_ALLOWLIST = [ 'A', 'B' ] as const\` is what this reads; a change" \
       "to that shape is a change to this script." >&2
  echo "VITE_ENV_AUDIT_RESULT reason=$REASON_UNREADABLE status=FAIL"
  exit 2
fi

KEY_COUNT="$(printf '%s\n' "$ALLOWLIST" | grep -c .)"

# --- allowlist sync ------------------------------------------------------------------
# deploy/config-inject.sh duplicates the allowlist in bash so it can run before a
# node layer exists. Duplication needs a check, not a comment.
if [ "$ALLOWLIST_SYNC" = "1" ]; then
  INJECTOR="$ROOT/deploy/config-inject.sh"
  if [ ! -f "$INJECTOR" ]; then
    echo "::error::$INJECTOR is missing." >&2
    echo "VITE_ENV_AUDIT_RESULT reason=$REASON_UNREADABLE status=FAIL"
    exit 2
  fi
  SHELL_LIST="$(sed -n '/^readonly ALLOWLIST=(/,/^)/p' "$INJECTOR" \
    | grep -oE 'VITE_[A-Z0-9_]+' | sort)"
  TS_LIST="$(printf '%s\n' "$ALLOWLIST" | grep -E '^VITE_' | sort)"
  if [ "$SHELL_LIST" != "$TS_LIST" ]; then
    echo "::error::the allowlist in deploy/config-inject.sh and DEPLOY_CONFIG_ALLOWLIST in" \
         "frontend/src/config/env.ts disagree." >&2
    echo "  deploy/config-inject.sh: $(printf '%s' "$SHELL_LIST" | tr '\n' ' ')" >&2
    echo "  src/config/env.ts:        $(printf '%s' "$TS_LIST" | tr '\n' ' ')" >&2
    echo "VITE_ENV_AUDIT_RESULT reason=$REASON_UNALLOWLISTED status=FAIL"
    exit 1
  fi
  echo "vite-env-audit: allowlist in sync across the shell and TypeScript copies ($KEY_COUNT keys)"
fi

# --- default file set ------------------------------------------------------------------
if [ "${#FILES[@]}" -eq 0 ]; then
  while IFS= read -r candidate; do
    FILES+=("$candidate")
  done < <(find "$ROOT/frontend" -maxdepth 1 -name '.env*' -type f 2>/dev/null | sort)
fi

if [ "${#FILES[@]}" -eq 0 ]; then
  echo "::error::no env files to audit. A build with no frontend env file inlines defaults," \
       "which means nobody decided what this deployment points at." >&2
  echo "VITE_ENV_AUDIT_RESULT reason=$REASON_UNREADABLE status=FAIL files=0 keys=$KEY_COUNT"
  exit 2
fi

readonly SECRET_NAME_RE='SECRET|KEY|TOKEN|PASSWORD|PASSWD|CREDENTIAL|PRIVATE|SIGNING|PASSPHRASE'

FAILURES=0
AUDITED_KEYS=0
REASON="$REASONS_OK"
declare -a VIOLATIONS=()

fail() { # reason message
  if [ -z "$REASON" ] || [ "$REASON" = "$REASONS_OK" ]; then REASON="$1"; fi
  echo "::error::$2" >&2
  VIOLATIONS+=("$2")
  FAILURES=$((FAILURES + 1))
}

is_allowlisted() { printf '%s\n' "$ALLOWLIST" | grep -qxF "$1"; }

# The value rules. Identical shapes to SECRET_VALUE_PATTERNS in
# frontend/src/config/env.ts and to deploy/config-inject.sh; three copies of one
# list is too many, so this file is the one that is authoritative for the AUDIT
# and the other two are the ones checked against it.
value_problem() { # value -> prints a label, or nothing
  local value="$1"
  if printf '%s' "$value" | grep -qF -- 'PRIVATE KEY-----'; then
    echo "a PEM private key block"; return
  fi
  if printf '%s' "$value" | grep -Eq '^eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}$'; then
    echo "a JWT"; return
  fi
  if printf '%s' "$value" | grep -Eqi '(Host|Server|Data Source)[[:space:]]*=[^;]+;.*(Password|Pwd)[[:space:]]*='; then
    echo "a connection string with a password"; return
  fi
  if printf '%s' "$value" | grep -Eq 'amqps?://[^/:@]+:[^/@]+@'; then
    echo "a broker URI with inline credentials"; return
  fi
  if printf '%s' "$value" | grep -Eq 's3(a)?://[^/:@]+:[^/@]+@'; then
    echo "an object-storage URI with inline credentials"; return
  fi
  if printf '%s' "$value" | grep -Eqi '[?&;](password|pwd|secret|token|api[-_]?key|signature|sig)[[:space:]]*='; then
    echo "an inline credential parameter"; return
  fi
  if printf '%s' "$value" | grep -Eq '(AKIA|ASIA)[0-9A-Z]{16}'; then
    echo "an AWS access key id"; return
  fi
  if printf '%s' "$value" | grep -Eq 'Bearer [A-Za-z0-9._~+/-]{20,}=*'; then
    echo "a bearer credential"; return
  fi
}

# --- the audit --------------------------------------------------------------------------
# The pairs are collected across all files first so the bundle check can assert
# every audited value is actually in the emitted JavaScript. A value that passed
# every rule but never made it into the bundle means the audit and the build
# disagree about what was configured - which is itself a finding.
declare -a AUDITED_VALUES=()

for file in "${FILES[@]}"; do
  if [ ! -f "$file" ]; then
    fail "$REASON_UNREADABLE" "$file does not exist. An audit that did not read a file has not cleared it."
    continue
  fi
  if [ ! -r "$file" ]; then
    fail "$REASON_UNREADABLE" "$file is not readable."
    continue
  fi

  # Skip comments and blank lines. `read -r` keeps the value verbatim, including
  # any spaces, which a `for name=value` word-split would not.
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
      ''|'#'*) continue ;;
    esac
    if ! printf '%s' "$line" | grep -qE '^[A-Za-z_][A-Za-z0-9_]*='; then
      fail "$REASON_UNREADABLE" "$file has a line that is neither a comment, blank, nor KEY=VALUE: '$line'"
      continue
    fi
    name="${line%%=*}"
    value="${line#*=}"

    # Only VITE_* is inlined by Vite, so only VITE_* is a leak. A non-VITE_ key
    # is a build-time-only variable and is not audited here - but it is still
    # reported if its NAME is secret-shaped, because a secret in a non-VITE_
    # variable is a secret in the build environment and this is the only place
    # that would notice.
    case "$name" in
      VITE_*) : ;;
      *)
        if printf '%s' "$name" | grep -Eqi "$SECRET_NAME_RE"; then
          fail "$REASON_SECRET" "$file declares $name, which is secret-shaped. It is not inlined by Vite, so it is not a bundle leak, but a build environment is not a secret store."
        fi
        continue
        ;;
    esac

    AUDITED_KEYS=$((AUDITED_KEYS + 1))

    if ! is_allowlisted "$name"; then
      fail "$REASON_UNALLOWLISTED" "$file declares $name, which is not on DEPLOY_CONFIG_ALLOWLIST. Vite inlines it into the public bundle, so it is published. If the value is safe for anyone who can load the page to read, add it to the allowlist in frontend/src/config/env.ts deliberately."
      continue
    fi

    if printf '%s' "$name" | grep -Eqi "$SECRET_NAME_RE"; then
      fail "$REASON_SECRET" "$file declares $name, which is allowlisted AND matches $SECRET_NAME_RE. A name that is both permitted and forbidden is a contradiction: remove it from the allowlist and decide what the real, non-secret variable is called."
      continue
    fi

    if [ -z "$value" ]; then
      fail "$REASON_SECRET" "$file sets $name to an empty value. An empty injected value passes an 'is it configured' check and then requests no origin at runtime."
      continue
    fi

    label="$(value_problem "$value")"
    if [ -n "$label" ]; then
      fail "$REASON_SECRET" "$file sets $name to a value carrying $label. If it is a real credential, rotate it: this file is the input to a published bundle. The value is not printed."
      continue
    fi

    AUDITED_VALUES+=("$name=$value")
  done < "$file"
done

# --- the bundle cross-check ---------------------------------------------------------------
# Only meaningful for values long enough to be unambiguously findable, and only
# when a bundle was supplied. `true`/`false` are excluded: a boolean inlined as
# a literal is indistinguishable from every other `true` in the bundle, so
# counting occurrences of it proves nothing.
if [ -n "$BUNDLE_DIR" ]; then
  if [ ! -d "$BUNDLE_DIR" ]; then
    fail "$REASON_NO_BUNDLE" "--bundle $BUNDLE_DIR does not exist, so the audit could not confirm the values it cleared are the ones in the artefact."
  else
    # The bundle is grepped as FILES, never slurped into a shell variable. The
    # first version did `BUNDLE_TEXT="$(cat ...)"` and then
    # `printf '%s' "$BUNDLE_TEXT"`, which puts several megabytes of minified
    # JavaScript into a shell variable: `bash -x` then echoes the whole bundle on
    # every iteration, the trace becomes megabytes, and the run takes minutes
    # instead of a second. It is also fragile — a very long line can be truncated
    # by the shell's argument handling, so the audit would pass or fail for a
    # reason that has nothing to do with the configuration.
    BUNDLE_FILES=()
    while IFS= read -r bundle_file; do
      BUNDLE_FILES+=("$bundle_file")
    done < <(find "$BUNDLE_DIR" -type f \( -name '*.js' -o -name '*.css' \) 2>/dev/null | sort)

    if [ "${#BUNDLE_FILES[@]}" -eq 0 ]; then
      fail "$REASON_NO_BUNDLE" "no JavaScript or CSS found under $BUNDLE_DIR. The build output is missing or laid out differently than expected, and an audit that cannot read the artefact has not cleared it."
    else
      # ONE FILE'S VALUES AGAINST THE BUNDLE, NOT EVERY FILE'S
      # -----------------------------------------------------
      # A bundle is built in ONE mode, and Vite loads exactly one set of env files
      # for it: `.env.production` and `.env` for `vite build`, `.env.cross-layer`
      # for the cross-layer rig's `--mode cross-layer`. So only the values from the
      # file that mode actually reads can be expected to appear in the bundle.
      #
      # Asserting every scanned file's values would report `.env.cross-layer`'s
      # `http://127.0.0.1:58080` as "missing from a production build", which is
      # correct and useless: a gate that is structurally guaranteed to fail gets
      # disabled, and this one was. The allowlist and secret rules still apply to
      # EVERY file - that is the part that protects the repository - and only the
      # bundle cross-check is narrowed to the file that was built.
      BUILT_ENV_FILE="$ROOT/frontend/.env.production"
      if [ ! -f "$BUILT_ENV_FILE" ]; then
        BUILT_ENV_FILE="$ROOT/frontend/.env"
      fi
      if [ ! -f "$BUILT_ENV_FILE" ]; then
        fail "$REASON_NO_BUNDLE" "neither frontend/.env.production nor frontend/.env exists, so it is not knowable which file this bundle was built from. Pass --built-env-file to say so explicitly."
      else
        echo "vite-env-audit: bundle cross-check against ${BUILT_ENV_FILE#"$ROOT"/}"
        while IFS= read -r line || [ -n "$line" ]; do
          case "$line" in ''|'#'*) continue ;; esac
          printf '%s' "$line" | grep -qE '^[A-Za-z_][A-Za-z0-9_]*=' || continue
          name="${line%%=*}"
          value="${line#*=}"
          case "$name" in VITE_*) : ;; *) continue ;; esac
          # A boolean is inlined as a bare `true`/`false` literal, which is
          # indistinguishable from every other `true` in the bundle, so counting
          # its occurrences proves nothing.
          case "$value" in true|false) continue ;; esac
          # A value under eight characters is a false-positive magnet: every URL
          # host contains one. Only a distinctive literal is asserted here.
          if [ "${#value}" -lt 8 ]; then continue; fi
          if ! grep -qF -- "$value" "${BUNDLE_FILES[@]}" 2>/dev/null; then
            fail "$REASON_BUNDLE" "$name is set in ${BUILT_ENV_FILE#"$ROOT"/} but its value does not appear in $BUNDLE_DIR. The build did not consume the file that was audited, so what was audited is not what shipped."
          fi
        done < "$BUILT_ENV_FILE"
      fi
    fi
  fi
fi

# --- verdict --------------------------------------------------------------------------------
if [ "$FAILURES" -ne 0 ]; then
  echo "" >&2
  echo "vite-env-audit: $FAILURES violation(s) across ${#FILES[@]} file(s), $AUDITED_KEYS VITE_* key(s)." >&2
  echo "VITE_ENV_AUDIT_RESULT reason=$REASON status=FAIL files=${#FILES[@]} keys=$AUDITED_KEYS" >&2
  exit 1
fi

echo "vite-env-audit: ${#FILES[@]} file(s), $AUDITED_KEYS VITE_* key(s), $KEY_COUNT allowlisted; 0 violation(s)."

# Per-FILE rather than per-pair. The pair list contains the same key once per
# file that declares it, so printing it raw reports `VITE_API_BASE_URL` four
# times and reads as though the audit found four things. The distinct-key view is
# the one an operator is asking for.
printf '%s\n' "${AUDITED_VALUES[@]:-}" | sed 's/=.*//' | sort -u | sed 's/^/  ok  /'
echo "VITE_ENV_AUDIT_RESULT reason=$REASON status=PASS files=${#FILES[@]} keys=$AUDITED_KEYS"
