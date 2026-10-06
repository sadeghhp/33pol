"""python3 -m unittest discover -s tests/scripts -p "test_*.py" """

import importlib.util
import pathlib
import unittest

_PATH = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "rate-limit-cap-keys.py"
_SPEC = importlib.util.spec_from_file_location("rate_limit_cap_keys", _PATH)
cap_keys = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(cap_keys)


def config(rules=None, rpm=120, burst=15, streams=30):
    return {
        "enabled": True,
        "adaptiveEnabled": False,
        "default": {"rpm": rpm, "burst": burst, "maxConcurrentStreams": streams},
        "plans": {},
        "rules": rules or [],
        "version": 7,
        "writable": True,
    }


class ObservedMinutesTests(unittest.TestCase):
    def test_a_tracker_older_than_the_window_covers_the_whole_window(self):
        report = {"windowMinutes": 180, "generatedUtc": "2026-10-06T10:00:00.1234567+00:00",
                  "tracker": {"trackingSinceUtc": "2026-10-04T11:54:00.5+00:00"}}

        self.assertEqual(cap_keys.observed_minutes(report), 180)

    def test_a_restart_inside_the_window_shortens_what_was_observed(self):
        report = {"windowMinutes": 180, "generatedUtc": "2026-10-06T10:03:00.8859024+00:00",
                  "tracker": {"trackingSinceUtc": "2026-10-06T09:53:00.5942808+00:00"}}

        self.assertEqual(cap_keys.observed_minutes(report), 10)

    def test_a_report_without_a_tracker_is_taken_at_its_window(self):
        self.assertEqual(cap_keys.observed_minutes({"windowMinutes": 60}), 60)
        self.assertEqual(cap_keys.observed_minutes({}), cap_keys.USAGE_MINUTES)

    def test_rates_are_requests_over_the_minutes_observed(self):
        class Restarted:
            def call(self, method, path, body=None, if_match=None):
                return 200, {"windowMinutes": 180, "generatedUtc": "2026-10-06T10:03:00+00:00",
                             "tracker": {"trackingSinceUtc": "2026-10-06T09:53:00+00:00"},
                             "byApiKey": [{"apiKeyId": "A", "requests": 70, "requestsPerMinute": 0.39}]}

        usage, minutes = cap_keys.usage_by_key(Restarted())

        self.assertEqual(minutes, 10)
        self.assertEqual(usage["a"]["requestsPerMinute"], 7)


class PlanCapsTests(unittest.TestCase):
    def test_a_key_without_a_rule_is_capped_at_its_share_of_the_tier(self):
        proposed, decisions = cap_keys.plan_caps(
            config(), [{"id": "A", "label": "one"}], max_share=0.4, floor_rpm=5)

        self.assertEqual(
            proposed["rules"],
            [{"scope": "api_key", "target": "A", "rpm": 48, "burst": 6,
              "maxConcurrentStreams": 12, "enabled": True}])
        self.assertEqual(decisions[0]["action"], "add")

    def test_existing_rules_are_kept_and_that_key_is_not_given_a_second_one(self):
        mine = {"scope": "api_key", "target": "aa-11", "rpm": 200, "burst": 0, "maxConcurrentStreams": 0}
        model = {"scope": "model", "target": "m", "rpm": 600, "burst": 60, "maxConcurrentStreams": 40}

        proposed, decisions = cap_keys.plan_caps(
            config([mine, model]), [{"id": "AA-11"}, {"id": "B"}], max_share=0.5, floor_rpm=5)

        self.assertEqual(proposed["rules"][:2], [mine, model])
        self.assertEqual([r["target"] for r in proposed["rules"][2:]], ["B"])
        self.assertEqual(decisions[0]["action"], "keep: has a rule")

    def test_revoked_and_archived_keys_get_no_rule(self):
        proposed, _ = cap_keys.plan_caps(
            config(),
            [{"id": "A", "revokedAt": "2026-01-01T00:00:00Z"}, {"id": "B", "archivedAt": "2026-01-01T00:00:00Z"}],
            max_share=0.4, floor_rpm=5)

        self.assertEqual(proposed["rules"], [])

    def test_rate_only_caps_the_rate_and_leaves_streams_uncapped(self):
        proposed, decisions = cap_keys.plan_caps(
            config(), [{"id": "A"}], max_share=0.4, floor_rpm=5, rate_only=True)

        self.assertEqual(
            proposed["rules"],
            [{"scope": "api_key", "target": "A", "rpm": 48, "burst": 6,
              "maxConcurrentStreams": 0, "enabled": True}])
        self.assertEqual(decisions[0]["maxConcurrentStreams"], 0)

    def test_unlimited_streams_stay_unlimited_and_a_small_share_still_allows_one(self):
        unlimited, _ = cap_keys.plan_caps(config(streams=0), [{"id": "A"}], max_share=0.4, floor_rpm=5)
        tiny, _ = cap_keys.plan_caps(config(streams=2), [{"id": "A"}], max_share=0.1, floor_rpm=5)

        self.assertEqual(unlimited["rules"][0]["maxConcurrentStreams"], 0)
        self.assertEqual(tiny["rules"][0]["maxConcurrentStreams"], 1)

    def test_the_floor_holds_when_the_share_is_smaller(self):
        proposed, _ = cap_keys.plan_caps(config(rpm=10), [{"id": "A"}], max_share=0.1, floor_rpm=5)

        self.assertEqual(proposed["rules"][0]["rpm"], 5)

    def test_a_new_tier_is_written_and_the_caps_are_shares_of_it(self):
        proposed, _ = cap_keys.plan_caps(
            config(), [{"id": "A"}], max_share=0.25, floor_rpm=5,
            tier={"rpm": 600, "burst": None, "maxConcurrentStreams": 100})

        self.assertEqual(proposed["default"], {"rpm": 600, "burst": 15, "maxConcurrentStreams": 100})
        self.assertEqual(proposed["rules"][0]["rpm"], 150)
        self.assertEqual(proposed["rules"][0]["maxConcurrentStreams"], 25)

    def test_the_input_is_not_modified(self):
        original = config()
        cap_keys.plan_caps(original, [{"id": "A"}], max_share=0.4, floor_rpm=5, tier={"rpm": 600})

        self.assertEqual(original, config())

    def test_a_share_outside_zero_to_one_is_refused(self):
        for share in (0, 1.5, -0.1):
            with self.assertRaises(ValueError):
                cap_keys.plan_caps(config(), [], max_share=share, floor_rpm=5)

    def test_the_put_body_carries_only_what_the_api_reads(self):
        body = cap_keys.put_body(config())

        self.assertEqual(sorted(body), ["adaptiveEnabled", "default", "enabled", "plans", "rules"])


if __name__ == "__main__":
    unittest.main()
