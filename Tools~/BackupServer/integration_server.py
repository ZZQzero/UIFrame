"""Ephemeral localhost fixture for the Unity MediaIntegration test category."""
import tempfile
from http.server import ThreadingHTTPServer
from server import Store, handler_for

with tempfile.TemporaryDirectory(prefix='uiframe-backup-integration-') as root:
    store = Store(root, [{'account': 'integration-user', 'token': 'uiframe-integration-test-token'}])
    server = ThreadingHTTPServer(('127.0.0.1', 18787), handler_for(store))
    print('Unity integration fixture ready on 127.0.0.1:18787', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        store.close()
