"""Real FRP 0.71.1 loopback regression; never accesses a production service.

Example (PowerShell):
  python gateway/tests/frp_socket_integration.py --frp-dir downloads/linkgate/frp_0.71.1_windows_amd64

Creates private temporary credentials, a local STUN responder, HTTP/WebSocket
fixture, and only its own frps/frpc child processes. Prints no raw process logs,
addresses, pairing material or private configuration. This proves protocol
composition on loopback, not mobile/WAN NAT traversal.
"""
import argparse
import base64
import hashlib
import http.server
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import socketserver
import struct
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

_stage = 'preflight'
_report_path = None


def checkpoint(name):
    global _stage
    _stage = name


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def quote(text):
    return json.dumps(str(text).replace('\\', '/'))


class Fixture(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    requests_seen = 0

    def do_GET(self):
        Fixture.requests_seen += 1
        if self.path == '/ws':
            key = self.headers.get('Sec-WebSocket-Key', '')
            answer = base64.b64encode(hashlib.sha1((key +
                '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
            self.send_response(101)
            self.send_header('Upgrade', 'websocket')
            self.send_header('Connection', 'Upgrade')
            self.send_header('Sec-WebSocket-Accept', answer)
            self.end_headers()
            header = self.rfile.read(2)
            if len(header) != 2:
                return
            length = header[1] & 0x7f
            if length > 125 or not header[1] & 0x80:
                return
            mask = self.rfile.read(4)
            body = self.rfile.read(length)
            plain = bytes(v ^ mask[i % 4] for i, v in enumerate(body))
            self.wfile.write(bytes([0x81, len(plain)]) + plain)
            self.wfile.flush()
            self.close_connection = True
            return
        body = (b'linkgate-loopback-' * 4096) if self.path == '/stream' else b'linkgate-ok'
        self.send_response(200)
        self.send_header('Content-Type', 'application/octet-stream')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        for offset in range(0, len(body), 4096):
            self.wfile.write(body[offset:offset+4096])
            self.wfile.flush()

    def log_message(self, *args):
        pass


class HttpServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


class Stun:
    def __init__(self):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(('127.0.0.1', 0))
        self.socket.settimeout(.2)
        self.port = self.socket.getsockname()[1]
        self.closed = threading.Event()
        self.replies = 0
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while not self.closed.is_set():
            try:
                packet, sender = self.socket.recvfrom(4096)
                if len(packet) < 20 or packet[:2] != b'\x00\x01' or packet[4:8] != b'\x21\x12\xa4\x42':
                    continue
                address = bytes(a ^ b for a, b in zip(socket.inet_aton(sender[0]), b'\x21\x12\xa4\x42'))
                attr = struct.pack('!HHBBH', 0x20, 8, 0, 1, sender[1] ^ 0x2112) + address
                reply = struct.pack('!HH', 0x101, len(attr)) + packet[4:20] + attr
                self.socket.sendto(reply, sender)
                self.replies += 1
            except socket.timeout:
                pass
            except OSError:
                return

    def close(self):
        self.closed.set()
        self.socket.close()
        self.thread.join(1)


class Child:
    def __init__(self, executable, config, env):
        self.lines = []
        self.lock = threading.Lock()
        self.p = subprocess.Popen([str(executable), '-c', str(config)],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding='utf-8', errors='replace', env=env,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def read(self):
        for line in self.p.stdout:
            with self.lock:
                self.lines.append(line.strip()[:1200])
                self.lines = self.lines[-300:]

    def has(self, text):
        with self.lock:
            return any(text in line for line in self.lines)

    def wait_text(self, text, seconds=12):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if self.has(text):
                return True
            if self.p.poll() is not None:
                self.reader.join(1)
                return self.has(text)
            time.sleep(.1)
        return False

    def metrics(self):
        with self.lock:
            return [json.loads(line[len('LINKGATE_METRIC '):]) for line in self.lines if line.startswith('LINKGATE_METRIC ')]

    def close(self):
        if self.p.poll() is None:
            self.p.terminate()
            try:
                self.p.wait(5)
            except subprocess.TimeoutExpired:
                self.p.kill()
                self.p.wait(5)
        self.reader.join(1)


def get(port, path='/'):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open('http://127.0.0.1:{}{}'.format(port, path), timeout=10) as response:
        return response.read()


def websocket(port):
    data = b'websocket-through-frp'
    with socket.create_connection(('127.0.0.1', port), timeout=10) as conn:
        conn.settimeout(10)
        key = base64.b64encode(secrets.token_bytes(16)).decode()
        request = ('GET /ws HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\n'
                   'Connection: Upgrade\r\nSec-WebSocket-Version: 13\r\n'
                   'Sec-WebSocket-Key: {}\r\n\r\n').format(key)
        conn.sendall(request.encode())
        response = b''
        while b'\r\n\r\n' not in response:
            response += conn.recv(1)
        assert response.startswith(b'HTTP/1.1 101 ')
        mask = secrets.token_bytes(4)
        conn.sendall(bytes([0x81, 0x80 | len(data)]) + mask +
                     bytes(value ^ mask[i % 4] for i, value in enumerate(data)))
        head = conn.recv(2)
        assert head == bytes([0x81, len(data)])
        body = b''
        while len(body) < len(data):
            body += conn.recv(len(data) - len(body))
        assert body == data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--frp-dir', type=Path, required=True)
    parser.add_argument('--openssl', default=shutil.which('openssl'))
    parser.add_argument('--output', type=Path)
    parser.add_argument('--frpc', type=Path, help='Instrumented client; server still comes from --frp-dir')
    parser.add_argument('--require-metrics', action='store_true')
    args = parser.parse_args()
    global _report_path
    _report_path = args.output
    if _report_path:
        _report_path.parent.mkdir(parents=True, exist_ok=True)
        _report_path.write_text(json.dumps(dict(passed=False, status='running', stage=_stage))+'\n', encoding='utf-8')
    suffix = '.exe' if os.name == 'nt' else ''
    frps, frpc = (args.frp_dir.resolve() / (name + suffix) for name in ('frps', 'frpc'))
    if args.frpc: frpc=args.frpc.resolve()
    assert args.openssl and frps.is_file() and frpc.is_file()
    checkpoint('frps-version')
    assert subprocess.check_output([str(frps), '--version'], text=True).strip() == '0.71.1'
    checkpoint('frpc-version')
    assert subprocess.check_output([str(frpc), '--version'], text=True).strip() == '0.71.1'
    children = []
    http = HttpServer(('127.0.0.1', 0), Fixture)
    thread = threading.Thread(target=http.serve_forever, daemon=True)
    thread.start()
    stun = Stun()
    env = {k: v for k, v in os.environ.items() if k.lower() not in ('http_proxy','https_proxy','all_proxy')}
    result = dict(frpVersion='0.71.1', phoneUiOperated=False, productionServicesUsed=False)
    try:
        with tempfile.TemporaryDirectory(prefix='linkgate-frp-socket-') as temp:
            temp = Path(temp)
            # Test-only self-signed trust anchor; production uses a private CA.
            for label in ('server', 'wrong'):
                checkpoint('temporary-certificate-generation')
                output = subprocess.run([args.openssl, 'req', '-x509', '-newkey', 'rsa:2048',
                    '-nodes', '-days', '1', '-subj', '/CN=gateway.internal',
                    '-addext', 'subjectAltName=DNS:gateway.internal',
                    '-keyout', str(temp / (label + '.key')), '-out', str(temp / (label + '.crt'))],
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                assert output.returncode == 0
            server_port, stcp_port, fallback_port, direct_port, bad_key_port = [free_port() for _ in range(5)]
            assert len({server_port,stcp_port,fallback_port,direct_port,bad_key_port,http.server_port}) == 6
            token, secret = secrets.token_hex(32), secrets.token_hex(32)
            server = '\n'.join([
                'bindAddr = "127.0.0.1"', 'bindPort = {}'.format(server_port),
                'proxyBindAddr = "127.0.0.1"', 'transport.tls.force = true',
                'transport.tls.certFile = ' + quote(temp / 'server.crt'),
                'transport.tls.keyFile = ' + quote(temp / 'server.key'),
                'auth.method = "token"', 'auth.token = ' + quote(token),
                'log.to = "console"', 'log.level = "debug"', 'log.disablePrintColor = true'])
            common = '\n'.join([
                'serverAddr = "127.0.0.1"', 'serverPort = {}'.format(server_port),
                'auth.method = "token"', 'auth.token = ' + quote(token),
                'transport.tls.enable = true', 'transport.tls.serverName = "gateway.internal"',
                'transport.tls.trustedCaFile = ' + quote(temp / 'server.crt'),
                'natHoleStunServer = "127.0.0.1:{}"'.format(stun.port),
                'loginFailExit = true', 'log.to = "console"', 'log.level = "debug"',
                'log.disablePrintColor = true']) + '\n'
            def config(name, value):
                path = temp / (name + '.toml')
                path.write_text(value, encoding='utf-8')
                return path
            def launch(executable, name, text):
                child = Child(executable, config(name, text), env)
                children.append(child)
                return child
            def proxy(kind, name):
                return ('\n[[proxies]]\nname = '+quote(name)+'\ntype = '+quote(kind)+
                    '\nsecretKey = '+quote(secret)+'\nlocalIP = "127.0.0.1"\nlocalPort = '+str(http.server_port)+
                    '\ntransport.useEncryption = true\ntransport.useCompression = false\n')
            def visitor(kind, name, target, port, key=secret, extra=''):
                return ('\n[[visitors]]\nname = '+quote(name)+'\ntype = '+quote(kind)+
                    '\nserverName = '+quote(target)+'\nsecretKey = '+quote(key)+
                    '\nbindAddr = "127.0.0.1"\nbindPort = '+str(port)+
                    '\ntransport.useEncryption = true\ntransport.useCompression = false\n'+extra)
            host = launch(frps, 'server', server)
            checkpoint('server-startup')
            assert host.wait_text('frps started successfully')
            publisher = launch(frpc, 'publisher', common+proxy('stcp','test-stcp')+proxy('xtcp','test-xtcp'))
            checkpoint('publisher-registration')
            assert publisher.wait_text('[test-stcp] start proxy success')
            assert publisher.wait_text('[test-xtcp] start proxy success')
            visit = common + visitor('stcp','relay','test-stcp',stcp_port)
            visit += visitor('stcp','fallback','test-stcp',-1)
            # Deterministic no-target XTCP path: no NAT manipulation or host firewall changes.
            visit += visitor('xtcp','fallback-direct','intentionally-absent',fallback_port,
                extra='protocol = "quic"\nfallbackTo = "fallback"\nfallbackTimeoutMs = 300\n')
            visit += visitor('xtcp','direct','test-xtcp',direct_port,
                extra='protocol = "quic"\nfallbackTo = "fallback"\nfallbackTimeoutMs = 1500\nkeepTunnelOpen = true\nminRetryInterval = 30\nmaxRetriesAnHour = 120\n')
            visit += visitor('stcp','bad-service-key','test-stcp',bad_key_port,key='b'*64)
            client = launch(frpc, 'visitor', visit)
            checkpoint('visitor-registration')
            assert client.wait_text('visitor added:')
            checkpoint('stcp-http')
            assert get(stcp_port) == b'linkgate-ok'
            result['stcpHttpPassed'] = True
            checkpoint('stcp-streaming')
            payload = get(stcp_port, '/stream')
            assert payload == b'linkgate-loopback-' * 4096
            result['stcpStreamingPassed'] = True
            checkpoint('stcp-websocket')
            websocket(stcp_port)
            result['stcpWebSocketPassed'] = True
            checkpoint('xtcp-fallback')
            assert get(fallback_port) == b'linkgate-ok'
            assert client.wait_text('try to transfer connection to visitor: fallback')
            websocket(fallback_port)
            result['xtcpUnavailableFallbackHttpAndWebSocketPassed'] = True
            if args.require_metrics:
                checkpoint('actual-relay-byte-accounting')
                deadline=time.monotonic()+5
                while time.monotonic()<deadline:
                    visitor_done={r['id']:r for r in client.metrics() if r['phase']=='closed'}
                    publisher_done={r['id']:r for r in publisher.metrics() if r['phase']=='closed'}
                    if len(visitor_done)>=5 and len(publisher_done)>=5: break
                    time.sleep(.1)
                assert len(visitor_done)==5 and len(publisher_done)==5
                assert all(r['path']=='relay' for r in list(visitor_done.values())+list(publisher_done.values()))
                visitor_tx=sum(r['tx'] for r in visitor_done.values());visitor_rx=sum(r['rx'] for r in visitor_done.values())
                publisher_tx=sum(r['tx'] for r in publisher_done.values());publisher_rx=sum(r['rx'] for r in publisher_done.values())
                assert visitor_tx==publisher_rx and visitor_rx==publisher_tx and visitor_rx>len(payload)
                fallback=[r for r in visitor_done.values() if r['reason']=='direct_tunnel_unavailable']
                assert len(fallback)==2 and all(r['setupMs']>=250 for r in fallback)
                result['instrumentedRelayBytesMatchedBothEnds']=True
                result['fallbackSetupAndReasonVerified']=True
                result['visitorBusinessTxBytes']=visitor_tx;result['visitorBusinessRxBytes']=visitor_rx
            bad = launch(frpc, 'bad-token', common.replace(token, 'f'*64))
            checkpoint('bad-server-token')
            assert bad.wait_text('token in login doesn', 8)
            result['badServerTokenRejected'] = True
            wrong_ca = launch(frpc, 'bad-ca', common.replace(quote(temp/'server.crt'),quote(temp/'wrong.crt')))
            checkpoint('bad-server-ca')
            assert wrong_ca.p.wait(timeout=8) != 0
            wrong_ca.reader.join(1)
            assert not wrong_ca.has('login to server success')
            assert get(stcp_port) == b'linkgate-ok'
            result['wrongCaRejected'] = True
            before = Fixture.requests_seen
            checkpoint('bad-service-key')
            try:
                get(bad_key_port)
                raise AssertionError('wrong service key was accepted')
            except (OSError, urllib.error.URLError):
                pass
            assert Fixture.requests_seen == before
            result['wrongServiceKeyRejected'] = True
            # Make traffic and only mark direct when an actual QUIC session exists.
            checkpoint('loopback-direct-observation')
            get(direct_port)
            result['localStunResponses'] = stun.replies
            result['loopbackNatHoleObserved'] = client.has('establishing nat hole connection successful')
            result['loopbackQuicDirectObserved'] = any(r['path']=='direct' and r['tx']+r['rx']>0 for r in client.metrics())
            result['wanDirectnessVerified'] = False
    finally:
        for child in reversed(children):
            child.close()
        stun.close()
        http.shutdown()
        http.server_close()
        thread.join(2)
    result['passed'] = True
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Raw assertion/process output may contain private configuration.
        failed = dict(passed=False, stage=_stage, errorType=type(error).__name__)
        if isinstance(error, OSError):
            failed['winerror'] = getattr(error, 'winerror', None)
        if _report_path:
            _report_path.write_text(json.dumps(failed, indent=2)+'\n', encoding='utf-8')
        print(json.dumps(failed))
        raise SystemExit(1)
