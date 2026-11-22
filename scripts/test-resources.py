"""Locate the deployed Driver Function App from ARM without reading account keys."""
import json
import os
from pathlib import Path
import re
import subprocess


def resolve_origin(environment, prefix, group, custom_domain=""):
    if environment not in {"test", "prod"}:
        raise ValueError("Expected the test or prod deployment environment")
    if not re.fullmatch(r"[a-z][a-z0-9]{0,15}", prefix) or not group.strip():
        raise ValueError("A Driver resource prefix and resource group are required")
    abbreviations = json.loads((Path(__file__).resolve().parents[1] / "infra/abbreviations.json").read_text())
    function = f"{prefix}-{abbreviations['webSitesFunctions']}{environment}"
    site = json.loads(subprocess.check_output(
        ["az", "resource", "show", "--resource-group", group, "--name", function,
         "--resource-type", "Microsoft.Web/sites", "--api-version", "2024-11-01",
         "--query", "{defaultHostName:properties.defaultHostName, hostNames:properties.hostNames}",
         "--only-show-errors", "--output", "json"], text=True))
    hostname = site["defaultHostName"]
    if not re.fullmatch(re.escape(function) + r"(?:-[a-z0-9]+)?(?:\.[a-z0-9-]+)*\.azurewebsites\.net", hostname):
        raise ValueError("Azure returned an unexpected Function App hostname")
    if environment == "prod" and custom_domain:
        if not re.fullmatch(r"[a-zA-Z0-9.-]+", custom_domain) or custom_domain.lower() not in {
                name.lower() for name in site["hostNames"]}:
            raise ValueError("The configured production domain is not bound to the Driver Function App")
        hostname = custom_domain
    return "https://" + hostname


if __name__ == "__main__":
    origin = resolve_origin(os.environ["AZURE_ENV_NAME"], os.environ["APP_SHORT_NAME"],
                            os.environ["AZURE_RESOURCE_GROUP"], os.environ.get("CUSTOM_DOMAIN_NAME", ""))
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as stream:
        stream.write(f"READONLY_API_URL={origin}\n")
