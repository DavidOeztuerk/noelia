#!/usr/bin/env python3
"""Collects Noelia's security checks from a running stack and judges them.

The release gate used to be green next to `noelia.headers.browser-baseline:
Fail`. Every other line of that gate proved something about a build; nothing
asked the running system whether the properties it claims actually hold. This
does, and it fails the moment one of them does not.

There is no endpoint to ask, on purpose: an anonymous URL listing a service's
security posture is a reconnaissance surface. The results reach an operator
through the dashboard and through the log, so the log is what this reads.

Usage:
    python3 eng/security-checks.py [--profile dev] [--allow id=reason ...]

An allowance is a decision someone wrote down, and it needs a reason. A `Fail`
nobody allowed exits non-zero.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import defaultdict

# Development renders the template, Staging and Production emit JSON. Reading
# only one of the two would have reported a clean staging stack because it said
# so in a shape this script did not recognise.
PREFIX = re.compile(r"^(?P<service>[a-z0-9-]+)-\d+\s+\|\s*(?P<rest>.*)$")

RENDERED = re.compile(
    r"Security check (?P<id>noelia\.[a-z0-9.-]+) for module (?P<module>\S+) "
    r"finished (?P<status>\w+) \((?P<severity>\w+)\)"
)

FAILING = {"Fail"}
NOTEWORTHY = {"Fail", "Warning"}


def collect(profile: str) -> dict[str, list[dict[str, str]]]:
    # Every service in this stack belongs to a profile, and `docker compose
    # logs` without one selects none of them — it would report an empty stack
    # as cleanly as a passing one.
    command = ["docker", "compose", "--profile", profile, "logs", "--no-color"]

    result = subprocess.run(command, capture_output=True, text=True, check=True)

    found: dict[str, list[dict[str, str]]] = defaultdict(list)
    for line in result.stdout.splitlines():
        prefix = PREFIX.match(line)
        if not prefix:
            continue

        service, rest = prefix.group("service"), prefix.group("rest")
        entry = parse(rest)
        if entry and entry not in found[service]:
            found[service].append(entry)

    return found


def parse(rest: str) -> dict[str, str] | None:
    """One check result, from either log shape."""
    if rest.startswith("{"):
        try:
            properties = json.loads(rest).get("Properties", {})
        except json.JSONDecodeError:
            return None

        if "SecurityCheckId" not in properties:
            return None

        return {
            "id": properties["SecurityCheckId"],
            "module": str(properties.get("Module", "")),
            "status": str(properties.get("Status", "")),
            "severity": str(properties.get("Severity", "")),
        }

    match = RENDERED.search(rest)
    return match.groupdict() if match else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", default="all",
                        help="Which stage to judge. Default: all.")
    parser.add_argument(
        "--allow",
        action="append",
        default=[],
        metavar="ID=REASON",
        help="Accept one failing check, with the reason it is acceptable here.",
    )
    arguments = parser.parse_args()

    allowed: dict[str, str] = {}
    for entry in arguments.allow:
        identifier, separator, reason = entry.partition("=")
        if not separator or not reason.strip():
            print(f"--allow {entry!r} needs a reason: --allow {identifier}=why", file=sys.stderr)
            return 2
        allowed[identifier.strip()] = reason.strip()

    found = collect(arguments.profile)
    if not found:
        print("No security checks in the logs. Is the stack running?", file=sys.stderr)
        return 2

    unallowed = 0
    for service in sorted(found):
        print(f"\n{service}")
        for check in sorted(found[service], key=lambda c: c["id"]):
            mark = " "
            if check["status"] in FAILING:
                if check["id"] in allowed:
                    mark = "~"
                else:
                    mark = "!"
                    unallowed += 1
            elif check["status"] in NOTEWORTHY:
                mark = "?"

            note = ""
            if mark == "~":
                note = f"   allowed: {allowed[check['id']]}"
            print(f"  {mark} {check['id']:38} {check['status']:14} {check['severity']}{note}")

    print()
    if unallowed:
        print(f"{unallowed} security check(s) failed and nobody allowed them.", file=sys.stderr)
        return 1

    print("No unallowed failures.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
