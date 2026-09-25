import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from datetime import datetime
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import wgbtrace


class WgbTraceParserTests(unittest.TestCase):
    def parse(self, text):
        return wgbtrace.parse_line(text, 7)

    def test_parses_cisco_envelope_and_scan_transition(self):
        event = self.parse(
            "[*08/16/2023 08:18:25.329223] UP_EVT:4 R1 State CONNECTED to SCAN_START"
        )

        self.assertEqual("2023-08-16 08:18:25.329223", event.timestamp_text)
        self.assertEqual("UP_EVT", event.module)
        self.assertEqual(4, event.level)
        self.assertEqual(1, event.radio)
        self.assertEqual("SCAN_START", event.event_type)
        self.assertEqual(
            {"from_state": "CONNECTED", "to_state": "SCAN_START"}, event.fields
        )

    def test_prefers_inner_wgb_timestamp_over_syslog_prefix(self):
        event = self.parse(
            "Jun 19 12:57:40 WGB kernel: [*06/19/2026 12:57:40.5753] "
            "DOT11_UPLINK_EV:4 R2 State EAPOL to CONNECTED"
        )

        self.assertEqual("2026-06-19 12:57:40.5753", event.timestamp_text)
        self.assertEqual(2, event.radio)
        self.assertEqual("CONNECTED", event.event_type)

    def test_extracts_best_ap_fields(self):
        event = self.parse(
            "[*06/23/2026 11:07:54.179799] UP_EVT:4 R1 "
            "Best AP: 94:0d:4b:2c:a0:2b, CH: 44, RSSI: 66"
        )

        self.assertEqual("BEST_AP", event.event_type)
        self.assertEqual("94:0D:4B:2C:A0:2B", event.fields["bssid"])
        self.assertEqual(44, event.fields["channel"])
        self.assertEqual(66, event.fields["rssi"])

    def test_recognizes_roam_trigger_and_abort(self):
        trigger = self.parse("RSSI Low(-66) Roaming triggered")
        abort = self.parse("New best AP not better enough")

        self.assertEqual("ROAM_TRIGGER", trigger.event_type)
        self.assertEqual({"reason": "RSSI_LOW", "rssi": -66}, trigger.fields)
        self.assertEqual("ROAM_ABORT", abort.event_type)

    def test_uses_numeric_auth_and_assoc_status_without_interpreting_codes(self):
        auth_ok = self.parse("R1 Auth Resp status=0")
        auth_failed = self.parse("R1 Auth Resp status=17")
        assoc_ok = self.parse("R1 Assoc Resp status 0")
        assoc_failed = self.parse("R1 Assoc Resp status 42")

        self.assertEqual("AUTH_SUCCESS", auth_ok.event_type)
        self.assertEqual("AUTH_FAILURE", auth_failed.event_type)
        self.assertEqual(17, auth_failed.fields["status"])
        self.assertEqual("ASSOC_SUCCESS", assoc_ok.event_type)
        self.assertEqual("ASSOC_FAILURE", assoc_failed.event_type)
        self.assertEqual(42, assoc_failed.fields["status"])

    def test_recognizes_eapol_disconnect_and_delete(self):
        self.assertEqual("EAPOL_START", self.parse("EAPOL.M1").event_type)
        self.assertEqual("EAPOL_COMPLETE", self.parse("EAPOL.M4").event_type)
        disconnected = self.parse("Disconnected client. Reason (15)")
        deleted = self.parse("R1 Delete client FC:58:9A:17:B3:E7")

        self.assertEqual("DISCONNECTED", disconnected.event_type)
        self.assertEqual("15", disconnected.fields["reason"])
        self.assertEqual("DELETE_CLIENT", deleted.event_type)
        self.assertEqual("FC:58:9A:17:B3:E7", deleted.fields["mac"])

    def test_surfaces_interesting_unknown_lines_and_retains_ordinary_lines(self):
        interesting = self.parse("Unexpected deauth detail code 771")
        ordinary = self.parse("current power level: 1")

        self.assertEqual("UNCLASSIFIED_INTERESTING", interesting.event_type)
        self.assertEqual("RAW", ordinary.event_type)
        self.assertEqual("current power level: 1", ordinary.raw)

    def test_continuation_lines_inherit_timestamp_for_around_filter(self):
        events = wgbtrace.parse_lines(
            [
                "[*06/23/2026 11:07:57.205836] UP_EVT:4 R1 RSSI Low(-66) Roaming triggered\n",
                "wrapped continuation text\n",
                "[*06/23/2026 11:08:10.000000] UP_EVT:4 R1 State EAPOL to CONNECTED\n",
            ]
        )
        selected = wgbtrace.select_around(
            events, datetime(2026, 6, 23, 11, 7, 57, 205836), 0.1
        )

        self.assertEqual(2, len(selected))
        self.assertTrue(selected[1].timestamp_inherited)

    def test_around_cli_prints_parsed_and_raw_sections(self):
        content = (
            "[*06/23/2026 11:07:57.205836] UP_EVT:4 R1 RSSI Low(-66) Roaming triggered\n"
            "[*06/23/2026 11:08:10.000000] UP_EVT:4 R1 State EAPOL to CONNECTED\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.txt"
            path.write_text(content, encoding="utf-8")
            output = io.StringIO()
            with redirect_stdout(output):
                result = wgbtrace.main(
                    [str(path), "--around", "2026-06-23 11:07:57.205836", "--window", "0.1"]
                )

        self.assertEqual(0, result)
        self.assertIn("Parsed events:", output.getvalue())
        self.assertIn("ROAM_TRIGGER", output.getvalue())
        self.assertIn("Raw log:", output.getvalue())
        self.assertNotIn("CONNECTED", output.getvalue())

    def test_json_contains_original_raw_line(self):
        event = self.parse("unclassified ordinary line")
        data = json.loads(event.as_json())

        self.assertEqual("unclassified ordinary line", data["raw"])
        self.assertEqual("RAW", data["event_type"])


if __name__ == "__main__":
    unittest.main()
