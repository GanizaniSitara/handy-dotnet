"""Latency bench for Handy.NET ASR backends.

Runs Handy's one-shot ``--transcribe-file`` mode against a set of WAV clips and
summarises per-stage timings from the ``--transcribe-file: diag`` log line.

Example:

    python bench/asr_latency_bench.py --audio clips/short.wav --audio clips/medium.wav

Manifest form:

    {
      "domainCorrections": [],
      "cases": [
        {"id": "short", "audio": "clips/short.wav", "expected": "hello world"}
      ]
    }
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import statistics
import string
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path


_DIAG_RE = re.compile(r"--transcribe-file: diag (?P<fields>.*)$")
_TRANSCRIBE_RE = re.compile(r'--transcribe-file: raw="(?P<raw>.*?)" postProcessed="(?P<final>.*?)"$')
_KV_RE = re.compile(r"(?P<key>[A-Za-z0-9_]+)=(?P<value>\S+)")
_PUNCT = str.maketrans("", "", string.punctuation)


@dataclass(frozen=True)
class Case:
    id: str
    audio: Path
    expected: str = ""


@dataclass(frozen=True)
class Variant:
    name: str
    backend: str
    whisper_model: str = "base"


def parse_variant(value: str) -> Variant:
    token = value.strip().lower()
    if token == "parakeet":
        return Variant("parakeet", "Parakeet")
    if token.startswith("whisper:"):
        model = token.split(":", 1)[1] or "base"
        if model not in {"tiny.en", "tiny", "base.en", "base", "small.en", "small"}:
            raise argparse.ArgumentTypeError(f"unsupported Whisper model: {model}")
        return Variant(f"whisper_{model.replace('.', '_')}", "Whisper", model)
    raise argparse.ArgumentTypeError(f"unsupported variant: {value}")


def tokens(text: str) -> list[str]:
    return text.lower().translate(_PUNCT).split()


def wer(reference: str, hypothesis: str) -> float | None:
    ref = tokens(reference)
    hyp = tokens(hypothesis)
    if not ref:
        return None

    prev = list(range(len(hyp) + 1))
    for i, ref_tok in enumerate(ref, start=1):
        row = [i]
        for j, hyp_tok in enumerate(hyp, start=1):
            cost = 0 if ref_tok == hyp_tok else 1
            row.append(min(prev[j] + 1, row[j - 1] + 1, prev[j - 1] + cost))
        prev = row
    return prev[-1] / len(ref)


def load_cases(args: argparse.Namespace) -> tuple[list[Case], list[dict]]:
    corrections: list[dict] = []
    cases: list[Case] = []

    if args.manifest:
        manifest_path = args.manifest.resolve()
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        corrections = list(manifest.get("domainCorrections", []))
        for item in manifest.get("cases", []):
            audio = (manifest_path.parent / item["audio"]).resolve()
            cases.append(Case(str(item["id"]), audio, str(item.get("expected", ""))))

    for audio_arg in args.audio:
        audio = audio_arg.resolve()
        cases.append(Case(audio.stem, audio))

    return cases, corrections


def default_roaming() -> Path:
    appdata = os.environ.get("APPDATA")
    if appdata:
        return Path(appdata)
    return Path.home() / "AppData" / "Roaming"


def first_existing(paths: list[Path]) -> Path | None:
    for path in paths:
        if path.exists():
            return path
    return None


def seed_vad_model(data_dir: Path, explicit_source: Path | None) -> None:
    roaming = default_roaming()
    source = explicit_source or first_existing(
        [
            roaming / "Handy" / "models" / "silero_vad.onnx",
            roaming / "com.pais.handy" / "models" / "silero_vad.onnx",
        ]
    )
    if source is None or not source.exists():
        return

    target = data_dir / "models" / "silero_vad.onnx"
    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists() or target.stat().st_size != source.stat().st_size:
        shutil.copy2(source, target)


def build_env(args: argparse.Namespace) -> dict[str, str]:
    env = os.environ.copy()
    roaming = default_roaming()

    whisper_dir = args.whisper_model_dir or first_existing(
        [
            roaming / "Handy" / "models" / "whisper",
            roaming / "com.pais.handy" / "models" / "whisper",
        ]
    )
    if whisper_dir:
        env["HANDY_WHISPER_MODEL_DIR"] = str(whisper_dir.resolve())

    parakeet_dir = args.parakeet_model_dir or first_existing(
        [
            roaming / "Handy" / "models" / "parakeet-tdt-0.6b-v3-int8",
            roaming / "Handy" / "models" / "parakeet-tdt-0.6b-v2-int8",
            roaming / "com.pais.handy" / "models" / "parakeet-tdt-0.6b-v3-int8",
            roaming / "com.pais.handy" / "models" / "parakeet-tdt-0.6b-v2-int8",
        ]
    )
    if parakeet_dir:
        env["HANDY_MODEL_DIR"] = str(parakeet_dir.resolve())

    return env


def write_settings(
    data_dir: Path,
    variant: Variant,
    corrections: list[dict],
    vad_enabled: bool,
    prompt_enabled: bool,
    carry_prompt: bool,
) -> None:
    data_dir.mkdir(parents=True, exist_ok=True)
    settings = {
        "settingsVersion": 2,
        "transcriptionBackend": variant.backend,
        "whisperModel": variant.whisper_model,
        "whisperVocabularyPromptEnabled": prompt_enabled and variant.backend == "Whisper",
        "whisperCarryInitialPrompt": carry_prompt,
        "vadEnabled": vad_enabled,
        "vadThreshold": 0.3,
        "vadPaddingMs": 500,
        "appLanguage": "en",
        "domainCorrections": corrections,
    }
    (data_dir / "settings.json").write_text(json.dumps(settings, indent=2), encoding="utf-8")


def parse_log(log: str) -> tuple[dict, str, str]:
    diag: dict[str, str | int | float] = {}
    raw = ""
    final = ""

    for line in log.splitlines():
        transcribe_match = _TRANSCRIBE_RE.search(line)
        if transcribe_match:
            raw = transcribe_match.group("raw").strip()
            final = transcribe_match.group("final").strip()

        diag_match = _DIAG_RE.search(line)
        if not diag_match:
            continue
        for match in _KV_RE.finditer(diag_match.group("fields")):
            key = match.group("key")
            value = match.group("value")
            if value.lstrip("-").isdigit():
                diag[key] = int(value)
            else:
                try:
                    diag[key] = float(value)
                except ValueError:
                    diag[key] = value

    return diag, raw, final


def run_one(
    handy_exe: Path,
    data_dir: Path,
    audio: Path,
    env: dict[str, str],
) -> tuple[dict, str, str, str | None]:
    log_path = data_dir / "handy.log"
    if log_path.exists():
        log_path.unlink()

    proc = subprocess.run(
        [str(handy_exe), "--data-dir", str(data_dir), "--transcribe-file", str(audio)],
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )

    log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
    diag, raw, final = parse_log(log)
    if not final and (data_dir / "last-transcript.txt").exists():
        final = (data_dir / "last-transcript.txt").read_text(encoding="utf-8", errors="replace").strip()
    if not raw:
        raw = final

    error = None
    if proc.returncode != 0:
        error = f"exit={proc.returncode} stderr={proc.stderr.strip()}"
    if final.startswith("ERROR:"):
        error = final
    if not diag and error is None:
        error = "missing --transcribe-file diag line"

    return diag, raw, final, error


def median(values: list[float]) -> float | None:
    return statistics.median(values) if values else None


def summarise(rows: list[dict]) -> list[dict]:
    groups: dict[tuple[str, str], list[dict]] = {}
    for row in rows:
        if row.get("warmup") or row.get("error"):
            continue
        groups.setdefault((row["case"], row["variant"]), []).append(row)

    summary: list[dict] = []
    for (case_id, variant), items in sorted(groups.items()):
        def vals(key: str) -> list[float]:
            return [float(item[key]) for item in items if isinstance(item.get(key), (int, float))]

        audio_ms = median(vals("audioMs"))
        asr_ms = median(vals("asrMs"))
        total_ms = median(vals("totalMs"))
        summary.append(
            {
                "case": case_id,
                "variant": variant,
                "runs": len(items),
                "audioMs_median": audio_ms,
                "loadMs_median": median(vals("loadMs")),
                "vadMs_median": median(vals("vadMs")),
                "asrMs_median": asr_ms,
                "postMs_median": median(vals("postMs")),
                "totalMs_median": total_ms,
                "asr_rtf_median": round(asr_ms / audio_ms, 3) if audio_ms and asr_ms is not None else None,
                "total_rtf_median": round(total_ms / audio_ms, 3) if audio_ms and total_ms is not None else None,
                "wer_median": median(vals("wer")),
            }
        )
    return summary


def render_report(rows: list[dict], summary: list[dict], run_root: Path) -> str:
    lines = [
        "# Handy.NET ASR Latency Bench",
        "",
        f"- run root: `{run_root}`",
        "",
        "## Summary",
        "",
        "| case | variant | runs | audio ms | load ms | vad ms | asr ms | post ms | total ms | ASR RTF | total RTF | WER |",
        "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for item in summary:
        def fmt(key: str) -> str:
            value = item.get(key)
            if value is None:
                return "-"
            if isinstance(value, float):
                return f"{value:.3f}" if key.endswith("rtf_median") or key == "wer_median" else f"{value:.0f}"
            return str(value)

        lines.append(
            "| {case} | {variant} | {runs} | {audio} | {load} | {vad} | {asr} | {post} | {total} | {asr_rtf} | {total_rtf} | {wer} |".format(
                case=item["case"],
                variant=item["variant"],
                runs=item["runs"],
                audio=fmt("audioMs_median"),
                load=fmt("loadMs_median"),
                vad=fmt("vadMs_median"),
                asr=fmt("asrMs_median"),
                post=fmt("postMs_median"),
                total=fmt("totalMs_median"),
                asr_rtf=fmt("asr_rtf_median"),
                total_rtf=fmt("total_rtf_median"),
                wer=fmt("wer_median"),
            )
        )

    errors = [row for row in rows if row.get("error")]
    if errors:
        lines.extend(["", "## Errors", ""])
        for row in errors:
            lines.append(f"- {row['case']} / {row['variant']} run {row['run']}: {row['error']}")

    lines.extend(["", "## Details", ""])
    for row in rows:
        if row.get("warmup"):
            continue
        lines.append(f"### {row['case']} - {row['variant']} run {row['run']}")
        if row.get("error"):
            lines.append(f"- error: {row['error']}")
        lines.append(f"- raw: {row.get('raw', '')}")
        lines.append(f"- final: {row.get('final', '')}")
        if row.get("expected"):
            lines.append(f"- expected: {row['expected']}")
        lines.append("")

    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--manifest", type=Path)
    ap.add_argument("--audio", type=Path, action="append", default=[])
    ap.add_argument("--handy-exe", type=Path, default=Path("src/Handy/bin/Release/net8.0-windows/Handy.exe"))
    ap.add_argument("--work-dir", type=Path, default=Path(tempfile.gettempdir()) / "handy-asr-latency-bench")
    ap.add_argument("--out", type=Path, default=Path.cwd() / "asr_latency_report")
    ap.add_argument("--variant", type=parse_variant, action="append")
    ap.add_argument("--repetitions", type=int, default=3)
    ap.add_argument("--warmups", type=int, default=1)
    ap.add_argument("--whisper-model-dir", type=Path)
    ap.add_argument("--parakeet-model-dir", type=Path)
    ap.add_argument("--vad-model", type=Path)
    ap.add_argument("--vad-enabled", action=argparse.BooleanOptionalAction, default=True)
    ap.add_argument("--whisper-prompt", action=argparse.BooleanOptionalAction, default=False)
    ap.add_argument("--carry-prompt", action=argparse.BooleanOptionalAction, default=True)
    args = ap.parse_args(argv)

    cases, corrections = load_cases(args)
    if not cases:
        print("provide --audio or --manifest with at least one case", file=sys.stderr)
        return 2

    handy_exe = args.handy_exe.resolve()
    if not handy_exe.exists():
        print(f"Handy executable not found: {handy_exe}", file=sys.stderr)
        return 2

    variants = args.variant or [
        Variant("whisper_tiny_en", "Whisper", "tiny.en"),
        Variant("whisper_tiny", "Whisper", "tiny"),
        Variant("whisper_base", "Whisper", "base"),
        Variant("parakeet", "Parakeet"),
    ]

    env = build_env(args)
    args.work_dir.mkdir(parents=True, exist_ok=True)
    run_root = Path(tempfile.mkdtemp(prefix="run-", dir=args.work_dir)).resolve()

    rows: list[dict] = []
    for case in cases:
        if not case.audio.exists():
            rows.append(
                {
                    "case": case.id,
                    "variant": "-",
                    "run": 0,
                    "error": f"audio not found: {case.audio}",
                }
            )
            continue

        for variant in variants:
            for run in range(args.warmups + args.repetitions):
                data_dir = run_root / case.id / variant.name / f"run-{run:02d}"
                write_settings(
                    data_dir,
                    variant,
                    corrections,
                    args.vad_enabled,
                    args.whisper_prompt,
                    args.carry_prompt,
                )
                seed_vad_model(data_dir, args.vad_model)
                diag, raw, final, error = run_one(handy_exe, data_dir, case.audio, env)
                row = {
                    "case": case.id,
                    "variant": variant.name,
                    "run": run,
                    "warmup": run < args.warmups,
                    "audio": str(case.audio),
                    "expected": case.expected,
                    "raw": raw,
                    "final": final,
                    "error": error,
                    **diag,
                }
                score = wer(case.expected, final)
                if score is not None:
                    row["wer"] = score
                rows.append(row)

    summary = summarise(rows)
    out_md = args.out.with_suffix(".md")
    out_json = args.out.with_suffix(".json")
    out_md.write_text(render_report(rows, summary, run_root), encoding="utf-8")
    out_json.write_text(json.dumps({"run_root": str(run_root), "summary": summary, "rows": rows}, indent=2), encoding="utf-8")

    print(f"wrote {out_md}")
    print(f"wrote {out_json}")
    print()
    print(render_report([], summary, run_root).split("## Details", 1)[0].rstrip())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
