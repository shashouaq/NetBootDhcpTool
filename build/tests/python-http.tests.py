"""Offline tests of the real fallback code, using HTTPX's in-memory transport."""
import importlib.util
import pathlib
import ssl
import unittest
from unittest.mock import patch

import httpx

source = pathlib.Path(__file__).parents[1] / "release-pipeline" / "reliable_http.py"
spec = importlib.util.spec_from_file_location("reliable_http", source)
fallback = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fallback)


class FallbackTests(unittest.TestCase):
    def test_verified_ssl_and_timeouts(self):
        real_client = httpx.Client
        seen = {}

        def create_client(**options):
            seen.update(options)
            options["transport"] = httpx.MockTransport(lambda request: httpx.Response(200, json={"version": "1.1.0"}))
            return real_client(**options)

        with patch.object(httpx, "Client", create_client):
            response = fallback.request(dict(uri="https://api.github.com/repos/test/repo", headers={}, connectTimeout=15, readTimeout=60))
        self.assertEqual(200, response["status"])
        self.assertEqual(ssl.CERT_REQUIRED, seen["verify"].verify_mode)
        self.assertTrue(seen["verify"].check_hostname)
        self.assertEqual(15, seen["timeout"].connect)
        self.assertEqual(60, seen["timeout"].read)

    def test_public_asset_cannot_receive_authorization(self):
        with self.assertRaises(ValueError):
            fallback.request(dict(uri="https://github.com/test/repo/file", headers={"Authorization": "Bearer test-secret"}, connectTimeout=15, readTimeout=60))

    def test_insecure_redirect_fails(self):
        real_client = httpx.Client

        def create_client(**options):
            options["transport"] = httpx.MockTransport(lambda request: httpx.Response(302, headers={"location": "http://github.com/unsafe"}))
            return real_client(**options)

        with patch.object(httpx, "Client", create_client), self.assertRaises(ValueError):
            fallback.request(dict(uri="https://github.com/test/repo/file", headers={}, connectTimeout=15, readTimeout=60))

    def test_redirect_userinfo_fails_before_second_request(self):
        real_client = httpx.Client
        calls = []
        def respond(request):
            calls.append(request)
            if len(calls) == 1:
                return httpx.Response(302, headers={"location": "https://user:password@github.com/unsafe"})
            return httpx.Response(200, content=b"unexpected authenticated asset GET")
        def create_client(**options):
            options["transport"] = httpx.MockTransport(respond)
            return real_client(**options)
        with patch.object(httpx, "Client", create_client), self.assertRaises(ValueError):
            fallback.request(dict(uri="https://github.com/test/repo/file", headers={}, connectTimeout=15, readTimeout=60))
        self.assertEqual(1, len(calls))

    def test_unapproved_host_and_userinfo_fail(self):
        for uri in ("http://github.com/file", "https://example.com/file", "https://secret@github.com/file"):
            with self.assertRaises(ValueError):
                fallback.request(dict(uri=uri, headers={}, connectTimeout=15, readTimeout=60))


if __name__ == "__main__":
    unittest.main()
