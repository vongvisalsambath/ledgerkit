# Serves a published site with the headers from vercel.json, so browser tests run under the production policy.
# Usage: python3 serve.py <vercel.json> <folder> <port>
import http.server, json, re, sys

cfg = json.load(open(sys.argv[1]))
root, port = sys.argv[2], int(sys.argv[3])
rules = [(re.compile("^" + re.escape(r["source"]).replace(re.escape("(.*)"), ".*") + "$"), r["headers"]) for r in cfg.get("headers", [])]


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, ".wasm": "application/wasm", ".js": "text/javascript", ".json": "application/json", ".dat": "application/octet-stream"}

    def __init__(self, *a, **k):
        super().__init__(*a, directory=root, **k)

    def end_headers(self):
        path = self.path.split("?")[0]
        headers = {}
        for rx, hs in rules:  # a later rule overrides an earlier one, as on Vercel
            if rx.match(path):
                headers.update({h["key"]: h["value"] for h in hs})
        for k, v in headers.items():
            self.send_header(k, v)
        super().end_headers()

    def log_message(self, *a):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
