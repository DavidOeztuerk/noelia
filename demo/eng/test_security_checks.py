"""Unit tests for eng/security-checks.py. Stdlib only:

    python3 -m unittest discover -s eng -p 'test_*.py'

The fixtures are log snippets in both shapes the stack emits (rendered text in
Development, JSON in Staging and Production). Each test names the way the old
gate would have been green next to a real problem.
"""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("security_checks", HERE / "security-checks.py")
gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gate)

CHECKS = {  # a small, real-shaped catalogue; ordinal order is the runner's order
    "noelia.a.composition": "Composition",
    "noelia.b.headers": "SecurityHeaders",
    "noelia.c.dashboard": "Dashboard",
    "noelia.d.jwt": "Jwt",
}

BASELINES = {
    "Roles": [
        {"Name": "gateway-redis", "Modules": ["SecurityHeaders", "Dashboard"]},
        {"Name": "verifier-redis", "Modules": ["SecurityHeaders", "Dashboard", "Jwt"]},
    ]
}

EXPECTATIONS = {
    "Checks": CHECKS,
    "Services": {"gateway": "gateway", "todo": "verifier"},
    "Scenarios": {"prod": {"Providers": "redis", "Services": ["gateway", "todo"]}},
    "OmittedModules": {},
}


def json_line(service: str, check_id: str, status: str = "Pass", replica: int = 1) -> str:
    module = CHECKS[check_id]
    properties = {"SecurityCheckId": check_id, "Module": module, "Status": status, "Severity": "High"}
    return f"{service}-{replica}  | " + json.dumps({"Properties": properties})


def rendered_line(service: str, check_id: str, status: str = "Pass") -> str:
    return (f"{service}-1  | [12:00:00 INF] Security check {check_id} for module "
            f"{CHECKS[check_id]} finished {status} (High)")


def run(service: str, ids: list[str], status: dict[str, str] | None = None) -> list[str]:
    return [json_line(service, i, (status or {}).get(i, "Pass")) for i in ids]


ALL = sorted(CHECKS)
GATEWAY = ["noelia.a.composition", "noelia.b.headers", "noelia.c.dashboard"]


def judge(lines, expectations=None, allowances=None, profile="prod", **kwargs):
    expectations = dict(expectations or EXPECTATIONS)
    expectations["_roles"] = {r["Name"]: set(r["Modules"]) for r in BASELINES["Roles"]}
    expected = gate.expected_services(expectations, profile)
    return gate.evaluate(gate.parse_runs("\n".join(lines)), expected, allowances or [], **kwargs)


class ExpectedSet(unittest.TestCase):
    def test_a_service_without_a_module_is_not_expected_to_report_its_checks(self):
        expectations = dict(EXPECTATIONS, _roles={"gateway-redis": {"SecurityHeaders", "Dashboard"},
                                                 "verifier-redis": {"SecurityHeaders", "Dashboard", "Jwt"}})
        expected = gate.expected_services(expectations, "prod")
        self.assertEqual(expected["gateway-prod"], set(GATEWAY))
        self.assertEqual(expected["todo-prod"], set(ALL))

    def test_real_expectations_match_the_real_baselines(self):
        expectations = gate.load_expectations(HERE / "security-check-expectations.json",
                                              HERE / "composition-baselines.json")
        expected = gate.expected_services(expectations, "all")
        self.assertEqual(len(expected), 12)
        self.assertNotIn("noelia.jwt.key-separation", expected["gateway-prod"])
        self.assertIn("noelia.jwt.key-separation", expected["todo-prod"])
        self.assertIn("noelia.sessions.refresh-cookie", expected["user-staging"])
        self.assertNotIn("noelia.sessions.refresh-cookie", expected["todo-staging"])


class Judging(unittest.TestCase):
    def healthy(self):
        return run("gateway-prod", GATEWAY) + run("todo-prod", ALL)

    def test_a_complete_healthy_stack_passes(self):
        _, problems = judge(self.healthy())
        self.assertEqual(problems, [])

    def test_a_missing_whole_host_fails(self):
        _, problems = judge(run("todo-prod", ALL))
        self.assertEqual(len(problems), 1)
        self.assertIn("gateway-prod: no security check results", problems[0])

    def test_a_missing_check_id_fails(self):
        lines = run("gateway-prod", GATEWAY) + run("todo-prod", [i for i in ALL if i != "noelia.d.jwt"])
        _, problems = judge(lines)
        self.assertEqual(len(problems), 1)
        self.assertIn("todo-prod: expected check noelia.d.jwt is missing", problems[0])

    def test_an_old_pass_does_not_hide_a_new_fail(self):
        lines = (run("gateway-prod", GATEWAY)
                 + run("todo-prod", ALL)
                 + run("todo-prod", ALL, {"noelia.b.headers": "Fail"}))  # the restarted, later run
        _, problems = judge(lines)
        self.assertEqual(len(problems), 1)
        self.assertIn("noelia.b.headers Fail", problems[0])

    def test_an_old_fail_does_not_condemn_a_new_pass(self):
        lines = (run("todo-prod", ALL, {"noelia.b.headers": "Fail"})
                 + run("gateway-prod", GATEWAY) + run("todo-prod", ALL))
        _, problems = judge(lines)
        self.assertEqual(problems, [])

    def test_an_incomplete_latest_run_fails_even_after_a_complete_one(self):
        lines = run("gateway-prod", GATEWAY) + run("todo-prod", ALL) + run("todo-prod", ALL[:2])
        _, problems = judge(lines)
        self.assertEqual(len(problems), 2)
        self.assertTrue(all("todo-prod: expected check" in p for p in problems))

    def test_a_deliberately_omitted_module_is_not_a_failure(self):
        expectations = dict(EXPECTATIONS, OmittedModules={"gateway-prod": ["Dashboard"]})
        lines = run("gateway-prod", ["noelia.a.composition", "noelia.b.headers"]) + run("todo-prod", ALL)
        _, problems = judge(lines, expectations)
        self.assertEqual(problems, [])

    def test_the_same_omission_is_a_failure_when_it_was_not_declared(self):
        lines = run("gateway-prod", ["noelia.a.composition", "noelia.b.headers"]) + run("todo-prod", ALL)
        _, problems = judge(lines)
        self.assertEqual(len(problems), 1)
        self.assertIn("noelia.c.dashboard", problems[0])

    def test_rendered_development_lines_are_read_like_json_ones(self):
        lines = [rendered_line("gateway-prod", i) for i in GATEWAY] + run("todo-prod", ALL)
        _, problems = judge(lines)
        self.assertEqual(problems, [])

    def test_an_unallowed_fail_fails(self):
        lines = run("gateway-prod", GATEWAY, {"noelia.b.headers": "Fail"}) + run("todo-prod", ALL)
        _, problems = judge(lines)
        self.assertEqual(len(problems), 1)


class Allowances(unittest.TestCase):
    def lines(self):
        return (run("gateway-prod", GATEWAY, {"noelia.b.headers": "Fail"})
                + run("todo-prod", ALL, {"noelia.b.headers": "Fail"}))

    def test_an_allowance_covers_exactly_its_service_and_check(self):
        _, problems = judge(self.lines(), allowances=[("gateway-prod", "noelia.b.headers", "reason")])
        self.assertEqual(len(problems), 1)
        self.assertIn("todo-prod", problems[0])

    def test_a_stage_scope_covers_every_service_in_the_stage(self):
        _, problems = judge(self.lines(), allowances=[("prod", "noelia.b.headers", "reason")])
        self.assertEqual(problems, [])

    def test_an_allowance_for_one_check_does_not_cover_another(self):
        _, problems = judge(self.lines(), allowances=[("prod", "noelia.d.jwt", "reason")])
        self.assertEqual(len(problems), 2)

    def test_a_warning_passes_unless_asked_and_then_needs_a_scoped_allowance(self):
        lines = (run("gateway-prod", GATEWAY, {"noelia.c.dashboard": "Warning"})
                 + run("todo-prod", ALL))
        self.assertEqual(judge(lines)[1], [])
        self.assertEqual(len(judge(lines, fail_on_warning=True)[1]), 1)
        self.assertEqual(
            judge(lines, allowances=[("gateway-prod", "noelia.c.dashboard", "why")], fail_on_warning=True)[1], [])

    def test_a_missing_check_cannot_be_allowed_away(self):
        lines = run("gateway-prod", GATEWAY[:2]) + run("todo-prod", ALL)
        _, problems = judge(lines, allowances=[("prod", "noelia.c.dashboard", "why")])
        self.assertEqual(len(problems), 1)


class AllowanceInput(unittest.TestCase):
    def test_an_allow_option_needs_scope_and_reason(self):
        self.assertEqual(gate.parse_allow_option("prod/noelia.x=because"), ("prod", "noelia.x", "because"))
        for bad in ("noelia.x=because", "prod/noelia.x=", "prod/noelia.x"):
            with self.assertRaises(gate.GateError):
                gate.parse_allow_option(bad)

    def test_a_file_entry_needs_a_scope_id_and_reason(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "accepted.txt")
            path.write_text("# c\nprod noelia.x a reason\n")
            self.assertEqual(gate.read_accepted(path), [("prod", "noelia.x", "a reason")])

            path.write_text("prod noelia.x\n")
            with self.assertRaises(gate.GateError):
                gate.read_accepted(path)


class Runs(unittest.TestCase):
    def test_runs_split_where_the_id_order_restarts(self):
        runs = gate.parse_runs("\n".join(run("todo-prod", ALL) + run("todo-prod", ALL[:2])))
        self.assertEqual([len(r) for r in runs["todo-prod"]], [len(ALL), 2])

    def test_interleaved_services_keep_their_own_runs(self):
        a, b = run("gateway-prod", GATEWAY), run("todo-prod", ALL)
        interleaved = [line for pair in zip(a, b) for line in pair] + b[len(a):]
        runs = gate.parse_runs("\n".join(interleaved))
        self.assertEqual(len(runs["gateway-prod"]), 1)
        self.assertEqual(len(runs["todo-prod"]), 1)


if __name__ == "__main__":
    unittest.main()
