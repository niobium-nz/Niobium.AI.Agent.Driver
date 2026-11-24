"""Verify the deployed commit using only GET /health on the selected HTTPS origin."""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def denies_anonymous(client, request):
    try:
        with client.open(request, timeout=10) as response:
            return response.status in (401, 403)
    except HTTPError as error:
        with error:
            return error.code in (401, 403)


def check(origin, expected, wait_seconds=180, opener=None, *, access_token=None):
    parsed = urlsplit(origin)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or parsed.query or parsed.fragment or parsed.path not in ("", "/")):
        raise ValueError("An HTTPS service origin is required")
    if not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise ValueError("An exact deployment commit is required")
    if access_token is not None and not re.fullmatch(r"[!-~]{1,16384}", access_token):
        raise ValueError("A valid environment-supplied health bearer is required")
    handlers = [NoRedirect()]
    if access_token is not None:
        handlers.append(ProxyHandler({}))
    client = opener or build_opener(*handlers)
    url = origin.rstrip("/") + "/health"
    anonymous = Request(url, method="GET")
    request = anonymous if access_token is None else Request(
        url, method="GET", headers={"Authorization": "Bearer " + access_token})
    deadline = time.monotonic() + wait_seconds
    while True:
        try:
            with client.open(request, timeout=10) as response:
                body = json.loads(response.read(4096))
                ready = (response.status == 200 and response.headers.get_content_type() == "application/json"
                         and isinstance(body, dict) and body.get("status") == "ready" and body.get("commit") == expected)
            if ready and (access_token is None or denies_anonymous(client, anonymous)):
                return True
        except HTTPError as error:
            error.close()
        except (URLError, OSError, ValueError):
            pass
        if time.monotonic() >= deadline:
            return False
        time.sleep(2)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--require-authentication", action="store_true",
                        help="Require a health-only bearer from READONLY_API_ACCESS_TOKEN and deny anonymous health.")
    args = parser.parse_args()
    expected = os.environ.get("READONLY_EXPECTED_COMMIT", "")
    passed = False
    try:
        access_token = os.environ.get("READONLY_API_ACCESS_TOKEN", "") if args.require_authentication else None
        passed = check(os.environ["READONLY_API_URL"], expected, access_token=access_token)
    finally:
        report = {"commit": expected, "checks": [{"check": "deployed_commit", "passed": passed}], "success": passed}
        if args.require_authentication:
            report["mode"] = "authenticated"
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
