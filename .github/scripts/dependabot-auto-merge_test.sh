#!/usr/bin/env bash
# Isolated tests for dependabot-auto-merge.sh. Never talks to GitHub.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="${ROOT}/.github/scripts/dependabot-auto-merge.sh"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/dependabot-auto-merge-test-XXXXXX")"
trap 'rm -rf "${WORK}"' EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

assert_eq() {
  local got="$1"
  local want="$2"
  local label="$3"
  if [ "$got" != "$want" ]; then
    fail "${label}: got '${got}' want '${want}'"
  fi
}

got="$(printf '%s' '{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"number":297}}}}' | bash "$SCRIPT" classify-enable)"
assert_eq "$got" "enabled" "populated mutation result"

got="$(printf '%s' '{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request Pull request is in unstable status"}]}' | bash "$SCRIPT" classify-enable)"
assert_eq "$got" "unstable" "unstable is retryable, not success"

got="$(printf '%s' '{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request Pull request is in clean status"}]}' | bash "$SCRIPT" classify-enable)"
assert_eq "$got" "clean" "clean means merge now, not skip"

got="$(printf '%s' '{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Auto merge is already enabled"}]}' | bash "$SCRIPT" classify-enable)"
assert_eq "$got" "already_enabled" "already enabled"

got="$(printf '%s' '{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request has already been merged"}]}' | bash "$SCRIPT" classify-enable)"
assert_eq "$got" "already_merged" "already merged"

got="$(printf '%s' '{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Resource not accessible by integration"}]}' | bash "$SCRIPT" classify-enable)"
assert_eq "$got" "error" "genuine GraphQL failure"

got="$(printf '%s' '{"merged":true}' | bash "$SCRIPT" classify-merge 200)"
assert_eq "$got" "merged" "HTTP 200 merge"

got="$(printf '%s' '{"message":"Pull Request was already merged"}' | bash "$SCRIPT" classify-merge 405)"
assert_eq "$got" "already_merged" "HTTP 405 already merged"

got="$(printf '%s' '{"message":"Pull Request is not mergeable"}' | bash "$SCRIPT" classify-merge 405)"
assert_eq "$got" "not_mergeable" "HTTP 405 not mergeable"

BIN="${WORK}/bin"
mkdir -p "${BIN}"
export PATH="${BIN}:${PATH}"
export GITHUB_TOKEN="fake-token"
export GITHUB_REPOSITORY="snaptapp/more-or-less"
export REPO="snaptapp/more-or-less"
export API="https://example.test"
export GQL="https://example.test/graphql"
export CURL_LOG="${WORK}/curl.log"
export CURL_STATE="${WORK}/curl.state"
printf '0\n' >"${CURL_STATE}"

cat >"${BIN}/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "${CURL_LOG}"
method="GET"
out=""
write_out=""
args=("$@")
i=0
while [ "$i" -lt "${#args[@]}" ]; do
  case "${args[$i]}" in
    -X) i=$((i + 1)); method="${args[$i]}" ;;
    -o) i=$((i + 1)); out="${args[$i]}" ;;
    -w) i=$((i + 1)); write_out="${args[$i]}" ;;
    -d) i=$((i + 1)) ;;
    -H|-sS) ;;
    https://*) url="${args[$i]}" ;;
  esac
  i=$((i + 1))
done

body=""
code="200"
if [[ "${url:-}" == *"/pulls/"*"/reviews" ]]; then
  body='{"id":1}'
  code="200"
elif [[ "${url:-}" == *"/graphql" ]]; then
  n="$(cat "${CURL_STATE}")"
  if [ "${GRAPHQL_MODE:-unstable-then-enabled}" = "always-unstable" ]; then
    body='{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request Pull request is in unstable status"}]}'
  elif [ "${GRAPHQL_MODE:-}" = "clean" ]; then
    body='{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request Pull request is in clean status"}]}'
  elif [ "$n" -eq 0 ]; then
    body='{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request Pull request is in unstable status"}]}'
    echo 1 >"${CURL_STATE}"
  else
    body='{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"number":297}}}}'
  fi
elif [[ "${url:-}" == *"/merge" ]]; then
  body='{"merged":true,"sha":"abc"}'
  code="200"
elif [[ "${url:-}" == *"/pulls/"* ]]; then
  if [ -n "${PR_JSON:-}" ]; then
    body="${PR_JSON}"
  else
    login="${PR_LOGIN:-dependabot[bot]}"
    full_name="${PR_HEAD_REPO:-snaptapp/more-or-less}"
    body="{\"number\":297,\"node_id\":\"PR_node\",\"user\":{\"login\":\"${login}\"},\"head\":{\"repo\":{\"full_name\":\"${full_name}\"}}}"
  fi
else
  echo "curl mock: unexpected url ${url:-}" >&2
  exit 1
fi

if [ -n "$out" ]; then
  printf '%s' "$body" >"$out"
else
  printf '%s' "$body"
fi
if [ -n "$write_out" ]; then
  printf '%s' "$code"
fi
EOF
chmod +x "${BIN}/curl"

export EVENT_NAME="pull_request"
export PR_NUMBER="297"
export PR_NODE_ID="PR_node"
export MAX_ATTEMPTS="3"
export SLEEP_SECS="0"

OUT="${WORK}/enabled.txt"
if ! bash "$SCRIPT" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  fail "unstable-then-enabled should succeed"
fi
grep -q "Auto-merge enabled." "$OUT" || {
  cat "$OUT" >&2
  fail "expected auto-merge enabled after retry"
}
grep -q "attempt 1" "$OUT" || fail "expected retry log"

printf '0\n' >"${CURL_STATE}"
: >"${CURL_LOG}"
export GRAPHQL_MODE="clean"
OUT="${WORK}/clean.txt"
if ! bash "$SCRIPT" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  fail "clean status should squash-merge"
fi
grep -q "Squash-merged (merged)." "$OUT" || {
  cat "$OUT" >&2
  fail "expected squash merge on clean"
}
grep -q "/merge" "${CURL_LOG}" || fail "clean path must PUT /merge"

printf '0\n' >"${CURL_STATE}"
: >"${CURL_LOG}"
export GRAPHQL_MODE="always-unstable"
OUT="${WORK}/unstable.txt"
if ! bash "$SCRIPT" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  fail "exhausted unstable retries should not fail the job"
fi
grep -q "CI workflow_run will squash-merge" "$OUT" || {
  cat "$OUT" >&2
  fail "expected warning that CI workflow_run will merge"
}
if grep -q "Auto-merge not needed" "$OUT"; then
  fail "must not treat unstable as already mergeable"
fi

unset GRAPHQL_MODE
printf '0\n' >"${CURL_STATE}"
: >"${CURL_LOG}"
export EVENT_NAME="workflow_run"
export WORKFLOW_CONCLUSION="success"
OUT="${WORK}/workflow-run.txt"
if ! bash "$SCRIPT" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  fail "workflow_run success should squash-merge"
fi
grep -q "Squash-merged (merged)." "$OUT" || {
  cat "$OUT" >&2
  fail "expected squash merge on workflow_run"
}
grep -q "/merge" "${CURL_LOG}" || fail "workflow_run path must PUT /merge"

: >"${CURL_LOG}"
export PR_LOGIN="octocat"
OUT="${WORK}/skip.txt"
if ! bash "$SCRIPT" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  fail "non-dependabot PR should skip, not fail"
fi
grep -q "Skipping: author is 'octocat'" "$OUT" || {
  cat "$OUT" >&2
  fail "expected skip for human PR"
}
if grep -q "/merge" "${CURL_LOG}"; then
  fail "must not merge a non-dependabot PR"
fi

unset PR_LOGIN
: >"${CURL_LOG}"
export PR_JSON='{"number":297,"node_id":"PR_node","user":null,"head":{"sha":"abc"}}'
OUT="${WORK}/malformed.txt"
if ! bash "$SCRIPT" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  fail "missing nested JSON fields should skip, not fail the job"
fi
grep -q "Skipping: author is ''" "$OUT" || {
  cat "$OUT" >&2
  fail "expected skip when user.login is missing"
}
if grep -q "/merge" "${CURL_LOG}"; then
  fail "must not merge when PR JSON is missing author fields"
fi

echo "dependabot-auto-merge helper tests passed."
