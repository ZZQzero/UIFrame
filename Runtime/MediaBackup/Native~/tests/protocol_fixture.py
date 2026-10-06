"""HTTP response fixtures; all client transitions execute the production ABI."""
import json

NOW = 621355968000000000
LIMITS = dict(maxItems=32, maxRequestBytes=262144, maxResponseBytes=524288,
              maxDescriptorBytes=8192, maxFileBytes=536870912)


def control(db, request='ce', executor=0, now=NOW, submit=True):
    rows = db.call(80, request, executor, now, 0, 1)[-1]
    assert rows, 'No eligible control request'
    if submit:
        db.call(81, request); db.call(82, request, 'system:' + request); db.call(83, request)
    return rows[0]


def response(db, request, status='Confirmed'):
    saved = db.call(104, request)[-1][0]
    items = json.loads(saved['body'])['items']
    namespace = db.call(1)[-1][0]['source_namespace']
    results = []
    for item in items:
        task = db.call(7, item['clientTaskId'])[-1][0]
        identity = dict(clientTaskId=task['id'], attemptGeneration=item['attemptGeneration'])
        result = dict(identity, status=status)
        content = dict(identity, sha256=task['sha256'], byteCount=task['byte_count'])
        if status == 'Confirmed':
            result['receipt'] = dict(content, backupId=task['sha256'], sourceNamespace=namespace,
                                     sourceId=task['source_id'], sourceVersion=task['content_version'], confirmedAt=0)
        elif status == 'UploadRequired':
            result['upload'] = dict(content, uploadId='upload-' + task['id'], url='https://storage.invalid/' + task['id'],
                                   headers=[], expiresAt=86400000, successStatusCodes=[200, 201, 204])
        results.append(result)
    return json.dumps(dict(protocolVersion=2, requestId=request, account='test', serverTime=0, limits=LIMITS, items=results))


def apply(db, request, status='Confirmed', now=NOW):
    body = response(db, request, status)
    descriptors = db.call(84, request, body)[-1]
    values = [request, body, now, len(descriptors)]
    for item in descriptors: values.extend([item['task_id'], 'protected-descriptor-' + item['task_id']])
    db.call(85, *values)
    return body
