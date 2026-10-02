"""One TLS-verified GET. Retry and write reconciliation belong to PowerShell."""
import json
import ssl
import sys
from urllib.parse import urlsplit


def request(data):
    import httpx

    uri = urlsplit(data["uri"])
    if uri.scheme != "https" or uri.hostname not in {"github.com", "api.github.com"} or uri.username or uri.password:
        raise ValueError("unapproved fallback URL")
    headers = data.get("headers") or {}
    if uri.hostname != "api.github.com" and any(k.lower() == "authorization" for k in headers):
        raise ValueError("public asset request must be anonymous")
    timeout = httpx.Timeout(connect=data["connectTimeout"], read=data["readTimeout"], write=30, pool=10)
    def require_https(outgoing):
        if outgoing.url.scheme != "https" or outgoing.url.username or outgoing.url.password:
            raise ValueError("insecure or credential-bearing redirect")

    with httpx.Client(verify=ssl.create_default_context(), timeout=timeout, follow_redirects=True,
                      event_hooks={"request": [require_https]}) as client:
        with client.stream("GET", data["uri"], headers=headers) as response:
            content = ""
            if data.get("outFile") and response.status_code == 200:
                with open(data["outFile"], "wb") as output:
                    for chunk in response.iter_bytes():
                        output.write(chunk)
            else:
                response.read()
                content = response.text
            return dict(ok=True, status=response.status_code, headers=dict(response.headers), content=content)


def main():
    try:
        data = json.load(sys.stdin)
        result = request(data)
    except Exception as error:
        # Never emit exception messages, request URLs, response bodies or headers.
        category = "permanent-transport"
        try:
            import httpx
            detail = str(error).lower()
            if "certificate" in detail or "cert_verify" in detail:
                category = "certificate"
            elif isinstance(error, httpx.ConnectTimeout):
                category = "connect-timeout"
            elif isinstance(error, httpx.ReadTimeout):
                category = "read-timeout"
            elif isinstance(error, (httpx.RemoteProtocolError, httpx.ReadError)):
                category = "eof"
            elif isinstance(error, httpx.ConnectError):
                category = "connection-reset"
        except ImportError:
            pass
        result = dict(ok=False, category=category)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
