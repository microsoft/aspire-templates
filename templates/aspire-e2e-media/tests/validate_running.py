#!/usr/bin/env python3
"""Discover an exact API endpoint from Aspire describe and run business assertions."""
import json
from pathlib import Path
import subprocess
import sys

text = Path(sys.argv[1]).read_text()
data = json.loads(text[text.index("{"):])
resources = data["resources"]
api = next(resource for resource in resources if resource.get("displayName", resource["name"]) == "api")
url = next(endpoint["url"] for endpoint in api["urls"] if endpoint["url"].startswith("http"))
frontend = next(resource for resource in resources if resource.get("displayName", resource["name"]) == "frontend")
frontend_url = next(endpoint["url"] for endpoint in frontend["urls"] if endpoint["url"].startswith("http"))
subprocess.run([sys.executable, "tests/business_flow.py", url, "--frontend-url", frontend_url, *sys.argv[2:]], check=True)
