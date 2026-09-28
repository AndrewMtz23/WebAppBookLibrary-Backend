"""Synthetic reading load and BSON restore rehearsal. Requires pymongo and the loopback QA host.
No URL/database override: refuses anything except the host's random local QA database.
"""
import concurrent.futures
import json
import math
import re
import time
import uuid
from datetime import datetime, timezone, timedelta
from urllib.request import Request, urlopen
from bson import BSON, ObjectId
from pymongo import MongoClient

BASE = 'http://127.0.0.1:7184'


def api(path, token=None, body=None, method=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    request = Request(BASE + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def summary(samples):
    ordered = sorted(samples)
    return {'samples': len(samples), 'p50_ms': round(ordered[len(ordered)//2], 2),
            'p95_ms': round(ordered[math.ceil(len(ordered)*.95)-1], 2), 'max_ms': round(max(samples), 2)}


def main():
    marker = api('/__qa')
    assert marker['fixture'] == 'booklibrary-phase5'
    assert re.fullmatch(r'booklibrary_ui_test_[a-f0-9]{32}', marker['databaseName'])
    client = MongoClient('mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa', serverSelectionTimeoutMS=5000)
    db = client[marker['databaseName']]
    admin = api('/api/auth/login', body={'email': 'admin@booklibrary.invalid', 'password': 'QaLocalOnly!2026'})['token']
    tag = uuid.uuid4().hex[:10]
    users = []
    book = db.Books.find_one({'IsActive': True})
    assert book
    for index in range(24):
        name = f'load_{tag}_{index}'
        email = name + '@example.invalid'
        api('/api/admin/users', admin, {'username': name, 'displayName': 'Synthetic load', 'email': email, 'password': 'QaLocalOnly!2026', 'role': 'user'})
        auth = api('/api/auth/login', body={'email': email, 'password': 'QaLocalOnly!2026'})
        users.append(auth)

    def writer(auth):
        revision = None
        timings = []
        for progress in range(10, 60, 10):
            start = time.perf_counter()
            result = api('/api/reading/my/books/' + str(book['_id']), auth['token'],
                         {'status': 'reading', 'progressMode': 'percent', 'progressPercent': progress,
                          'currentPage': None, 'expectedRevision': revision}, 'PUT')
            assert result['progressPercent'] == progress
            revision = result['revision']
            timings.append((time.perf_counter() - start)*1000)
        return timings

    start = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=24) as pool:
        samples = [sample for batch in pool.map(writer, users) for sample in batch]
    report = {'shared_book_writes': summary(samples), 'write_elapsed_s': round(time.perf_counter()-start, 2), 'concurrent_users': 24}
    # Large personal history; newest 90% of active readings refer to hidden books.
    owner = ObjectId(users[0]['user']['id'])
    now = datetime.now(timezone.utc)
    books, entries = [], []
    for index in range(3000):
        book_id = ObjectId()
        books.append({**book, '_id': book_id, 'Title': f'Synthetic {tag} {index}', 'IsActive': index % 10 == 0})
        entries.append({'_id': ObjectId(), 'UserId': owner, 'BookId': book_id, 'Status': 'reading',
                        'ProgressMode': 'percent', 'ProgressPercent': 30, 'CurrentPage': None, 'PageCountSnapshot': None,
                        'StartedAt': now, 'FinishedAt': None, 'LastProgressAt': now + timedelta(seconds=index),
                        'UpdatedAt': now + timedelta(seconds=index), 'Version': 1})
    db.Books.insert_many(books)
    db.ReadingEntries.insert_many(entries)
    for name, path in [('first_page', '/api/reading/my?pageSize=20'), ('deep_page', '/api/reading/my?pageSize=20&page=150'), ('latest', '/api/reading/my/latest')]:
        timings = []
        for iteration in range(33):
            start = time.perf_counter()
            value = api(path, users[0]['token'])
            if name == 'latest':
                assert value['entry']['bookAvailable'] and value['entry']['title'].endswith('2990')
            else:
                assert len(value['items']) == 20 and value['totalItems'] == 3001
            if iteration >= 3:
                timings.append((time.perf_counter()-start)*1000)
        report[name] = summary(timings)
    explain = db.ReadingEntries.find({'UserId': owner}).sort([('UpdatedAt', -1), ('_id', -1)]).skip(2980).limit(20).explain()
    report['deep_page_plan'] = {key: explain['executionStats'][key] for key in ['nReturned', 'totalKeysExamined', 'totalDocsExamined']}
    report['deep_page_index_used'] = 'ix_reading_updated' in json.dumps(explain, default=str)
    # Restore exact BSON plus indexes into a separate, disposable database.
    restore_name = 'booklibrary_ui_test_' + uuid.uuid4().hex
    restore = client[restore_name]
    try:
        collections = ['Users', 'Books', 'ReadingEntries']
        restored = {}
        for name in collections:
            snapshot = [BSON.encode(row) for row in db[name].find().sort('_id')]
            if snapshot:
                restore[name].insert_many([BSON(data).decode() for data in snapshot])
            for index in db[name].list_indexes():
                if index['name'] != '_id_':
                    restore[name].create_index(list(index['key'].items()), **{k:v for k,v in index.items() if k not in ['key', 'v', 'ns']})
            assert snapshot == [BSON.encode(row) for row in restore[name].find().sort('_id')]
            restored[name] = len(snapshot)
        report['bson_restore_verified'] = restored
        report['reading_indexes_restored'] = list(restore.ReadingEntries.index_information())
    finally:
        client.drop_database(restore_name)
    report['scope'] = 'Synthetic loopback measurement, not a production capacity guarantee or provider backup test.'
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
