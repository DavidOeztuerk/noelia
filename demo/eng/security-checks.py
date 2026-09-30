#!/usr/bin/env python3
"""Collects Noelia's security checks from a running stack and judges them.

The release gate used to be green next to `noelia.headers.browser-baseline:
Fail`. Every other line of that gate proved something about a build; nothing
asked the running system whether the properties it claims actually hold. This
does, and it fails the moment one of them does not.

There is no endpoint to ask, on purpose: an anonymous URL listing a service's
security posture is a reconnaissance surface. The results reach an operator
through the dashboard and through the log, so the log is what this reads.

It also knows what it should find. Judging "whatever lines turned up" passes a
stack in which a whole host never ran its checks, and a green gate next to a
missing host is the same defect as a green gate next to a Fail. The expected
set — services per stage, check IDs per service — comes from one place:
`eng/security-check-expectations.json` (which checks exist and which module
each belongs to, which services a stage has) together with
`eng/composition-baselines.json` (which modules each service role composes).

Runs. The runner writes one log line per check, in ordinal order of the check
ID, and leaves no marker between runs (a restart, or a later run, simply
repeats the sequence). A run therefore ends where an ID does not sort after
its predecessor. Only the latest run per service counts. Limit: a run cut off
mid-way is recognised as incomplete only because an expected ID is missing from
it; a cut-off that happens to lose only IDs nobody expects cannot be seen.

Usage:
    python3 eng/security-checks.py [--profile dev|staging|prod|all]
                                   [-p PROJECT] [--env-file FILE]
                                   [--allow SCOPE/CHECK-ID=REASON ...]

An allowance is a decision someone wrote down. It needs a reason and a scope:
a stage (`staging`) or one service (`gateway-prod`), never the whole stack.
It covers a Fail or a Warning of exactly that check ID and nothing else.
Exit codes: 0 all expected checks present, complete, no unallowed failure;
1 a finding; 2 the gate itself could not run (no logs, bad input).
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# Development renders the template, Staging and Production emit JSON. Reading
# only one of the two would have reported a clean staging stack because it said
# so in a shape this script did not recognise. The service name is what
# precedes the replica suffix: `gateway-staging-1` -> `gateway-staging`.
PREFIX = re.compile(r"^(?P<service>[a-z0-9-]+)-\d+\s+\|\s*(?P<rest>.*)$")

RENDERED = re.compile(
    r"Security check (?P<id>noelia\.[a-z0-9.-]+) for module (?P<module>\S+) "
    r"finished (?P<status>\w+) \((?P<severity>\w+)\)"
)

FAILING = {"Fail"}
NOTEWORTHY = {"Fail", "Warning"}


class GateError(Exception):
    """The gate could not be evaluated at all (bad input), as opposed to a finding."""


# --- reading the log -------------------------------------------------------


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


def parse_runs(text: str) -> dict[str, list[list[dict[str, str]]]]:
    """Per service, every run in log order. A new run starts when an ID does not
    sort after the previous one (see the module docstring for the limit)."""
    runs: dict[str, list[list[dict[str, str]]]] = {}
    for line in text.splitlines():
        prefix = PREFIX.match(line)
        if not prefix:
            continue

        entry = parse(prefix.group("rest"))
        if not entry:
            continue

        service_runs = runs.setdefault(prefix.group("service"), [])
        if not service_runs or entry["id"] <= service_runs[-1][-1]["id"]:
            service_runs.append([])
        service_runs[-1].append(entry)

    return runs


def collect(profile: str, project: str | None, env_file: str | None) -> str:
    # Every service in this stack belongs to a profile, and `docker compose
    # logs` without one selects none of them — it would report an empty stack
    # as cleanly as a passing one.
    command = ["docker", "compose"]
    if project:
        command += ["-p", project]
    if env_file:
        command += ["--env-file", env_file]
    command += ["--profile", profile, "logs", "--no-color"]

    return subprocess.run(command, capture_output=True, text=True, check=True).stdout


# --- what is expected ------------------------------------------------------


def load_expectations(expectations_path: Path, baselines_path: Path) -> dict:
    try:
        expectations = json.loads(expectations_path.read_text())
        baselines = json.loads(baselines_path.read_text())
    except (OSError, json.JSONDecodeError) as error:
        raise GateError(f"cannot read expectations: {error}") from error

    expectations["_roles"] = {role["Name"]: set(role["Modules"]) for role in baselines["Roles"]}
    return expectations


def scenarios_for(expectations: dict, profile: str) -> list[str]:
    scenarios = list(expectations["Scenarios"])
    if profile == "all":
        return scenarios
    if profile not in expectations["Scenarios"]:
        raise GateError(f"unknown profile {profile!r}; known: {', '.join(scenarios)} or all")
    return [profile]


def expected_services(expectations: dict, profile: str) -> dict[str, set[str]]:
    """Instance name (`gateway-staging`) -> the check IDs it must report."""
    expected: dict[str, set[str]] = {}
    for scenario in scenarios_for(expectations, profile):
        settings = expectations["Scenarios"][scenario]
        for service in settings["Services"]:
            if service not in expectations["Services"]:
                raise GateError(f"scenario {scenario!r} names unknown service {service!r}")

            role = f"{expectations['Services'][service]}-{settings['Providers']}"
            if role not in expectations["_roles"]:
                raise GateError(f"no reviewed baseline role {role!r} in composition-baselines.json")

            instance = f"{service}-{scenario}"
            omitted = set(expectations.get("OmittedModules", {}).get(instance, []))
            modules = expectations["_roles"][role] - omitted

            # A Composition check runs whether or not its module is composed;
            # every other one only for a module that is in the composition — so
            # a module left out on purpose takes its checks with it.
            expected[instance] = {
                check_id
                for check_id, module in expectations["Checks"].items()
                if module == "Composition" or module in modules
            }

    return expected


# --- allowances ------------------------------------------------------------


def valid_scopes(expectations: dict) -> set[str]:
    scopes = set(expectations["Scenarios"])
    for scenario, settings in expectations["Scenarios"].items():
        scopes.update(f"{service}-{scenario}" for service in settings["Services"])
    return scopes


def read_accepted(path: Path) -> list[tuple[str, str, str]]:
    """`<scope> <check id> <reason>` per line, as (scope, id, reason)."""
    if not path.exists():
        return []

    accepted = []
    for number, line in enumerate(path.read_text().splitlines(), 1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue

        parts = line.split(None, 2)
        if len(parts) < 3 or not parts[2].strip():
            raise GateError(
                f"{path.name}:{number}: expected '<scope> <check id> <reason>' — "
                "an acceptance without a scope or a reason is not one"
            )
        accepted.append((parts[0], parts[1], parts[2].strip()))

    return accepted


def parse_allow_option(entry: str) -> tuple[str, str, str]:
    target, separator, reason = entry.partition("=")
    scope, slash, identifier = target.partition("/")
    if not separator or not slash or not reason.strip() or not identifier.strip():
        raise GateError(f"--allow {entry!r} needs a scope and a reason: --allow SCOPE/CHECK-ID=why")
    return scope.strip(), identifier.strip(), reason.strip()


def allowance_for(
    allowances: list[tuple[str, str, str]], instance: str, scenario: str, check_id: str
) -> str | None:
    for scope, identifier, reason in allowances:
        if identifier == check_id and scope in (instance, scenario):
            return reason
    return None


# --- judging ---------------------------------------------------------------


def evaluate(
    runs: dict[str, list[list[dict[str, str]]]],
    expected: dict[str, set[str]],
    allowances: list[tuple[str, str, str]],
    fail_on_warning: bool = False,
) -> tuple[list[str], list[str]]:
    """Returns (report lines, problems). No problems means the gate passes."""
    lines: list[str] = []
    problems: list[str] = []

    for instance in sorted(expected):
        scenario = instance.rsplit("-", 1)[1]
        lines.append(f"\n{instance}")

        service_runs = runs.get(instance)
        if not service_runs:
            problems.append(f"{instance}: no security check results in the logs (host missing or never ran its checks)")
            lines.append("  ! no results")
            continue

        latest = {entry["id"]: entry for entry in service_runs[-1]}
        missing = sorted(expected[instance] - latest.keys())
        for check_id in missing:
            problems.append(f"{instance}: expected check {check_id} is missing from the latest run")
            lines.append(f"  ! {check_id:38} MISSING (latest run has {len(latest)} of {len(expected[instance])} expected checks)")

        for check_id in sorted(latest):
            check = latest[check_id]
            mark, note = " ", ""
            reason = allowance_for(allowances, instance, scenario, check_id)

            if check["status"] in FAILING or (fail_on_warning and check["status"] == "Warning"):
                if reason:
                    mark, note = "~", f"   allowed: {reason}"
                else:
                    mark = "!"
                    problems.append(f"{instance}: {check_id} {check['status']} and nobody allowed it")
            elif check["status"] in NOTEWORTHY:
                mark = "~" if reason else "?"
                note = f"   allowed: {reason}" if reason else ""

            if check_id not in expected[instance]:
                note += "   (not expected here)"

            lines.append(f"  {mark} {check_id:38} {check['status']:14} {check['severity']}{note}")

    for instance in sorted(set(runs) - set(expected)):
        lines.append(f"\n{instance}\n  (not part of this gate's expectations; not judged)")

    return lines, problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", default="all",
                        help="Which stage to judge: dev, staging, prod or all (default).")
    parser.add_argument("-p", "--project", help="Compose project name of the stack to read.")
    parser.add_argument("--env-file", help="Compose env file, when the stack was started with one.")
    parser.add_argument("--log-file", help="Read this saved `docker compose logs` output instead of asking Docker.")
    parser.add_argument("--allow", action="append", default=[], metavar="SCOPE/ID=REASON",
                        help="Accept one Fail or Warning of one check in one stage or service, with the reason.")
    parser.add_argument("--fail-on-warning", action="store_true",
                        help="Treat a Warning like a Fail unless it is allowed.")
    parser.add_argument("--accepted", default=str(HERE / "accepted-findings.txt"),
                        help="File of accepted findings. Default: eng/accepted-findings.txt.")
    parser.add_argument("--expectations", default=str(HERE / "security-check-expectations.json"))
    parser.add_argument("--baselines", default=str(HERE / "composition-baselines.json"))
    arguments = parser.parse_args(argv)

    try:
        expectations = load_expectations(Path(arguments.expectations), Path(arguments.baselines))
        expected = expected_services(expectations, arguments.profile)

        allowances = read_accepted(Path(arguments.accepted))
        allowances += [parse_allow_option(entry) for entry in arguments.allow]
        scopes = valid_scopes(expectations)
        for scope, identifier, _ in allowances:
            if scope not in scopes:
                raise GateError(
                    f"allowance for {identifier!r} has scope {scope!r}; a scope is a stage or a "
                    f"service instance, one of: {', '.join(sorted(scopes))}"
                )

        if arguments.log_file:
            text = Path(arguments.log_file).read_text()
        else:
            text = collect(arguments.profile, arguments.project, arguments.env_file)
    except GateError as error:
        print(f"security-checks: {error}", file=sys.stderr)
        return 2
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"security-checks: cannot read the logs: {error}", file=sys.stderr)
        return 2

    lines, problems = evaluate(parse_runs(text), expected, allowances, arguments.fail_on_warning)
    print("\n".join(lines))
    print()

    if problems:
        for problem in problems:
            print(f"FAIL {problem}", file=sys.stderr)
        print(f"{len(problems)} problem(s): the security gate failed.", file=sys.stderr)
        return 1

    print(f"{len(expected)} service(s), every expected check present in the latest run; no unallowed failures.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
