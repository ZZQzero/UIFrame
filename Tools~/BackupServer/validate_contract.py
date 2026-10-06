"""Validate OpenAPI and saved examples. Development dependencies only."""
import copy
import json
from pathlib import Path
import yaml
from jsonschema import Draft202012Validator
from openapi_spec_validator import validate
from protocol import decode_request, encode

ROOT = Path(__file__).resolve().parent
document = yaml.safe_load((ROOT.parents[1] / 'Docs/GalleryBackupProtocol.openapi.yaml').read_text())
validate(document)
count = 0
for fixture in json.loads((ROOT / 'fixtures/protocol-v2.json').read_text()):
    schema = dict(document, **{'$ref': '#/components/schemas/' + fixture['schema']})
    validator = Draft202012Validator(schema)
    validator.validate(fixture['value'])
    if 'operation' in fixture:
        decode_request(encode(fixture['value']), fixture['operation'])
    invalid = copy.deepcopy(fixture['value'])
    invalid['unexpected'] = True
    assert list(validator.iter_errors(invalid)), 'Unexpected fields were accepted'
    count += 1
print('OpenAPI 3.1 and %d positive/negative example pairs validated' % count)

