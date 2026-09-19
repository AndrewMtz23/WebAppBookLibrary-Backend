"""Reproducible serial latency baseline. Refuses non-loopback/non-QA endpoints."""
import datetime
import json
import math
import platform
import statistics
import time
import urllib.request

BASE = "http://127.0.0.1:7184"

def request(path, data=None, token=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=json.dumps(data).encode() if data else None, headers=headers)
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.load(response)

fixture = request('/__qa')
assert fixture['fixture'] == 'booklibrary-phase5'
assert fixture['databaseName'].startswith('booklibrary_ui_test_')
token = request('/api/auth/login', {'username': 'qa_admin', 'password': 'QaLocalOnly!2026'})['token']
today = datetime.datetime.now(datetime.timezone.utc).date()
period = f"from={today - datetime.timedelta(days=29)}&to={today}&timezone=UTC"
paths = ['/api/health/live', '/api/health/ready', '/api/books?page=1&pageSize=20', '/api/admin/users?page=1&pageSize=20', '/api/loans?page=1&pageSize=20', '/api/dashboard/admin?' + period]
results = []
for path in paths:
    for _ in range(3):
        request(path, token=token)
    samples = []
    for _ in range(30):
        started = time.perf_counter()
        request(path, token=token)
        samples.append((time.perf_counter() - started) * 1000)
    samples.sort()
    results.append({'endpoint': path, 'samples': len(samples), 'p50_ms': round(statistics.median(samples), 2), 'p95_ms': round(samples[math.ceil(len(samples)*.95)-1], 2), 'max_ms': round(max(samples), 2)})
print(json.dumps({'timestamp_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'environment': platform.platform(), 'mode': 'serial loopback; 3 warmups and 30 samples per endpoint; QA seed, not a load test', 'results': results}, indent=2))
