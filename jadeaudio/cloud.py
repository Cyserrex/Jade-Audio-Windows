"""Client for FiiO's online EQ preset library, and the account behind it.

Two services sit behind fiiocontrol.fiio.com:

`/ucenter-api` holds the presets. Every request is wrapped in a hybrid
envelope — a random AES-256 key encrypts the JSON body, and that key travels
RSA-encrypted beside it::

    {"cipherSign": base64(RSA_PKCS1v15(aes_key_hex)),
     "cipherText": base64(AES_256_ECB(body))}

Responses come back encrypted under the same key. Notably it carries no
Authorization header at all: the caller is identified by a ``userId`` inside
the encrypted body, so browsing and searching work without an account.

`/usersystem-api` is the account system: an OAuth2 client-credentials token
lets you fetch a picture CAPTCHA, and a password grant carrying that CAPTCHA
returns the user's token.

This module never writes a password anywhere. `Auth` holds tokens in memory
for the life of the process and nothing reaches disk.
"""

from __future__ import annotations

import base64
import json
import os
import uuid
from dataclasses import dataclass, field

import requests
from cryptography.hazmat.primitives import padding, serialization
from cryptography.hazmat.primitives.asymmetric import padding as asym_padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

BASE_URL = "https://fiiocontrol.fiio.com"
UCENTER = "/ucenter-api"
USERSYSTEM = "/usersystem-api"

REGISTER_URL = "https://usersystem.fiio.com/sso/register"

#: Public OAuth client of the web app, as shipped in its JavaScript bundle.
CLIENT_ID = "ELP6WFJ6Q0VXV6J3"
CLIENT_SECRET = "63ZEP47VJEFCBPMXQJD3X1ZHLMM44AAK"

#: Wraps the AES key for /ucenter-api.
PRESET_KEY_PEM = b"""-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0mPGrgZPDCPDd9m129qy
+TV5HeLWDTJbNK5wlUAemqH3NyrOh3HUo+LbTbMf6J45AFDjNRCDiZZc3Y+uCici
9fHn0dixbtuHOdJer0U/4xHloOgYmsTwhAh56njWQAyaqoi0R3nG0bpCgsi5omlS
RqP5KyaybuKjPyZvGVn0IKnVVQx3AI1+p/5lWARv23nPNS9ehfKts5oeFdOAKgT2
mvV80TzJHbc2mKU8XoBr0VgX4Ohgq/A+Ddv0Wz0bNAQJgzrFAN1lIg2NqktcGdzD
/HHPIeGCYG2gB7ZADsGX6vGpSoeUj3anr25nojQVbZgEBeE/6nJZ793Qhq8vudep
XQIDAQAB
-----END PUBLIC KEY-----"""

#: Wraps the AES key for the user_info call. A different key to the above.
USER_KEY_PEM = b"""-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA6rHbVXvKG4oRara64ulw
uJYBVMJx2nilsn/ktOhA08tIKe9NPMIf+BYZrZ6g4mRyZhTtFMza2n5kDJfEHp6g
AOyuuKtwsEbYSyRQNTySpHPi2YDNmD8mrw060Qcifoy2aupqZncsDMNk9HRkWhqI
eYk2V3tk7eBoZWqDR13w4nueEne8gThw+IdGMn5eV6Sd7v/4wBiYplTo7rG1VznC
IzncS2Dv8JEvmpgzFEgvScSe3BXdCMIJ9j8n/GeLb/J3R3I8zUqpA9xZDVwnfw1l
L62sYv2RhZtnLyndmZ4uJuQrdrU8dZKXGOo370uht8p8AxWAlZfvGAlTkQGzqmFD
OQIDAQAB
-----END PUBLIC KEY-----"""

#: Device ids the service uses, as found in the official app.
DEVICE_TYPES = {
    "JadeAudio JA11": 109,
    "FIIO KA17": 108,
    "FIIO KA15": 110,
    "FIIO FP3": 111,
    "FIIO FX17": 112,
    "FIIO QX13": 113,
    "SNOWSKY Melody": 114,
    "SNOWSKY TINY A": 115,
    "SNOWSKY TINY B": 117,
    "JadeAudio JIEZI": 118,
    "FIIO LS-TC2": 119,
    "FIIO FG3": 120,
    "FIIO QX11": 121,
    "OAK NANO": 122,
    "FIIO BTR13": 31,
    "FIIO BTR15": 24,
    "FIIO BTR17": 35,
    "RETRO NANO": 38,
    "FIIO K19": 29,
    "FIIO BT11": 30,
}

_COMMON_HEADERS = {"Origin": BASE_URL, "Referer": BASE_URL + "/"}


class CloudError(Exception):
    pass


class AuthError(CloudError):
    pass


@dataclass
class CloudPreset:
    """One preset from the library, normalised to our band shape."""

    name: str
    author: str = ""
    description: str = ""
    device_type: int = -1
    global_gain: float = 0.0
    share_code: str = ""
    downloads: int = 0
    tags: tuple = ()
    bands: list = field(default_factory=list)

    @classmethod
    def from_record(cls, record: dict) -> "CloudPreset":
        raw = record.get("eqParamsJson") or record.get("peqList") or []
        bands = [
            {
                "index": b.get("position", i),
                "frequency": int(b.get("frequency", 1000)),
                "gain": float(b.get("gain", 0.0)),
                "q": float(b.get("qValue", 1.0)),
                "filter_type": int(b.get("filterType", 0)),
            }
            for i, b in enumerate(raw)
        ]
        return cls(
            name=record.get("styleName") or record.get("name") or "Untitled",
            author=record.get("peqUserName") or record.get("userName") or "",
            description=record.get("description") or "",
            device_type=record.get("deviceType", -1),
            global_gain=float(record.get("masterGain") or 0.0),
            share_code=record.get("shareCode") or "",
            downloads=int(record.get("downloadSum") or 0),
            tags=tuple(record.get("tag") or ()),
            bands=bands,
        )


# --- the hybrid envelope ----------------------------------------------------


def _new_key() -> bytes:
    """A 32-character hex string, used verbatim as 32 AES key bytes."""
    return os.urandom(16).hex().encode("ascii")


def _aes_encrypt(key: bytes, plaintext: str) -> str:
    padder = padding.PKCS7(128).padder()
    data = padder.update(plaintext.encode("utf-8")) + padder.finalize()
    enc = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    return base64.b64encode(enc.update(data) + enc.finalize()).decode("ascii")


def _aes_decrypt(key: bytes, ciphertext: str) -> str:
    dec = Cipher(algorithms.AES(key), modes.ECB()).decryptor()
    raw = dec.update(base64.b64decode(ciphertext)) + dec.finalize()
    unpadder = padding.PKCS7(128).unpadder()
    return (unpadder.update(raw) + unpadder.finalize()).decode("utf-8")


def _rsa_wrap(pem: bytes, key: bytes) -> str:
    public_key = serialization.load_pem_public_key(pem)
    return base64.b64encode(public_key.encrypt(key, asym_padding.PKCS1v15())).decode("ascii")


class Auth:
    """FiiO account session.

    Tokens live in memory only, for the life of the process. The password is
    handed straight to FiiO's token endpoint and is never stored, logged or
    written to disk.
    """

    def __init__(self, session: requests.Session | None = None, timeout: float = 20.0):
        self._session = session or requests.Session()
        self.timeout = timeout
        self._app_token: str | None = None
        self.access_token: str | None = None
        self.user_id: str | None = None
        self.user_name: str = ""

    @property
    def logged_in(self) -> bool:
        return bool(self.access_token and self.user_id)

    def _post_form(self, path: str, data: dict, token: str | None = None) -> dict:
        headers = dict(_COMMON_HEADERS)
        if token:
            headers["Authorization"] = f"Bearer {token}"
        try:
            response = self._session.post(
                BASE_URL + USERSYSTEM + path, data=data, headers=headers, timeout=self.timeout
            )
        except requests.RequestException as exc:
            raise AuthError(f"Could not reach the account server: {exc}") from exc
        try:
            payload = response.json()
        except ValueError:
            raise AuthError(f"Unexpected reply from {path} (HTTP {response.status_code})")
        if response.status_code != 200:
            raise AuthError(payload.get("msg") or payload.get("error_description") or "Login failed")
        return payload

    def app_token(self) -> str:
        """The client-credentials token, needed before a CAPTCHA can be asked for."""
        if self._app_token:
            return self._app_token
        payload = self._post_form(
            "/oauth/token",
            {
                "client_id": CLIENT_ID,
                "client_secret": CLIENT_SECRET,
                "grant_type": "client_credentials",
            },
        )
        self._app_token = payload.get("access_token")
        if not self._app_token:
            raise AuthError("The server did not return an application token.")
        return self._app_token

    def captcha(self) -> tuple[bytes, str]:
        """Fetch a picture CAPTCHA. Returns (image bytes, captcha token).

        The image is for a person to read. Show it; do not try to solve it.
        """
        headers = {**_COMMON_HEADERS, "Authorization": f"Bearer {self.app_token()}"}
        try:
            response = self._session.post(
                BASE_URL + USERSYSTEM + "/api/portal/captcha/pic",
                json={},
                headers=headers,
                timeout=self.timeout,
            )
            payload = response.json()
        except (requests.RequestException, ValueError) as exc:
            raise AuthError(f"Could not fetch a CAPTCHA: {exc}") from exc

        result = payload.get("result") or {}
        image, token = result.get("basePic"), result.get("token")
        if not image or not token:
            raise AuthError(payload.get("msg") or "The server did not return a CAPTCHA.")
        return base64.b64decode(image.split(",", 1)[-1]), token

    def login(self, username: str, password: str, captcha_text: str, captcha_token: str) -> None:
        """Exchange the user's own credentials for a token.

        `password` is forwarded to FiiO and then dropped; nothing here keeps it.
        """
        if not username or not password or not captcha_text:
            raise AuthError("Username, password and the CAPTCHA are all required.")
        payload = self._post_form(
            "/oauth/token",
            {
                "client_id": CLIENT_ID,
                "client_secret": CLIENT_SECRET,
                "grant_type": "password",
                "username": username,
                "password": password,
                "picCaptcha": captcha_text,
                "captchaToken": captcha_token,
            },
            token=self.app_token(),
        )
        self.access_token = payload.get("access_token")
        if not self.access_token:
            raise AuthError(payload.get("msg") or "Login failed.")
        self.fetch_user_info()

    def fetch_user_info(self) -> dict:
        """Read the account profile, which is where the userId comes from."""
        key = _new_key()
        headers = {**_COMMON_HEADERS, "Authorization": f"Bearer {self.access_token}"}
        try:
            response = self._session.get(
                BASE_URL + USERSYSTEM + "/api/portal/user/user_info",
                params={"cipherSign": _rsa_wrap(USER_KEY_PEM, key)},
                headers=headers,
                timeout=self.timeout,
            )
        except requests.RequestException as exc:
            raise AuthError(f"Could not read the account profile: {exc}") from exc

        body = response.text.strip()
        try:
            parsed = json.loads(body)
        except ValueError:
            parsed = body
        # The profile arrives as a bare AES blob, sometimes wrapped in a envelope.
        blob = parsed if isinstance(parsed, str) else (parsed.get("data") or parsed.get("result"))
        if not isinstance(blob, str):
            raise AuthError("The account profile came back in a shape we do not understand.")
        info = json.loads(_aes_decrypt(key, blob))
        self.user_id = info.get("userId")
        self.user_name = info.get("userName") or ""
        if not self.user_id:
            raise AuthError("The account profile carried no user id.")
        return info

    def logout(self) -> None:
        self.access_token = None
        self.user_id = None
        self.user_name = ""


class Cloud:
    """The preset library. One instance per session is plenty."""

    def __init__(self, timeout: float = 20.0):
        self.timeout = timeout
        self._session = requests.Session()
        self.auth = Auth(self._session, timeout)

    # -- transport -----------------------------------------------------------

    def _post(self, path: str, payload: dict, accept: str | None = None):
        key = _new_key()
        body = json.dumps(
            {
                "cipherSign": _rsa_wrap(PRESET_KEY_PEM, key),
                "cipherText": _aes_encrypt(key, json.dumps(payload)),
            }
        )
        headers = {
            **_COMMON_HEADERS,
            "Content-Type": "application/json",
            "X-Request-ID": str(uuid.uuid4()),
        }
        if accept:
            headers["Accept"] = accept
        try:
            response = self._session.post(
                BASE_URL + UCENTER + path, data=body, headers=headers, timeout=self.timeout
            )
        except requests.RequestException as exc:
            raise CloudError(f"Could not reach the preset server: {exc}") from exc

        try:
            result = response.json()
        except ValueError:
            raise CloudError(f"Unexpected reply from {path} (HTTP {response.status_code})")

        if result.get("code") != 200:
            raise CloudError(result.get("msg") or result.get("detail") or f"{path} failed")
        data = result.get("data")
        if isinstance(data, str):
            data = json.loads(_aes_decrypt(key, data))
        return data

    @staticmethod
    def _records(data) -> list:
        if isinstance(data, list):
            return data
        if isinstance(data, dict):
            return data.get("records") or []
        return []

    # -- browsing (no account needed) ---------------------------------------

    def official_presets(
        self, device_type: int | None = None, page: int = 1, page_size: int = 20
    ) -> tuple[list[CloudPreset], int]:
        """FiiO's own presets. Returns (presets, total available)."""
        payload = {"pageNumber": page, "pageSize": page_size, "totalRows": -1}
        if device_type is not None and device_type >= 0:
            payload["deviceType"] = device_type
        data = self._post("/get-peq-official-list", payload) or {}
        return [CloudPreset.from_record(r) for r in self._records(data)], int(
            data.get("totalRows") or 0
        )

    def community_presets(
        self, device_type: int | None = None, page: int = 1, page_size: int = 20
    ) -> tuple[list[CloudPreset], int]:
        """Presets shared by other owners - the web app's "Handpick" list."""
        payload = {"pageNumber": page, "pageSize": page_size, "totalRow": -1}
        if device_type is not None and device_type >= 0:
            payload["deviceType"] = [device_type]
        data = self._post(
            "/get-share-peq", payload, accept="application/vnd.fiio.v1+json"
        ) or {}
        return [CloudPreset.from_record(r) for r in self._records(data)], int(
            data.get("totalRow") or 0
        )

    def search(self, keyword: str, device_type: int | None = None) -> list[CloudPreset]:
        """Search the shared presets by name or description."""
        payload = {"searchValue": keyword}
        if device_type is not None and device_type >= 0:
            payload["deviceType"] = device_type
        data = self._post("/search-peq", payload, accept="application/vnd.fiio.v1+json")
        return [CloudPreset.from_record(r) for r in self._records(data)]

    def by_share_code(self, share_code: str) -> CloudPreset:
        """Fetch a single preset by its share code."""
        code = share_code.strip()
        if not code:
            raise CloudError("Enter a share code.")
        data = self._post(
            "/get-share-peq", {"shareCode": code}, accept="application/vnd.fiio.v1+json"
        )
        records = self._records(data)
        if not records:
            raise CloudError("No preset found for that share code.")
        return CloudPreset.from_record(records[0])

    # -- the account's own presets ------------------------------------------

    def personal_presets(self, device_type: int | None = None) -> list[CloudPreset]:
        """The signed-in account's saved presets."""
        if not self.auth.logged_in:
            raise AuthError("Sign in to see your own presets.")
        payload = {"userId": self.auth.user_id}
        if device_type is not None and device_type >= 0:
            payload["deviceType"] = device_type
        data = self._post("/get-peq", payload)
        return [CloudPreset.from_record(r) for r in self._records(data)]


def device_type_for(product_name: str) -> int:
    return DEVICE_TYPES.get(product_name, -1)
