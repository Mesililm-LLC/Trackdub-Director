#!/usr/bin/env bash
# Approve, enable GitHub auto-merge, and/or squash-merge same-repo Dependabot PRs.
#
# GitHub's enablePullRequestAutoMerge mutation rejects PRs in "unstable" status
# (pending/failing non-required checks) and "clean" status (ready to merge now).
# Treating those GraphQL errors as success is how auto-merge silently no-op'd.
# This helper:
#   - retries "unstable" so required checks can register (BLOCKED -> enable works)
#   - squash-merges immediately on "clean"
#   - squash-merges on CI workflow_run success, which is the reliable path
set -euo pipefail

API="${API:-${GITHUB_API_URL:-https://api.github.com}}"
GQL="${GQL:-${GITHUB_GRAPHQL_URL:-https://api.github.com/graphql}}"
EVENT_NAME="${EVENT_NAME:-}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-8}"
SLEEP_SECS="${SLEEP_SECS:-15}"

classify_enable_resp() {
  local resp="$1"
  if printf '%s' "$resp" | grep -q '"enablePullRequestAutoMerge":{"pullRequest":{"number"'; then
    printf '%s\n' enabled
    return
  fi
  if printf '%s' "$resp" | grep -Eqi 'already been enabled|auto merge is already enabled'; then
    printf '%s\n' already_enabled
    return
  fi
  if printf '%s' "$resp" | grep -Eqi 'already been merged|Pull request is merged'; then
    printf '%s\n' already_merged
    return
  fi
  if printf '%s' "$resp" | grep -q 'in clean status'; then
    printf '%s\n' clean
    return
  fi
  if printf '%s' "$resp" | grep -q 'in unstable status'; then
    printf '%s\n' unstable
    return
  fi
  printf '%s\n' error
}

classify_merge_http() {
  local code="$1"
  local body="$2"
  case "$code" in
    200|201)
      printf '%s\n' merged
      ;;
    405|409)
      if printf '%s' "$body" | grep -Eqi 'already (been )?merged|Pull Request is merged'; then
        printf '%s\n' already_merged
      else
        printf '%s\n' not_mergeable
      fi
      ;;
    *)
      printf '%s\n' error
      ;;
  esac
}

if [ "${1:-}" = "classify-enable" ]; then
  classify_enable_resp "$(cat)"
  exit 0
fi
if [ "${1:-}" = "classify-merge" ]; then
  classify_merge_http "${2:?http code}" "$(cat)"
  exit 0
fi

REPO="${REPO:-${GITHUB_REPOSITORY:?GITHUB_REPOSITORY or REPO is required}}"
GITHUB_TOKEN="${GITHUB_TOKEN:?GITHUB_TOKEN is required}"

if command -v python3 >/dev/null 2>&1; then
  PYTHON=python3
elif command -v python >/dev/null 2>&1; then
  PYTHON=python
else
  echo "::error::python3 or python is required"
  exit 1
fi

auth_headers=(
  -H "Authorization: Bearer ${GITHUB_TOKEN}"
  -H "Accept: application/vnd.github+json"
  -H "X-GitHub-Api-Version: 2022-11-28"
)

api_get() {
  curl -sS "${auth_headers[@]}" "$1"
}

json_field() {
  local json="$1"
  local field="$2"
  "$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1]).get(sys.argv[2]) or "")' "$json" "$field"
}

json_nested() {
  # Missing/null/non-object hops return empty string (exit 0). Callers treat
  # empty author/repo as "skip", not "merge". Do not sys.exit(1) here: with
  # set -e that would fail the job instead of skipping.
  "$PYTHON" -c 'import json,sys
data=json.loads(sys.argv[1])
for key in sys.argv[2:]:
    if not isinstance(data, dict):
        print("")
        raise SystemExit(0)
    data=data.get(key)
print("" if data is None else data)
' "$@"
}

resolve_pr_number() {
  if [ -n "${PR_NUMBER:-}" ]; then
    printf '%s\n' "$PR_NUMBER"
    return
  fi
  if [ -n "${WORKFLOW_PR_NUMBER:-}" ]; then
    printf '%s\n' "$WORKFLOW_PR_NUMBER"
    return
  fi
  local sha="${HEAD_SHA:-}"
  if [ -z "$sha" ]; then
    echo "::error::no PR number or HEAD_SHA available" >&2
    return 1
  fi
  local payload
  payload="$(api_get "${API}/repos/${REPO}/commits/${sha}/pulls")"
  "$PYTHON" -c 'import json,sys
prs=json.loads(sys.argv[1])
if not isinstance(prs, list) or not prs:
    raise SystemExit("no pull requests for commit")
print(prs[0]["number"])
' "$payload"
}

fetch_pr() {
  local number="$1"
  api_get "${API}/repos/${REPO}/pulls/${number}"
}

assert_dependabot_pr() {
  local pr_json="$1"
  local login repo_full
  login="$(json_nested "$pr_json" user login)"
  repo_full="$(json_nested "$pr_json" head repo full_name)"
  if [ "$login" != "dependabot[bot]" ]; then
    echo "Skipping: author is '${login}', not dependabot[bot]."
    return 1
  fi
  if [ "$repo_full" != "$REPO" ]; then
    echo "Skipping: head repo '${repo_full}' is not ${REPO}."
    return 1
  fi
  return 0
}

approve_pr() {
  local number="$1"
  local code
  code="$(
    curl -sS -o /dev/null -w '%{http_code}' \
      -X POST \
      "${auth_headers[@]}" \
      "${API}/repos/${REPO}/pulls/${number}/reviews" \
      -d '{"event":"APPROVE"}' || true
  )"
  echo "approve HTTP status: ${code}"
}

enable_auto_merge() {
  local node_id="$1"
  curl -sS \
    "${auth_headers[@]}" \
    "${GQL}" \
    -d "$(printf '{"query":"mutation { enablePullRequestAutoMerge(input: {pullRequestId: \\"%s\\", mergeMethod: SQUASH}) { pullRequest { number } } }"}' "${node_id}")"
}

squash_merge() {
  local number="$1"
  local body code
  body="$(mktemp)"
  code="$(
    curl -sS -o "$body" -w '%{http_code}' \
      -X PUT \
      "${auth_headers[@]}" \
      "${API}/repos/${REPO}/pulls/${number}/merge" \
      -d '{"merge_method":"squash"}' || true
  )"
  local resp
  resp="$(cat "$body")"
  rm -f "$body"
  echo "merge HTTP status: ${code}"
  echo "merge response: ${resp}"
  local kind
  kind="$(classify_merge_http "$code" "$resp")"
  case "$kind" in
    merged|already_merged)
      echo "Squash-merged (${kind})."
      return 0
      ;;
    *)
      echo "::error::squash merge did not succeed (${kind})"
      return 1
      ;;
  esac
}

try_enable_auto_merge() {
  local node_id="$1"
  local number="$2"
  local attempt resp kind
  attempt=1
  while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
    resp="$(enable_auto_merge "$node_id")"
    echo "auto-merge response (attempt ${attempt}): ${resp}"
    kind="$(classify_enable_resp "$resp")"
    case "$kind" in
      enabled|already_enabled|already_merged)
        echo "Auto-merge ${kind}."
        return 0
        ;;
      clean)
        echo "PR is clean; squash-merging now."
        squash_merge "$number"
        return 0
        ;;
      unstable)
        echo "PR is unstable (pending or failing non-required checks)."
        if [ "$attempt" -lt "$MAX_ATTEMPTS" ]; then
          sleep "$SLEEP_SECS"
        fi
        ;;
      *)
        echo "::error::enablePullRequestAutoMerge did not succeed"
        return 1
        ;;
    esac
    attempt=$((attempt + 1))
  done
  echo "::warning::auto-merge still unstable after ${MAX_ATTEMPTS} attempts; CI workflow_run will squash-merge if tests pass"
  return 0
}

pr_number="$(resolve_pr_number)"
pr_json="$(fetch_pr "$pr_number")"
if ! assert_dependabot_pr "$pr_json"; then
  exit 0
fi
pr_node_id="${PR_NODE_ID:-$(json_field "$pr_json" node_id)}"
if [ -z "$pr_node_id" ]; then
  echo "::error::missing pull request node_id"
  exit 1
fi

approve_pr "$pr_number"

if [ "$EVENT_NAME" = "workflow_run" ]; then
  if [ "${WORKFLOW_CONCLUSION:-}" != "success" ]; then
    echo "Skipping merge: CI conclusion is '${WORKFLOW_CONCLUSION:-}'."
    exit 0
  fi
  squash_merge "$pr_number"
  exit 0
fi

try_enable_auto_merge "$pr_node_id" "$pr_number"
