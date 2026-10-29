#!/usr/bin/env python3
"""Business assertions against a running studio; standard library only."""
import argparse
import json
import os
from pathlib import Path
import struct
import sys
import time
import urllib.error
import urllib.request
import uuid
import zlib

parser = argparse.ArgumentParser()
parser.add_argument("url")
parser.add_argument("--timeout", type=int, default=240)
parser.add_argument("--frontend-url", help="Local Vite URL; deployed frontend is served by the API")
parser.add_argument("--save", help="Save IDs for a subsequent --verify restart check")
parser.add_argument("--verify", help="Verify saved durable jobs after an AppHost restart")
parser.add_argument("--enqueue-only", action="store_true", help="Submit while worker is stopped, then verify after restart")
args = parser.parse_args()
token = os.environ.get("STUDIO_TOKEN", "local-media-walkthrough")
base = args.url.rstrip("/")


def call(path, data=None, headers=None, expected=200, authorized=True):
    request_headers = {"Authorization": f"Bearer {token}"} if authorized else {}
    request_headers.update(headers or {})
    request = urllib.request.Request(base + path, data=data, headers=request_headers)
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read()
        assert response.status == expected, (path, response.status, body[:1000])
        return body


def png(width=1200, height=800):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    pixels = b"".join(b"\0" + bytes((177, 31, 75)) * width for _ in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")


def upload(key, failure=False, content=None, expected=202):
    boundary = "studio-business-proof"
    content = png() if content is None else content
    data = (
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"simulateFailure\"\r\n\r\n{str(failure).lower()}\r\n"
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"proof.png\"\r\nContent-Type: image/png\r\n\r\n"
    ).encode() + content + f"\r\n--{boundary}--\r\n".encode()
    body = call("/api/jobs", data, {"Content-Type": f"multipart/form-data; boundary={boundary}", "Idempotency-Key": key}, expected)
    return json.loads(body)


def wait(job_id, state):
    deadline = time.monotonic() + args.timeout
    while time.monotonic() < deadline:
        job = json.loads(call(f"/api/jobs/{job_id}"))
        if job["state"] == state:
            return job
        time.sleep(1)
    raise AssertionError(f"Job {job_id} did not reach {state}: {job}")


def rendition(job_id):
    data = call(f"/api/jobs/{job_id}/rendition")
    assert data[:2] == b"\xff\xd8", "Rendition must be JPEG, not the uploaded PNG"
    # Parse JPEG frame dimensions, not just its signature.
    offset = 2
    while offset < len(data):
        assert data[offset] == 0xFF
        marker = data[offset + 1]
        length = int.from_bytes(data[offset + 2:offset + 4], "big")
        if marker in (0xC0, 0xC1, 0xC2):
            height, width = struct.unpack(">HH", data[offset + 5:offset + 9])
            assert (width, height) == (640, 427), (width, height)
            break
        offset += 2 + length
    else:
        raise AssertionError("JPEG has no frame dimensions")
    call(f"/api/jobs/{job_id}/rendition", expected=401, authorized=False)


call("/api/jobs", expected=401, authorized=False)
with urllib.request.urlopen(args.frontend_url or base, timeout=30) as frontend:
    assert b'<div id="root">' in frontend.read()
if args.enqueue_only:
    assert args.save, "--enqueue-only requires --save"
    key = str(uuid.uuid4())
    job = upload(key)
    assert job["state"] == "Queued"
    Path(args.save).write_text(json.dumps({"key": key, "ids": [job["id"]]}, indent=2))
    print("PASS durable submission while worker is unavailable")
    sys.exit(0)
if args.verify:
    saved = json.loads(Path(args.verify).read_text())
    for job_id in saved["ids"]:
        job = wait(job_id, "Ready")
        rendition(job_id)
        assert job["attempts"] == 1, "Restart or duplicate delivery reprocessed a completed job"
    duplicate = upload(saved["key"], expected=200)
    assert duplicate["id"] == saved["ids"][0]
    print("PASS restart persistence, completed-job deduplication and private renditions")
else:
    key = str(uuid.uuid4())
    first = upload(key)
    assert upload(key, expected=200)["id"] == first["id"]
    upload(key, content=png(1000, 800), expected=409)
    upload(str(uuid.uuid4()), content=b"not an image", expected=400)
    ready = wait(first["id"], "Ready")
    assert ready["attempts"] == 1
    rendition(first["id"])
    call(f"/api/jobs/{first['id']}/retry", b"", expected=409)
    failure_key = str(uuid.uuid4())
    failed = upload(failure_key, failure=True)
    terminal = wait(failed["id"], "Failed")
    assert terminal["attempts"] == 3 and terminal["error"]
    call(f"/api/jobs/{failed['id']}/rendition", expected=404)
    call(f"/api/jobs/{failed['id']}/retry", b"", expected=202)
    recovered = wait(failed["id"], "Ready")
    assert recovered["attempts"] == 1 and recovered["generation"] == 1 and recovered["error"] is None
    rendition(failed["id"])
    assert upload(failure_key, failure=True, expected=200)["id"] == failed["id"]
    if args.save:
        Path(args.save).write_text(json.dumps({"key": key, "ids": [first["id"], failed["id"]]}, indent=2))
    print("PASS authenticated upload, validation, idempotency/conflict, 640px JPEG, three-attempt failure, private data and retry recovery")
