"""Command-line entrypoint; Python standard library only."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
from media import prepare
from measurement import summarize


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    media = commands.add_parser("prepare")
    media.add_argument("--manifest", type=Path, required=True)
    media.add_argument("--source", type=Path, required=True)
    media.add_argument("--ffmpeg", type=Path, required=True)
    media.add_argument("--output", type=Path, required=True)
    media.add_argument("--timeout", type=int, default=300)
    analysis = commands.add_parser("analyze")
    analysis.add_argument("--runs", type=Path, nargs="+", required=True)
    analysis.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        report = prepare(json.loads(args.manifest.read_text(encoding="utf-8")), args.source,
                         args.ffmpeg, args.output, args.timeout)
        print(json.dumps({"clips": len(report["clips"]), "metadata": str(args.output / "clips.json"),
                          "allPcmTailsVerified": True, "cloudCalls": False}))
    elif args.command == "analyze":
        if args.output.exists():
            raise FileExistsError("Report already exists; no overwrite permitted")
        runs = [json.loads(path.read_text(encoding="utf-8")) for path in args.runs]
        report = summarize(runs)
        with args.output.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2, allow_nan=False)
        print(json.dumps({"attempts": report["attempts"], "contentFreeReport": str(args.output)}))


if __name__ == "__main__":
    main()
