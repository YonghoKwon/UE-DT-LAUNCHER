#!/usr/bin/env python3
"""TEST ONLY: loopback HTTPS streaming proxy for native-GUI fixture verification.

Useful when a WSL1 nginx test instance stalls on large responses. Does not replace
the production nginx configuration or its separate auth_request/E2E tests. All
requests still pass through the distribution server's token/IP/grant checks.
Only generated test certificates/keys may be used. No request headers are logged.
"""
import argparse
import http.client
import http.server
import socket
import ssl


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        super().setup()
        self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def log_message(self, *_):
        pass

    def forward(self):
        if self.path != "/api/v1/catalog" and not self.path.startswith("/releases/"):
            self.send_error(404)
            return
        upstream = http.client.HTTPConnection("127.0.0.1", self.server.upstream_port, timeout=120)
        try:
            headers = {key: value for key, value in self.headers.items()
                       if key.lower() not in ("host", "connection", "x-distribution-client-ip", "x-forwarded-for")}
            headers["X-Distribution-Client-IP"] = self.client_address[0]
            upstream.request(self.command, self.path, headers=headers)
            response = upstream.getresponse()
            self.send_response(response.status)
            for key, value in response.getheaders():
                if key.lower() not in ("connection", "transfer-encoding", "server", "date"):
                    self.send_header(key, value)
            self.close_connection = response.getheader("Content-Length") is None
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            if self.command != "HEAD":
                while chunk := response.read(65536):
                    self.wfile.write(chunk)
        except (OSError, http.client.HTTPException):
            self.close_connection = True
        finally:
            upstream.close()

    do_GET = forward
    do_HEAD = forward


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--upstream-port", type=int, required=True)
    parser.add_argument("--cert", required=True)
    parser.add_argument("--key", required=True)
    args = parser.parse_args()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    server.upstream_port = args.upstream_port
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(args.cert, args.key)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    print("Isolated HTTPS fixture proxy listening on loopback only.", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
