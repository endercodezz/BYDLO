"""Isolated generation tests: no downloads, engines, services or system networking."""
import argparse
import json
import os
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.parse import urlsplit, parse_qs
import install_reality as installer

CLIENT = "11111111-2222-4333-8444-555555555555"


class RealityTests(unittest.TestCase):
    def test_download_pins_match_client_lock(self):
        entries = json.loads((Path(__file__).resolve().parents[2] / "engines/engines.lock.json").read_text())["engines"]
        xray = next(entry for entry in entries if entry["id"] == "xray")
        self.assertEqual(xray["version"], installer.VERSION)
        for arch, rid in [("x86_64", "linux-x64"), ("aarch64", "linux-arm64")]:
            self.assertEqual(xray["assets"][rid]["sha256"], installer.ASSETS[arch][1])

    def test_server_and_link_agree(self):
        config = installer.make_config(CLIENT, "private", "1234567890abcdef", "example.com", 8443)
        inbound = config["inbounds"][0]
        link = urlsplit(installer.make_link("2001:db8::1", 8443, CLIENT, "public", "1234567890abcdef", "example.com"))
        self.assertEqual("2001:db8::1", link.hostname)
        self.assertEqual(inbound["port"], link.port)
        self.assertEqual(inbound["settings"]["clients"][0]["id"], link.username)
        query = parse_qs(link.query)
        reality = inbound["streamSettings"]["realitySettings"]
        self.assertEqual(reality["serverNames"], query["sni"])
        self.assertEqual(reality["shortIds"], query["sid"])
        self.assertEqual(["public"], query["pbk"])
        self.assertNotIn("private", link.geturl())
        self.assertEqual(["xtls-rprx-vision"], query["flow"])

    def test_invalid_inputs(self):
        for name in ["example.com:443", "https://example.com", "a\nb", "-example.com", "a..b", "x/../y"]:
            with self.assertRaises(ValueError):
                installer.hostname(name)
        for port in [0, -1, 65536]:
            with self.assertRaises(ValueError):
                installer.make_config(CLIENT, "private", "0123", "example.com", port)

    def test_key_output_formats(self):
        for label in ["Password (PublicKey)", "PublicKey", "Public key"]:
            self.assertEqual(("a" * 43, "b" * 43), installer.parse_keys(f"PrivateKey: {'a' * 43}\n{label}: {'b' * 43}\nHash32: ignored"))
        with self.assertRaises(ValueError):
            installer.parse_keys("unrecognized output")

    def test_safety_guard_precedes_all_side_effects(self):
        with patch.dict(os.environ, {"BYDLO_SAFE_DEV": "1"}), \
             patch.object(installer, "urlopen") as download, \
             patch.object(installer.subprocess, "run") as process, \
             patch.object(installer.Path, "mkdir") as mkdir:
            with self.assertRaises(RuntimeError):
                installer.install(argparse.Namespace())
            download.assert_not_called()
            process.assert_not_called()
            mkdir.assert_not_called()


if __name__ == "__main__":
    unittest.main()
