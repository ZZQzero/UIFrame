"""Ephemeral localhost fixture for the Unity MediaIntegration test category."""
import tempfile
from server import serve

with tempfile.TemporaryDirectory(prefix='uiframe-backup-integration-') as root:
    try:
        serve(root, [{'account': 'integration-user', 'token': 'uiframe-integration-test-token'}],
              port=18787, storage_port=18788)
    except KeyboardInterrupt:
        pass
