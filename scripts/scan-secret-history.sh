#!/usr/bin/env bash
set -euo pipefail
qa_tools="$(mktemp -d)"
trap 'rm -rf -- "$qa_tools"' EXIT
curl --fail --location --silent --show-error https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz -o "$qa_tools/gitleaks.tar.gz"
printf '%s  %s\n' '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb' "$qa_tools/gitleaks.tar.gz" | sha256sum --check
tar -xzf "$qa_tools/gitleaks.tar.gz" -C "$qa_tools" gitleaks
"$qa_tools/gitleaks" git --redact=100 --log-opts=--all .
