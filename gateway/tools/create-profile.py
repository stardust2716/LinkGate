"""Create private single-owner LinkGate pairing and server TLS material.

Requires Python cryptography. Never prints secrets or writes into a release tree.
"""
import argparse
import csv
import datetime as dt
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import subprocess


def secure_directory(path):
    if path.exists():
        raise ValueError('Output directory already exists; retain the existing identity')
    path.mkdir(parents=True, mode=0o700)
    if os.name == 'nt':
        row = list(csv.reader(subprocess.check_output(
            ['whoami', '/user', '/fo', 'csv', '/nh'], text=True).splitlines()))[0]
        sid = row[-1]
        if not re.fullmatch(r'S-1-\d+(?:-\d+)+', sid):
            raise ValueError('Cannot establish current user ownership')
        subprocess.run(['icacls', str(path), '/inheritance:r', '/grant:r',
                        '*' + sid + ':(OI)(CI)F', '*S-1-5-18:(OI)(CI)F',
                        '*S-1-5-32-544:(OI)(CI)F'], check=True, stdout=subprocess.DEVNULL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--server-port', type=int, default=17400)
    parser.add_argument('--stun-port', type=int, default=3478)
    parser.add_argument('--local-port', type=int, default=18480)
    parser.add_argument('--visitor-port', type=int, default=18481)
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,251}[a-zA-Z0-9])?', args.host):
        raise ValueError('Use a DNS name or IPv4 host without scheme or path')
    for port in (args.server_port, args.stun_port, args.local_port, args.visitor_port):
        if not 1024 <= port <= 65535:
            raise ValueError('Use unprivileged ports')
    output = args.output.resolve()
    if any(part.lower() in ('dist', '.git', 'bin', 'obj') for part in output.parts):
        raise ValueError('Pairing credentials cannot be written to a release/build tree')
    secure_directory(output)
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID

    now = dt.datetime.now(dt.timezone.utc)
    ca_key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    server_key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'LinkGate private CA')])
    ca = (x509.CertificateBuilder().subject_name(ca_name).issuer_name(ca_name)
          .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
          .not_valid_before(now-dt.timedelta(minutes=5)).not_valid_after(now+dt.timedelta(days=3650))
          .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
          .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, None, None), critical=True)
          .sign(ca_key, hashes.SHA256()))
    server_name = 'gateway.internal'
    cert = (x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, server_name)]))
            .issuer_name(ca_name).public_key(server_key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now-dt.timedelta(minutes=5))
            .not_valid_after(now+dt.timedelta(days=365))
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.SubjectAlternativeName([x509.DNSName(server_name)]), critical=False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .sign(ca_key, hashes.SHA256()))
    token = secrets.token_hex(32)
    profile = dict(formatVersion=1, namespace=secrets.token_hex(6), deviceName='My computer',
                   server=dict(host=args.host, port=args.server_port, stunPort=args.stun_port,
                               token=token, tlsServerName=server_name,
                               trustedCaPem=ca.public_bytes(serialization.Encoding.PEM).decode()),
                   services=[dict(id='demo', label='LinkGate 示例', localPort=args.local_port,
                                  visitorPort=args.visitor_port, secretKey=secrets.token_hex(32))])
    (output/'pairing.local.json').write_text(json.dumps(profile, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    (output/'ca.crt').write_bytes(ca.public_bytes(serialization.Encoding.PEM))
    for name, key in [('ca.key', ca_key), ('server.key', server_key)]:
        (output/name).write_bytes(key.private_bytes(serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    (output/'server.crt').write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    config = ('bindAddr = "0.0.0.0"\nproxyBindAddr = "127.0.0.1"\n'
              'bindPort = '+str(args.server_port)+'\n'
              'auth.method = "token"\nauth.token = '+json.dumps(token)+'\n'
              'transport.tls.force = true\ntransport.tls.certFile = "/etc/linkgate/server.crt"\n'
              'transport.tls.keyFile = "/etc/linkgate/server.key"\n'
              'detailedErrorsToClient = false\n'
              'log.to = "console"\nlog.level = "info"\nlog.disablePrintColor = true\n')
    (output/'frps.toml').write_text(config, encoding='utf-8')
    for path in output.iterdir():
        if os.name != 'nt': path.chmod(0o600)
    print(json.dumps(dict(created=True, pairingFile=str(output/'pairing.local.json'),
                          certificateExpiry=cert.not_valid_after_utc.isoformat(), secretsPrinted=False)))


if __name__ == '__main__':
    main()
