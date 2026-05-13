"""A/B bench for recognition-level vocabulary biasing.

Runs Handy's one-shot transcription mode against a manifest of WAV clips and
compares raw and final output across:

* current Parakeet, no glossary
* Whisper, no prompt
* Whisper with glossary-generated vocabulary prompt
* post-processing-only glossary correction

Example manifest:

{
  "domainCorrections": [
    {
      "enabled": true,
      "from": "service now",
      "to": "ServiceNow",
      "variants": ["service now", "snow"],
      "requiredContext": ["ticket", "incident"]
    }
  ],
  "cases": [
    {
      "id": "servicenow-ticket",
      "audio": "clips/servicenow-ticket.wav",
      "expected": "Open a ServiceNow ticket."
    }
  ]
}
"""

from __future__ import annotations

import argparse
import json
import os
import re
import string
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path


_TRANSCRIBE_RE = re.compile(r'--transcribe-file: raw="(.*?)" postProcessed="(.*?)"$')
_PUNCT = str.maketrans("", "", string.punctuation)


@dataclass(frozen=True)
class Variant:
    name: str
    backend: str
    glossary: bool
    prompt: bool


VARIANTS = [
    Variant("parakeet_raw", "Parakeet", glossary=False, prompt=False),
    Variant("whisper_plain", "Whisper", glossary=False, prompt=False),
    Variant("whisper_prompt", "Whisper", glossary=True, prompt=True),
    Variant("postprocess_only", "Parakeet", glossary=True, prompt=False),
]


def tokens(text: str) -> list[str]:
    return text.lower().translate(_PUNCT).split()


def wer(reference: str, hypothesis: str) -> float:
    ref = tokens(reference)
    hyp = tokens(hypothesis)
    if not ref:
        return 0.0 if not hyp else 1.0

    prev = list(range(len(hyp) + 1))
    for i, ref_tok in enumerate(ref, start=1):
        row = [i]
        for j, hyp_tok in enumerate(hyp, start=1):
            cost = 0 if ref_tok == hyp_tok else 1
            row.append(min(prev[j] + 1, row[j - 1] + 1, prev[j - 1] + cost))
        prev = row
    return prev[-1] / len(ref)


def write_settings(
    data_dir: Path,
    variant: Variant,
    whisper_model: str,
    corrections: list[dict],
    carry_prompt: bool,
) -> None:
    data_dir.mkdir(parents=True, exist_ok=True)
    settings = {
        "settingsVersion": 2,
        "transcriptionBackend": variant.backend,
        "whisperModel": whisper_model,
        "whisperVocabularyPromptEnabled": variant.prompt,
        "whisperCarryInitialPrompt": carry_prompt,
        "vadEnabled": False,
        "appLanguage": "en",
        "customFillerWords": [],
        "domainCorrections": corrections if variant.glossary else [],
    }
    (data_dir / "settings.json").write_text(json.dumps(settings, indent=2), encoding="utf-8")


def run_one(
    handy_exe: Path,
    data_dir: Path,
    audio: Path,
    env: dict[str, str],
) -> tuple[str, str, str | None]:
    if (data_dir / "handy.log").exists():
        (data_dir / "handy.log").unlink()

    proc = subprocess.run(
        [str(handy_exe), "--data-dir", str(data_dir), "--transcribe-file", str(audio)],
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )

    log = (data_dir / "handy.log").read_text(encoding="utf-8", errors="replace") if (data_dir / "handy.log").exists() else ""
    raw = ""
    final = ""
    for line in reversed(log.splitlines()):
        match = _TRANSCRIBE_RE.search(line)
        if match:
            raw = match.group(1).strip()
            final = match.group(2).strip()
            break

    if not final and (data_dir / "last-transcript.txt").exists():
        final = (data_dir / "last-transcript.txt").read_text(encoding="utf-8", errors="replace").strip()
    if not raw:
        raw = final

    error = None
    if proc.returncode != 0:
        error = f"exit={proc.returncode} stderr={proc.stderr.strip()}"
    if final.startswith("ERROR:"):
        error = final
    return raw, final, error


def render_report(rows: list[dict]) -> str:
    lines = [
        "# ASR Vocabulary Biasing Bench",
        "",
        "| case | variant | raw WER | final WER | error |",
        "|---|---|---:|---:|---|",
    ]
    for row in rows:
        error = (row.get("error") or "").replace("|", "\\|")
        raw_wer = "-" if row.get("error") else f"{row['raw_wer']:.3f}"
        final_wer = "-" if row.get("error") else f"{row['final_wer']:.3f}"
        lines.append(f"| {row['case']} | {row['variant']} | {raw_wer} | {final_wer} | {error} |")

    lines.extend(["", "## Details", ""])
    for row in rows:
        lines.append(f"### {row['case']} - {row['variant']}")
        lines.append(f"- expected: {row['expected']}")
        lines.append(f"- raw: {row['raw']}")
        lines.append(f"- final: {row['final']}")
        if row.get("error"):
            lines.append(f"- error: {row['error']}")
        lines.append("")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--manifest", type=Path, required=True)
    ap.add_argument("--handy-exe", type=Path, default=Path("src/Handy/bin/Release/net8.0-windows/Handy.exe"))
    ap.add_argument("--work-dir", type=Path, default=Path(tempfile.gettempdir()) / "handy-asr-vocab-bench")
    ap.add_argument("--out", type=Path, default=Path.cwd() / "asr_vocab_report")
    ap.add_argument("--whisper-model", default="base")
    ap.add_argument("--whisper-model-dir", type=Path)
    ap.add_argument("--parakeet-model-dir", type=Path)
    ap.add_argument("--carry-prompt", action=argparse.BooleanOptionalAction, default=True)
    args = ap.parse_args(argv)

    manifest_path = args.manifest.resolve()
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest_root = manifest_path.parent
    corrections = manifest.get("domainCorrections", [])
    cases = manifest.get("cases", [])
    if not cases:
        print("manifest contains no cases", file=sys.stderr)
        return 2

    handy_exe = args.handy_exe.resolve()
    if not handy_exe.exists():
        print(f"Handy executable not found: {handy_exe}", file=sys.stderr)
        return 2

    env = os.environ.copy()
    if args.whisper_model_dir:
        env["HANDY_WHISPER_MODEL_DIR"] = str(args.whisper_model_dir.resolve())
    if args.parakeet_model_dir:
        env["HANDY_MODEL_DIR"] = str(args.parakeet_model_dir.resolve())

    args.work_dir.mkdir(parents=True, exist_ok=True)
    run_root = Path(tempfile.mkdtemp(prefix="run-", dir=args.work_dir))

    rows: list[dict] = []
    for case in cases:
        case_id = str(case["id"])
        audio = (manifest_root / case["audio"]).resolve()
        expected = str(case["expected"])
        for variant in VARIANTS:
            data_dir = run_root / case_id / variant.name
            write_settings(data_dir, variant, args.whisper_model, corrections, args.carry_prompt)
            raw, final, error = run_one(handy_exe, data_dir, audio, env)
            rows.append(
                {
                    "case": case_id,
                    "variant": variant.name,
                    "expected": expected,
                    "raw": raw,
                    "final": final,
                    "raw_wer": wer(expected, raw),
                    "final_wer": wer(expected, final),
                    "error": error,
                }
            )

    out_md = args.out.with_suffix(".md")
    out_json = args.out.with_suffix(".json")
    out_md.write_text(render_report(rows), encoding="utf-8")
    out_json.write_text(json.dumps({"rows": rows}, indent=2), encoding="utf-8")
    print(f"wrote {out_md}")
    print(f"wrote {out_json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
