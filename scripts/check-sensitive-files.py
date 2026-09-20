"""Fail on tracked environment files and recognizable credentials; never print values.

This is a narrow source-tree check, not a full history/entropy secret scanner.
Run from the repository root with Python 3; it uses only the standard library.
"""
from pathlib import Path
import re
import subprocess
import sys

paths = subprocess.check_output(["git", "ls-files", "-z"]).decode().split("\0")
patterns = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----"),
    "GitHub token": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b"),
    "AWS access key": re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "MongoDB URI with credentials": re.compile(r"mongodb(?:\+srv)?://[^\s<>{}$:/]+:[^\s<>{}$@]+@"),
}
failures = []
for name in filter(None, paths):
    path = Path(name)
    if not path.is_file():
        continue
    if (path.name == ".env" or path.name.startswith(".env.")) and not path.name.endswith((".example", ".sample", ".template")):
        failures.append((name, "environment file"))
    content = path.read_bytes()
    if b"\0" in content:
        continue
    value = content.decode("utf-8", errors="replace")
    for label, pattern in patterns.items():
        if pattern.search(value):
            failures.append((name, label))
for name, label in failures:
    print(f"BLOCKED {name}: {label}")
if failures:
    sys.exit(1)
print("Tracked files: no environment files or recognized credential patterns found.")
