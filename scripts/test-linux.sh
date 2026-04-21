#!/usr/bin/env bash
# The whole test suite on Linux, in the official .NET 10 SDK container (the build stage of the Dockerfile: the image is read from there, with
# its digest). The service runs on Linux in production, and the suite is usually run on a Windows machine: this is the run that shows a
# difference of file names, line endings, paths or time zones before a merge to main (docs/workflow.md, "Before merging to main").
#
# The tests start their own PostgreSQL (and Mailpit) with Testcontainers, so the container gets the Docker socket: they are sibling
# containers of the host's Docker, not containers inside this one. They are reached through the host (TESTCONTAINERS_HOST_OVERRIDE, below).
#
# What it does, from anywhere in the repository (a worktree too):
#   1. copies the files git knows (tracked and not ignored) into a scratch directory: the Linux build never touches the bin/ and obj/ of the
#      working tree, and the working tree is never written;
#   2. runs `dotnet test --solution Auth-Core.slnx` there, in the container; extra arguments go to `dotnet test`
#      (for example: scripts/test-linux.sh --filter-class "*HealthProbeTests"; the whole suite takes a while and wants most of the CPU);
#   3. removes the scratch directory on exit. The NuGet packages are kept in the docker volume auth-core-nuget, so that the next run does
#      not download them again (docker volume rm auth-core-nuget forgets them).
#
# Needs docker (Docker Desktop or Docker Engine), tar and git. Env: TESTCONTAINERS_HOST_OVERRIDE, the address the tests reach the databases
# on; by default the script resolves host.docker.internal (the host gateway, on Docker Engine for Linux too) to its IPv4 address ONCE, and
# hands that number over. The name itself is not handed over: Docker Desktop answers it with an IPv6 address as well, which the container
# cannot reach ("Network is unreachable"), and a test that tried that one first failed after the 15 s connect timeout. Exit code: that of
# dotnet test (0 all passed).
set -euo pipefail
# Git Bash: the paths below go to the native git and are converted only when path conversion is on; the docker call turns it off itself.
unset MSYS_NO_PATHCONV MSYS2_ARG_CONV_EXCL

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# The SDK image is the build stage of the Dockerfile, read from there, so that the two cannot drift apart.
IMAGE="$(sed -n 's/^FROM \(mcr\.microsoft\.com\/dotnet\/sdk:[^ ]*@sha256:[0-9a-f]*\) AS build.*/\1/p' "$root/src/Auth.Server/Dockerfile" | tr -d '\r')"
[[ -n "$IMAGE" ]] || { echo "FAIL no SDK image with a digest found in src/Auth.Server/Dockerfile" >&2; exit 1; }

# The docker of a Windows machine wants Windows paths for what it mounts.
host_path() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

# The IPv4 address of the host as the container sees it (the same mapping the test container gets below).
HOST_OVERRIDE="${TESTCONTAINERS_HOST_OVERRIDE:-}"
if [[ -z "$HOST_OVERRIDE" ]]; then
  HOST_OVERRIDE="$(docker run --rm --add-host host.docker.internal:host-gateway --entrypoint getent "$IMAGE" ahostsv4 host.docker.internal | awk 'NR==1 {print $1}' | tr -d '\r')"
  [[ "$HOST_OVERRIDE" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "FAIL could not resolve host.docker.internal to an IPv4 address (set TESTCONTAINERS_HOST_OVERRIDE yourself)" >&2; exit 1; }
fi

scratch="$(mktemp -d)"
cleanup() {
  # The build runs as root in the container. On Linux the files it wrote into the mount belong to root, and this user cannot remove them:
  # the container does it first.
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$scratch"):/scratch" --entrypoint rm "$IMAGE" -rf /scratch/src > /dev/null 2>&1 || true
  rm -rf "$scratch"
}
trap cleanup EXIT
mkdir -p "$scratch/src"
if ! git -C "$root" ls-files -z -c -o --exclude-standard | tar --null --ignore-failed-read -C "$root" -T - -cf - 2> "$scratch/tar.err" \
  | tar -C "$scratch/src" -xf - 2>> "$scratch/tar.err"; then
  cat "$scratch/tar.err" >&2
  echo "FAIL could not copy the files of the working tree" >&2
  exit 1
fi

echo "Linux test run: $IMAGE (databases reached on $HOST_OVERRIDE)"
code=0
# The socket is the one INSIDE the docker host (Docker Desktop's VM too), hence the leading slash and no path conversion.
# --add-host: Docker Desktop knows host.docker.internal already; Docker Engine for Linux needs the gateway named.
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$(host_path "$scratch/src"):/src" \
  -v auth-core-nuget:/root/.nuget/packages \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --add-host host.docker.internal:host-gateway \
  -e TESTCONTAINERS_HOST_OVERRIDE="$HOST_OVERRIDE" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -w /src \
  "$IMAGE" \
  dotnet test --solution Auth-Core.slnx "$@" || code=$?

if [[ "$code" -eq 0 ]]; then
  echo "PASS the test suite on Linux"
else
  echo "FAIL the test suite on Linux (exit $code)" >&2
fi
exit "$code"
