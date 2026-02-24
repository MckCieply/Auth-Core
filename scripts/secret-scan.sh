#!/usr/bin/env bash
# The secret scan of spec 0008 (criterion 17): gitleaks v8.30.1, pinned by digest, in a container. It reads the repository and writes
# nothing but its two reports, in a scratch directory that is removed on exit. The text of a finding is redacted, and never printed.
#
#   1. the committed history of every branch and tag (what is published), and not the local refs that are never pushed (backup refs, the stash);
#   2. the files of this working tree that are not committed yet (modified or new, and not git-ignored): what the next commit adds.
#
# The findings that are known and harmless are listed in .gitleaksignore (file:rule:line, with no commit: it matches in every commit and
# survives a rewrite of the history). Any other finding is printed as its fingerprint (commit:file:rule:line for the history, file:rule:line
# for the files) and fails the script. To list a finding that is a throw-away test value, copy the file:rule:line part of its fingerprint
# into .gitleaksignore; a finding that may be a real secret is never listed: it stops the work.
#
# Run from anywhere in the repository (a worktree too): scripts/secret-scan.sh. Needs docker and tar. Exit code: 0 clean, 1 findings.
set -euo pipefail
# Git Bash: the paths below go to the native git and are converted only when path conversion is on; the docker call turns it off itself.
unset MSYS_NO_PATHCONV MSYS2_ARG_CONV_EXCL

IMAGE="zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# The .git of a worktree is a file that names a path of the host, which the container does not have: mount the repository that owns
# the history (the parent of the common git directory) instead.
repo="$(dirname "$(git -C "$root" rev-parse --path-format=absolute --git-common-dir)")"

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/out" "$scratch/tree" "$scratch/ignore"
# The history scan reads the list as it is (file:rule:line). The files are scanned as /work/..., and gitleaks names a finding by that
# path, so the same list with that prefix is what the second scan reads.
cp "$root/.gitleaksignore" "$scratch/ignore/history"
sed -e '/^#/b' -e '/^[[:space:]]*$/b' -e 's#^#/work/#' "$root/.gitleaksignore" > "$scratch/ignore/tree"
git -C "$root" ls-files -z -m -o --exclude-standard | tar --null --ignore-failed-read -C "$root" -T - -cf - 2> /dev/null \
  | tar -C "$scratch/tree" -xf -

# The docker of a Windows machine wants Windows paths for what it mounts.
host_path() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

status=0
scan() { # scan <label> <report> <ignore list> <docker args and gitleaks command...>: findings make gitleaks exit 2, any other non-zero is an error
  local label="$1" report="$2" ignore="$3" code=0
  shift 3
  MSYS_NO_PATHCONV=1 docker run --rm \
    -v "$(host_path "$scratch/ignore"):/ignore:ro" -v "$(host_path "$scratch/out"):/out" "$@" \
    --redact --no-banner --exit-code 2 --gitleaks-ignore-path "/ignore/$ignore" --report-format json --report-path "/out/$report" \
    > "$scratch/$report.log" 2>&1 || code=$?
  case "$code" in
    0) echo "PASS $label: no leaks found" ;;
    2)
      echo "FOUND $label:"
      grep '"Fingerprint"' "$scratch/out/$report" | sed 's/^[[:space:]]*"Fingerprint": "//; s/",\{0,1\}\r\{0,1\}$//; s#^/work/##; s/^/  /'
      status=1
      ;;
    *)
      cat "$scratch/$report.log" >&2
      echo "FAIL $label: gitleaks or docker failed (exit $code)" >&2
      exit 1
      ;;
  esac
}

scan "the history" history.json history \
  -v "$(host_path "$repo"):/repo:ro" "$IMAGE" git /repo --log-opts="--branches --tags"
scan "the files not committed yet" tree.json tree \
  -v "$(host_path "$scratch/tree"):/work:ro" "$IMAGE" dir /work

exit "$status"
