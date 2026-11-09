"""Serve public metadata separately from licensing and account data."""
from waitress import serve
from .app import create_app

if __name__ == '__main__':
    serve(create_app(), host='0.0.0.0', port=8092, threads=4,
          max_request_body_size=4096, max_request_header_size=16384,
          channel_timeout=15, expose_tracebacks=False)
