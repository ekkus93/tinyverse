"""Command line: python -m greeter --url ws://HOST:7878/ws --token-file FILE [--no-llm]"""

from __future__ import annotations

import argparse
import asyncio
import logging
import os
import sys

from .agent import Greeter, Options
from .connection import split_pin


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="greeter", description="An AI agent that welcomes people to a Tinyverse world.")
    p.add_argument("--url", default=os.environ.get("TINYVERSE_URL", "ws://127.0.0.1:7878/ws"),
                   help="ws:// or wss:// URL of the world (default %(default)s, or TINYVERSE_URL); a wss:// "
                   "URL may end in #sha256=<hex> to accept exactly the certificate with that fingerprint")
    p.add_argument("--token", default=os.environ.get("TINYVERSE_TOKEN", ""),
                   help="agent token from `tinyverse-server token create` (or TINYVERSE_TOKEN)")
    p.add_argument("--token-file", help="file holding the token")
    p.add_argument("--name", default="", help="display name (default: the token's name, or Greeter)")
    p.add_argument("--ca-file", help="PEM certificate(s) to trust for a wss:// URL, e.g. the world's "
                   "self-signed cert.pem (default: the system's trusted certificates)")
    p.add_argument("--password", default=os.environ.get("TINYVERSE_PASSWORD", ""),
                   help="the world's join password, when joining without a token")
    p.add_argument("--no-llm", action="store_true", help="canned replies and simple rules; no API key needed")
    p.add_argument("--model", default=None, help="Claude model id (default: GREETER_MODEL or claude-opus-5-5)")
    p.add_argument("--greet-radius", type=float, default=4.0, help="metres (default %(default)s)")
    p.add_argument("--greet-agents", action="store_true", help="also greet other agents")
    p.add_argument("-v", "--verbose", action="store_true")
    return p.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s", stream=sys.stderr)
    for noisy in ("websockets", "httpx", "anthropic"):
        logging.getLogger(noisy).setLevel(logging.WARNING)
    token = args.token
    if args.token_file:
        with open(args.token_file, encoding="utf-8") as f:
            token = f.read().strip()
    name = args.name or ("" if token else "Greeter")
    if not args.no_llm and not os.environ.get("ANTHROPIC_API_KEY"):
        print("greeter: ANTHROPIC_API_KEY is not set; set it, or run with --no-llm", file=sys.stderr)
        return 2
    if args.ca_file and not args.url.startswith("wss://"):
        print("greeter: --ca-file needs a wss:// URL", file=sys.stderr)
        return 2
    try:
        split_pin(args.url)
    except ValueError as e:
        print(f"greeter: --url: {e}", file=sys.stderr)
        return 2
    opts = Options(url=args.url, ca_file=args.ca_file, token=token, name=name, password=args.password, no_llm=args.no_llm,
                   model=args.model, greet_radius=args.greet_radius,
                   greet_agents=args.greet_agents)
    return asyncio.run(Greeter(opts).run())


if __name__ == "__main__":
    sys.exit(main())
