#!/usr/bin/env python3
"""UIFrame v2 local reference service: business API and private storage origins."""
import argparse
import json
import os
import secrets
import sqlite3
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from protocol import MAX_REQUEST_BYTES, MAX_RESPONSE_BYTES, ProtocolError, decode_request, encode
from store import Store


class BackupHTTPServer(ThreadingHTTPServer):
    # server_close must drain every handler before Store releases directory ownership.
    daemon_threads = False
    block_on_close = True


def handler_for(store, storage=False):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def setup(self):
            super().setup()
            self.connection.settimeout(30)

        def log_message(self, format, *args):
            # Request paths and authorization are intentionally not logged.
            pass

        def reply(self, status, body=None):
            data = b'' if body is None else encode(body)
            if len(data) > MAX_RESPONSE_BYTES:
                raise RuntimeError('Server response exceeds the v2 response budget')
            self.send_response(status)
            if body is not None:
                self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(data)))
            self.send_header('Connection', 'close')
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            self.close_connection = True
            self.wfile.write(data)

        def length(self, maximum):
            values = self.headers.get_all('Content-Length', [])
            if self.headers.get('Transfer-Encoding') or len(values) != 1 or not values[0].isdigit():
                raise ProtocolError(400, 'InvalidLength', 'One explicit Content-Length is required')
            length = int(values[0])
            if length > maximum:
                raise ProtocolError(413, 'BodyTooLarge', 'Request body exceeds the declared limit')
            return length

        def route(self, method):
            try:
                path = urlsplit(self.path)
                parts = path.path.strip('/').split('/')
                if storage:
                    if self.headers.get('Authorization'):
                        raise ProtocolError(403, 'BusinessCredentialForbidden', 'Business credentials must not be sent to storage')
                    if method != 'PUT' or len(parts) != 2 or parts[0] != 'objects' or path.query:
                        raise ProtocolError(404, 'NotFound', 'Storage route not found')
                    store.receive(parts[1], self.headers.get('X-Upload-Authorization', ''),
                                  self.length(512 * 1024 * 1024), self.rfile)
                    self.reply(204)
                    return
                account = store.authenticate(self.headers.get('Authorization', ''))
                if account is None:
                    raise ProtocolError(401, 'AuthenticationRequired', 'Valid business authentication is required')
                if method == 'POST' and len(parts) == 3 and parts[:2] == ['v2', 'backup'] and parts[2] in ('plans', 'status', 'cancellations') and not path.query:
                    length = self.length(MAX_REQUEST_BYTES)
                    if self.headers.get_content_type() != 'application/json':
                        raise ProtocolError(415, 'ContentType', 'Control requests require application/json')
                    data = self.rfile.read(length)
                    if len(data) != length:
                        raise ProtocolError(400, 'TruncatedBody', 'Control body is incomplete')
                    request = decode_request(data, parts[2])
                    self.reply(200, store.control(account, parts[2], request))
                elif method == 'GET' and parts == ['v2', 'backups']:
                    query = parse_qs(path.query, strict_parsing=True) if path.query else {}
                    if set(query) - {'after', 'limit'} or any(len(values) != 1 for values in query.values()):
                        raise ValueError('Invalid page query')
                    after, limit = int(query.get('after', ['0'])[0]), int(query.get('limit', ['32'])[0])
                    if after < 0 or not 1 <= limit <= 32:
                        raise ValueError('Invalid page bounds')
                    self.reply(200, store.list_backups(account, after, limit))
                elif method == 'GET' and len(parts) == 4 and parts[:2] == ['v2', 'backups'] and parts[3] == 'content' and not path.query:
                    row = store.content(account, parts[2])
                    with Path(row['path']).open('rb') as source:
                        if os.fstat(source.fileno()).st_size != row['size']:
                            raise OSError('Published file size is inconsistent')
                        self.send_response(200)
                        self.send_header('Content-Type', row['mime'])
                        self.send_header('Content-Length', str(row['size']))
                        self.send_header('Connection', 'close')
                        self.end_headers()
                        self.close_connection = True
                        while block := source.read(131072):
                            self.wfile.write(block)
                else:
                    raise ProtocolError(404, 'NotFound', 'Business route not found')
            except ProtocolError as error:
                self.reply(error.status, dict(error=dict(code=error.code, message=str(error))))
            except (ValueError, UnicodeError):
                self.reply(400, dict(error=dict(code='InvalidRequest', message='Invalid request parameters')))
            except (BrokenPipeError, ConnectionResetError):
                self.close_connection = True
            except (OSError, sqlite3.Error):
                self.reply(500, dict(error=dict(code='StorageFailure', message='Storage operation failed')))

        def do_POST(self): self.route('POST')
        def do_PUT(self): self.route('PUT')
        def do_GET(self): self.route('GET')
        def do_DELETE(self): self.route('DELETE')
    return Handler


def serve(root, credentials, host='127.0.0.1', port=8787, storage_port=8788,
          storage_origin=None, retry_failed_cleanup=False):
    store = Store(root, credentials)
    business = private = None
    storage_thread = None
    try:
        private = BackupHTTPServer((host, storage_port), handler_for(store, storage=True))
        business = BackupHTTPServer((host, port), handler_for(store))
        store.storage_origin = storage_origin or f'http://{host}:{private.server_port}'
        if retry_failed_cleanup:
            for failure in store.cleanup(retry_failed=True)['failures'] + store.maintenance(retry_failed=True)['failures']:
                print(json.dumps(failure), file=sys.stderr)
        store.start_worker(lambda failure: print(json.dumps(failure), file=sys.stderr, flush=True))
        storage_thread = threading.Thread(target=private.serve_forever, name='PrivateStorage')
        storage_thread.start()
        print(f'Backup service listening at http://{host}:{business.server_port}; protocol v2; storage {store.storage_origin}', flush=True)
        business.serve_forever()
    finally:
        if private is not None:
            if storage_thread is not None:
                private.shutdown()
                storage_thread.join()
            private.server_close()
        if business is not None:
            business.server_close()
        store.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['init', 'serve'])
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--host', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=8787)
    parser.add_argument('--storage-port', type=int, default=8788)
    parser.add_argument('--storage-origin', help='Explicit address reachable by the client, e.g. http://192.168.1.2:8788')
    parser.add_argument('--retry-failed-cleanup', action='store_true')
    args = parser.parse_args()
    config = args.root / 'credentials.json'
    if args.command == 'init':
        args.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd = os.open(config, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as output:
            json.dump([dict(account='local-user', token=secrets.token_urlsafe(32))], output, indent=2)
        print('Created credentials.json. Configure the local client with this account and token; do not commit it.')
        return
    try:
        serve(args.root, json.loads(config.read_text()), args.host, args.port, args.storage_port,
              args.storage_origin, args.retry_failed_cleanup)
    except KeyboardInterrupt:
        pass


if __name__ == '__main__':
    main()
