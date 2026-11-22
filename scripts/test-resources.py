"""Locate the deployed Driver Function App from ARM without reading account keys."""
import json
import os
from pathlib import Path
import re
import subprocess


def resolve_origin(environment, prefix, group):
    if environment not in {"test", "prod"}:
        raise ValueError("Expected the test or prod deployment environment")
    if not re.fullmatch(r"[a-z][a-z0-9]{0,15}", prefix) or not group.strip():
        raise ValueError("A Driver resource prefix and resource group are required")
    abbreviations = json.loads((Path(__file__).resolve().parents[1] / "infra/abbreviations.json").read_text())
    function = f"{prefix}-{abbreviations['webSitesFunctions']}{environment}"
    hostname = subprocess.check_output(
        ["az", "resource", "show", "--resource-group", group, "--name", function,
         "--resource-type", "Microsoft.Web/sites", "--api-version", "2024-11-01",
         "--query", "properties.defaultHostName", "--only-show-errors", "--output", "tsv"], text=True).strip()
    if not re.fullmatch(re.escape(function) + r"(?:-[a-z0-9]+)?(?:\.[a-z0-9-]+)*\.azurewebsites\.net", hostname):
        raise ValueError("Azure returned an unexpected Function App hostname")
    return "https://" + hostname


if __name__ == "__main__":
    origin = resolve_origin(os.environ["AZURE_ENV_NAME"], os.environ["APP_SHORT_NAME"],
                            os.environ["AZURE_RESOURCE_GROUP"])
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as stream:
        stream.write(f"READONLY_API_URL={origin}\n")
