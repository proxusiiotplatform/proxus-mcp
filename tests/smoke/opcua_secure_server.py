"""Secured OPC UA test server (asyncua 1.1.8, Python 3.12).

Usage: opcua_secure_server.py <port> <work-dir> [strict]
- Generates its own certificate in <work-dir> on first run (SAN: urn:proxus:test:server, localhost, 127.0.0.1).
- Endpoints: Basic256Sha256 Sign/SignAndEncrypt, Aes128_Sha256_RsaOaep Sign/SignAndEncrypt and Aes256_Sha256_RsaPss
  SignAndEncrypt; logins anonymous or op/secret.
- strict: only clients whose certificate is in <work-dir>/trusted-clients are accepted (BadSecurityChecksFailed otherwise).
"""
import asyncio, datetime, ipaddress, sys
from pathlib import Path
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
from asyncua import Server, ua
from asyncua.crypto.validator import CertificateValidator, CertificateValidatorOptions
from asyncua.crypto.truststore import TrustStore
from asyncua.server.user_managers import UserManager
from asyncua.crypto.permission_rules import SimpleRoleRuleset
from asyncua.crypto.permission_rules import User, UserRole

URI = "urn:proxus:test:server"

def make_cert(work: Path):
    cert_path, key_path = work / "server.der", work / "server-key.pem"
    if cert_path.exists():
        return cert_path, key_path
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Proxus Test OPC UA Server")])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
            .not_valid_after(now + datetime.timedelta(days=365))
            .add_extension(x509.SubjectAlternativeName([x509.UniformResourceIdentifier(URI), x509.DNSName("localhost"),
                                                        x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]), False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), True)
            .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=True, key_encipherment=True,
                                         data_encipherment=True, key_agreement=False, key_cert_sign=False,
                                         crl_sign=False, encipher_only=False, decipher_only=False), True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH, ExtendedKeyUsageOID.CLIENT_AUTH]), False)
            .sign(key, hashes.SHA256()))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.DER))
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()))
    return cert_path, key_path

class Users(UserManager):
    def get_user(self, iserver, username=None, password=None, certificate=None):
        if username is None:
            return User(role=UserRole.User)
        if username == "op" and password == "secret":
            return User(role=UserRole.User, name="op")
        return None

async def main():
    port, work, strict = int(sys.argv[1]), Path(sys.argv[2]), len(sys.argv) > 3 and sys.argv[3] == "strict"
    work.mkdir(parents=True, exist_ok=True)
    cert_path, key_path = make_cert(work)
    s = Server(user_manager=Users()); await s.init()
    s.set_endpoint(f"opc.tcp://127.0.0.1:{port}/secure")
    await s.set_application_uri(URI)
    await s.load_certificate(str(cert_path)); await s.load_private_key(str(key_path))
    s.set_security_policy([ua.SecurityPolicyType.Basic256Sha256_SignAndEncrypt, ua.SecurityPolicyType.Basic256Sha256_Sign,
                           ua.SecurityPolicyType.Aes128Sha256RsaOaep_SignAndEncrypt, ua.SecurityPolicyType.Aes128Sha256RsaOaep_Sign,
                           ua.SecurityPolicyType.Aes256Sha256RsaPss_SignAndEncrypt], permission_ruleset=SimpleRoleRuleset())
    if strict:
        trusted = work / "trusted-clients"; trusted.mkdir(exist_ok=True)
        store = TrustStore([trusted], []); await store.load()
        s.set_certificate_validator(CertificateValidator(CertificateValidatorOptions.TRUSTED_VALIDATION | CertificateValidatorOptions.PEER_CLIENT, store))
    idx = await s.register_namespace("urn:proxus:demo")
    line = await s.nodes.objects.add_object(ua.NodeId("Line1", idx), "Line1")
    await line.add_variable(ua.NodeId("Line1.Temperature", idx), "Temperature", 72.5)
    async with s:
        while True: await asyncio.sleep(1)

asyncio.run(main())
