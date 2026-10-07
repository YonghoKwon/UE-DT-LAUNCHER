"""Independent signed GET producer for isolated loopback performance fixtures."""
import base64
import hashlib
import http.client
import json
import secrets
from urllib.parse import urlsplit
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils


class Device:
    def __init__(self, root, register):
        self.key = ec.generate_private_key(ec.SECP256R1())
        self.id = 'intake-' + secrets.token_hex(8)
        path = root / 'device-public.json'
        path.write_text(json.dumps({'schemaVersion': 1, 'keyId': self.id,
            'publicKeyPem': self.key.public_key().public_bytes(serialization.Encoding.PEM,
                serialization.PublicFormat.SubjectPublicKeyInfo).decode()}), encoding='utf-8')
        register(path)
        self.server_key = serialization.load_pem_public_key((root / 'public.pem').read_bytes())

    def verify(self, encoded, data):
        raw = base64.b64decode(encoded, validate=True)
        if len(raw) != 64:
            raise ValueError('Invalid signature size')
        self.server_key.verify(utils.encode_dss_signature(int.from_bytes(raw[:32], 'big'),
            int.from_bytes(raw[32:], 'big')), data, ec.ECDSA(hashes.SHA256()))


class Client:
    def __init__(self, origin, device):
        uri = urlsplit(origin)
        if uri.scheme != 'http' or uri.hostname != '127.0.0.1' or uri.path or uri.query or uri.fragment:
            raise ValueError('Only a private loopback fixture is supported')
        self.origin, self.device = origin, device
        self.connection = http.client.HTTPConnection(uri.hostname, uri.port, timeout=10)
        self.connection.request('GET', '/api/v1/auth/challenge?keyId=' + device.id)
        response = self.connection.getresponse()
        data = response.read(8193)
        if response.status != 200 or len(data) > 8192:
            self.close()
            raise ValueError('Challenge startup failed')
        self.challenge = json.loads(data)['challenge']
        self.sequence = 0

    def close(self):
        self.connection.close()

    def get(self, path, expected=None, full_size=None):
        nonce = base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip('=')
        target = self.origin + path
        ranged = expected is not None
        range_value = f'bytes=0-{len(expected)-1}' if ranged else None
        components = '("@method" "@target-uri" "x-ue-dt-challenge"' + (' "range"' if ranged else '') + ')'
        parameters = components + f';keyid="{self.device.id}";nonce="{nonce}";alg="ecdsa-p256-sha256";tag="ue-dt-request-v1"'
        message = f'"@method": GET\n"@target-uri": {target}\n"x-ue-dt-challenge": {self.challenge}\n'
        if ranged:
            message += f'"range": {range_value}\n'
        message += '"@signature-params": ' + parameters
        r, s = utils.decode_dss_signature(self.device.key.sign(message.encode(), ec.ECDSA(hashes.SHA256())))
        signature = base64.b64encode(r.to_bytes(32, 'big') + s.to_bytes(32, 'big')).decode()
        headers = {'X-UE-DT-Challenge': self.challenge, 'Signature-Input': 'sig1=' + parameters,
            'Signature': 'sig1=:' + signature + ':'}
        if ranged:
            headers['Range'] = range_value
        self.connection.request('GET', path, headers=headers)
        response = self.connection.getresponse()
        data = response.read(8 * 1024 * 1024 + 1)
        valid = response.status == (206 if ranged else 200) and len(data) <= 8 * 1024 * 1024
        if not valid:
            return response.status, False
        if ranged:
            return response.status, data == expected and response.getheader('Content-Range') == f'bytes 0-{len(expected)-1}/{full_size}'
        envelope = json.loads(data)
        payload = base64.b64decode(envelope['payload'], validate=True)
        self.device.verify(json.loads(envelope['signatureDocument'])['signature'], payload)
        binding = envelope['requestBinding']
        digest = hashlib.sha256(payload).hexdigest()
        if (binding['keyId'], binding['nonce'], binding['method'], binding['targetUri'], binding['payloadSha256']) != (self.device.id, nonce, 'GET', target, digest):
            raise ValueError('Catalog request binding mismatch')
        self.device.verify(json.loads(binding['signatureDocument'])['signature'], json.dumps(
            ['ue-dt-catalog-request-binding-v1', self.device.id, nonce, 'GET', target, digest], separators=(',', ':')).encode())
        sequence = json.loads(payload)['sequence']
        if not isinstance(sequence, int) or isinstance(sequence, bool) or sequence <= self.sequence:
            raise ValueError('Catalog sequence replay')
        self.sequence = sequence
        return response.status, True
