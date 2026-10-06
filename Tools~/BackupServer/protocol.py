"""The bounded v2 wire contract shared by the reference HTTP handlers."""
import hashlib
import json
import re

VERSION = 2
MAX_ITEMS = 32
MAX_REQUEST_BYTES = 256 * 1024
MAX_RESPONSE_BYTES = 512 * 1024
MAX_DESCRIPTOR_BYTES = 8 * 1024
MAX_FILE_BYTES = 512 * 1024 * 1024
UPLOAD_TTL_MS = 24 * 60 * 60 * 1000
CHECK_INTERVAL_MS = 5000
CONFIRMATION_TIMEOUT_MS = 24 * 60 * 60 * 1000
MAX_CONFIRMATION_CHECKS = 256
ID = re.compile(r'^[A-Za-z0-9._:-]{1,128}$')
SHA = re.compile(r'^[a-f0-9]{64}$')
IDENTITY_FIELDS = {'clientTaskId', 'attemptGeneration'}
PLAN_FIELDS = IDENTITY_FIELDS | {'sourceNamespace', 'sourceId', 'sourceVersion', 'sha256', 'byteCount', 'mimeType', 'name'}
LIMITS = dict(maxItems=MAX_ITEMS, maxRequestBytes=MAX_REQUEST_BYTES,
              maxResponseBytes=MAX_RESPONSE_BYTES, maxDescriptorBytes=MAX_DESCRIPTOR_BYTES,
              maxFileBytes=MAX_FILE_BYTES)


class ProtocolError(Exception):
    def __init__(self, status, code, message):
        super().__init__(message)
        self.status, self.code = status, code


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'), sort_keys=True).encode('utf-8')


def fingerprint(value):
    return hashlib.sha256(encode(value)).hexdigest()


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError('Duplicate JSON property')
        value[key] = item
    return value


def decode_request(data, operation):
    if len(data) > MAX_REQUEST_BYTES:
        raise ProtocolError(413, 'RequestTooLarge', 'Control request exceeds 256 KiB')
    try:
        body = json.loads(data, object_pairs_hook=_unique_object,
                          parse_constant=lambda value: (_ for _ in ()).throw(ValueError('Non-finite number')))
        if not isinstance(body, dict) or set(body) != {'protocolVersion', 'requestId', 'items'}:
            raise ValueError('Invalid control envelope')
        if type(body['protocolVersion']) is not int or body['protocolVersion'] != VERSION:
            raise ValueError('Only protocol v2 is supported')
        if not isinstance(body['requestId'], str) or not ID.fullmatch(body['requestId']):
            raise ValueError('Invalid requestId')
        items = body['items']
        if not isinstance(items, list) or not 1 <= len(items) <= MAX_ITEMS:
            raise ValueError('A control request requires 1–32 items')
        seen = set()
        for item in items:
            fields = PLAN_FIELDS if operation == 'plans' else IDENTITY_FIELDS
            if not isinstance(item, dict) or set(item) != fields:
                raise ValueError('Invalid item fields')
            task, generation = item['clientTaskId'], item['attemptGeneration']
            if not isinstance(task, str) or not ID.fullmatch(task) or type(generation) is not int or not 1 <= generation <= 2147483647:
                raise ValueError('Invalid task identity')
            if (task, generation) in seen:
                raise ValueError('Duplicate task identity')
            seen.add((task, generation))
            if operation == 'plans':
                for field, maximum in (('sourceNamespace', 1024), ('sourceId', 1024), ('sourceVersion', 1024), ('name', 1024), ('mimeType', 128)):
                    text = item[field]
                    if not isinstance(text, str) or not text or len(text.encode('utf-8')) > maximum or any(ord(c) < 32 for c in text):
                        raise ValueError('Invalid ' + field)
                if not isinstance(item['sha256'], str) or not SHA.fullmatch(item['sha256']):
                    raise ValueError('Invalid SHA-256')
                if type(item['byteCount']) is not int or not 0 <= item['byteCount'] <= 9223372036854775807:
                    raise ValueError('Invalid byteCount')
        return body
    except (ValueError, TypeError, UnicodeError, OverflowError) as error:
        raise ProtocolError(400, 'InvalidRequest', str(error)) from error


def identity(item):
    return {key: item[key] for key in ('clientTaskId', 'attemptGeneration')}
