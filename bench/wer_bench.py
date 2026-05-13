"""Tier 1 parallel-history WER bench for handy-dotnet vs upstream Handy.

Upstream Handy stores transcripts in SQLite at
    %APPDATA%/com.pais.handy/history.db      (table transcription_history, timestamp INTEGER unix-seconds)

handy-dotnet stores transcripts in JSON at
    %APPDATA%/Handy/history.json             (array of {Text, TimestampUtc})

This tool loads both, pairs entries by timestamp (nearest within a tolerance
window), and computes symmetric WER for each pair. Since both instances receive
the same mic signal simultaneously (dual low-level hooks in the Citrix setup),
aligned pairs are effectively two transcripts of the same utterance.
"""

from __future__ import annotations

import argparse
import json
import re
import sqlite3
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import jiwer


# Port of Handy-dotnet src/Handy/Services/TranscriptFilter.cs, which itself ports
# upstream Handy's src-tauri/src/audio_toolkit/text.rs:filter_transcription_output.
# Upstream stores RAW transcription in SQLite; handy-dotnet stores FILTERED text
# in history.json. To compare like for like we apply the same filter to both.
_FILLERS_BY_LANG: dict[str, list[str]] = {
    "en": ["uh", "um", "uhm", "umm", "uhh", "uhhh", "ah", "hmm", "hm", "mmm", "mm", "mh", "eh", "ehh", "ha"],
    "es": ["ehm", "mmm", "hmm", "hm"],
    "pt": ["ahm", "hmm", "mmm", "hm"],
    "fr": ["euh", "hmm", "hm", "mmm"],
    "de": ["äh", "ähm", "hmm", "hm", "mmm"],
    "it": ["ehm", "hmm", "mmm", "hm"],
}
_FILLERS_FALLBACK = ["uh", "uhm", "umm", "uhh", "uhhh", "ah", "hmm", "hm", "mmm", "mm", "mh", "ehh"]
_MULTISPACE = re.compile(r"\s{2,}")


def _fillers_for(lang: str) -> list[str]:
    if not lang:
        return _FILLERS_FALLBACK
    base = lang.split("-")[0].split("_")[0].lower()
    return _FILLERS_BY_LANG.get(base, _FILLERS_FALLBACK)


def _collapse_stutters(text: str) -> str:
    words = text.split()
    out: list[str] = []
    i = 0
    while i < len(words):
        w = words[i]
        if w.isalpha():
            lower = w.lower()
            count = 1
            while i + count < len(words) and words[i + count].lower() == lower:
                count += 1
            out.append(w)
            i += count if count >= 3 else 1
        else:
            out.append(w)
            i += 1
    return " ".join(out)


def apply_transcript_filter(text: str, lang: str = "en") -> str:
    if not text:
        return ""
    filtered = text
    for w in _fillers_for(lang):
        pat = re.compile(rf"(?i)\b{re.escape(w)}\b[,.]?")
        filtered = pat.sub("", filtered)
    filtered = _collapse_stutters(filtered)
    filtered = _MULTISPACE.sub(" ", filtered)
    return filtered.strip()


@dataclass(frozen=True)
class Entry:
    source: str
    text: str
    ts: float  # unix seconds, UTC


def load_upstream(db_path: Path) -> list[Entry]:
    conn = sqlite3.connect(str(db_path))
    try:
        cur = conn.execute(
            "SELECT transcription_text, post_processed_text, timestamp "
            "FROM transcription_history ORDER BY timestamp ASC"
        )
        out: list[Entry] = []
        for raw, post, ts in cur.fetchall():
            # Prefer raw transcription_text so we compare apples to apples.
            # post_processed_text is LLM-massaged and would bias the result.
            text = (raw or "").strip()
            if not text:
                continue
            out.append(Entry("upstream", text, float(ts)))
        return out
    finally:
        conn.close()


def load_dotnet(json_path: Path) -> list[Entry]:
    data = json.loads(json_path.read_text(encoding="utf-8"))
    out: list[Entry] = []
    for item in data:
        text = (item.get("Text") or "").strip()
        ts_str = item.get("TimestampUtc")
        if not text or not ts_str:
            continue
        dt = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        out.append(Entry("dotnet", text, dt.timestamp()))
    out.sort(key=lambda e: e.ts)
    return out


def pair_entries(
    upstream: list[Entry], dotnet: list[Entry], tolerance_s: float
) -> list[tuple[Entry, Entry, float]]:
    """Greedy nearest-neighbour pairing within tolerance.

    Each dotnet entry is used at most once. Returns (upstream, dotnet, dt_seconds).
    """
    dotnet_sorted = sorted(dotnet, key=lambda e: e.ts)
    used = [False] * len(dotnet_sorted)
    pairs: list[tuple[Entry, Entry, float]] = []
    for u in sorted(upstream, key=lambda e: e.ts):
        best_i = -1
        best_dt = tolerance_s + 1
        for i, d in enumerate(dotnet_sorted):
            if used[i]:
                continue
            dt = abs(d.ts - u.ts)
            if dt <= tolerance_s and dt < best_dt:
                best_i = i
                best_dt = dt
            if d.ts - u.ts > tolerance_s:
                break
        if best_i >= 0:
            used[best_i] = True
            pairs.append((u, dotnet_sorted[best_i], best_dt))
    return pairs


# jiwer transform: the default behaviour is harsh on punctuation/case.
# We want "is this working" and "Is this working?" to score as identical.
_transform = jiwer.Compose(
    [
        jiwer.ToLowerCase(),
        jiwer.RemovePunctuation(),
        jiwer.RemoveMultipleSpaces(),
        jiwer.Strip(),
        jiwer.ReduceToListOfListOfWords(),
    ]
)


def wer(reference: str, hypothesis: str) -> float:
    return float(
        jiwer.wer(
            reference,
            hypothesis,
            reference_transform=_transform,
            hypothesis_transform=_transform,
        )
    )


def normalise_for_wer(text: str, lang: str, apply_filter: bool) -> str:
    """Optionally apply the transcript filter so upstream (raw) and dotnet
    (already filtered) are compared in the same space."""
    return apply_transcript_filter(text, lang) if apply_filter else text


def summarise(values: list[float]) -> dict[str, float]:
    if not values:
        return {"count": 0}
    s = sorted(values)
    n = len(s)
    mean = sum(s) / n
    median = s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2
    return {
        "count": n,
        "mean": round(mean, 4),
        "median": round(median, 4),
        "p90": round(s[min(n - 1, int(n * 0.9))], 4),
        "max": round(s[-1], 4),
    }


def fmt_ts(ts: float) -> str:
    return datetime.fromtimestamp(ts, tz=timezone.utc).isoformat(timespec="seconds")


# Short high-frequency function words that are acoustically low-information
# and therefore the class most likely to flip under a small pipeline delta
# (VAD trim, encoding, normalisation). The user reported 'this' -> 'them' as
# the canonical case; this set captures that family.
_FUNCTION_WORDS = frozenset(
    [
        "this", "that", "these", "those", "them", "they", "there", "their",
        "then", "than", "the", "a", "an", "is", "was", "were", "are", "be",
        "been", "being", "i", "he", "she", "it", "we", "you", "me", "him",
        "her", "us", "and", "or", "but", "of", "in", "on", "at", "to", "for",
        "with", "by", "from", "as", "if", "so", "not",
    ]
)


def _tokens(s: str) -> list[str]:
    return [t.lower() for t in s.split() if t]


def classify_single_token_swap(u_norm: str, d_norm: str) -> tuple[str, str] | None:
    """Return (upstream_tok, dotnet_tok) when the two normalised texts differ
    by exactly one substitution at the same position and both tokens are
    function words. Else None."""
    ut, dt = _tokens(u_norm), _tokens(d_norm)
    if len(ut) != len(dt) or not ut:
        return None
    diffs = [(a, b) for a, b in zip(ut, dt) if a != b]
    if len(diffs) != 1:
        return None
    a, b = diffs[0]
    if a in _FUNCTION_WORDS and b in _FUNCTION_WORDS:
        return (a, b)
    return None


def build_report(
    pairs: list[tuple[Entry, Entry, float]],
    upstream: list[Entry],
    dotnet: list[Entry],
    tolerance_s: float,
    lang: str,
    apply_filter: bool,
) -> tuple[str, dict]:
    rows = []
    for u, d, dt in pairs:
        u_norm = normalise_for_wer(u.text, lang, apply_filter)
        d_norm = normalise_for_wer(d.text, lang, apply_filter)
        wer_ud = wer(u_norm, d_norm)  # upstream as reference
        wer_du = wer(d_norm, u_norm)  # dotnet as reference
        rows.append(
            {
                "upstream_ts": fmt_ts(u.ts),
                "dotnet_ts": fmt_ts(d.ts),
                "dt_s": round(dt, 3),
                "upstream_words_raw": len(u.text.split()),
                "dotnet_words_raw": len(d.text.split()),
                "upstream_words_norm": len(u_norm.split()),
                "dotnet_words_norm": len(d_norm.split()),
                "wer_upstream_as_ref": round(wer_ud, 4),
                "wer_dotnet_as_ref": round(wer_du, 4),
                "upstream_text": u.text,
                "dotnet_text": d.text,
                "upstream_text_norm": u_norm,
                "dotnet_text_norm": d_norm,
            }
        )

    wer_u_ref = [r["wer_upstream_as_ref"] for r in rows]
    wer_d_ref = [r["wer_dotnet_as_ref"] for r in rows]

    function_swaps: list[dict] = []
    for r in rows:
        swap = classify_single_token_swap(r["upstream_text_norm"], r["dotnet_text_norm"])
        if swap:
            function_swaps.append(
                {
                    "upstream_ts": r["upstream_ts"],
                    "upstream_token": swap[0],
                    "dotnet_token": swap[1],
                    "upstream_text": r["upstream_text"],
                    "dotnet_text": r["dotnet_text"],
                }
            )

    stats = {
        "tolerance_s": tolerance_s,
        "lang": lang,
        "apply_filter": apply_filter,
        "upstream_total": len(upstream),
        "dotnet_total": len(dotnet),
        "pairs_matched": len(pairs),
        "pairs_unmatched_upstream": len(upstream) - len(pairs),
        "pairs_unmatched_dotnet": len(dotnet) - len(pairs),
        "wer_upstream_as_ref": summarise(wer_u_ref),
        "wer_dotnet_as_ref": summarise(wer_d_ref),
        "function_word_swaps": len(function_swaps),
    }

    # worst offenders - top 10 by max of the two WERs
    worst = sorted(
        rows, key=lambda r: max(r["wer_upstream_as_ref"], r["wer_dotnet_as_ref"]), reverse=True
    )[:10]

    md = []
    md.append("# handy-dotnet vs upstream Handy - Tier 1 parallel WER")
    md.append("")
    md.append(f"- upstream total entries: {stats['upstream_total']}")
    md.append(f"- dotnet total entries: {stats['dotnet_total']}")
    md.append(f"- tolerance: +/-{tolerance_s:g}s")
    md.append(f"- language: {lang}")
    md.append(f"- transcript filter applied before WER: {apply_filter}")
    md.append(f"- pairs matched: {stats['pairs_matched']}")
    md.append(
        f"- unmatched: upstream={stats['pairs_unmatched_upstream']} "
        f"dotnet={stats['pairs_unmatched_dotnet']}"
    )
    md.append("")
    if apply_filter:
        md.append(
            "> Note: upstream stores raw transcription, handy-dotnet stores filter-processed "
            "text. The transcript filter (filler words, stutter collapse) is applied to both "
            "sides before WER so the metric reflects model/VAD/encoding differences only. "
            "Re-run with --no-filter to attribute WER to the filter itself."
        )
        md.append("")
    md.append("## WER distribution")
    md.append("")
    md.append("| direction | count | mean | median | p90 | max |")
    md.append("|---|---:|---:|---:|---:|---:|")
    for label, key in [
        ("upstream as reference", "wer_upstream_as_ref"),
        ("dotnet as reference", "wer_dotnet_as_ref"),
    ]:
        s = stats[key]
        if s.get("count", 0) == 0:
            md.append(f"| {label} | 0 | - | - | - | - |")
        else:
            md.append(
                f"| {label} | {s['count']} | {s['mean']} | {s['median']} | {s['p90']} | {s['max']} |"
            )
    md.append("")
    md.append("## Function-word swaps")
    md.append("")
    md.append(
        "Pairs where the two transcripts differ by exactly one token at the same "
        "position AND both tokens are short function words (this/them/there/then/...). "
        "These are the acoustically-low-information flips that a small pipeline delta "
        "can easily produce — over-representation here points at VAD trim, encoding, "
        "or normalisation as the culprit rather than the model itself."
    )
    md.append("")
    md.append(f"- total: {len(function_swaps)}")
    if function_swaps:
        md.append("")
        md.append("| timestamp | upstream | dotnet | context (upstream) |")
        md.append("|---|---|---|---|")
        for s in function_swaps[:20]:
            ctx = s["upstream_text"].replace("|", "\\|")
            md.append(
                f"| {s['upstream_ts']} | `{s['upstream_token']}` | `{s['dotnet_token']}` | {ctx} |"
            )
    md.append("")
    md.append("## Top 10 disagreements")
    md.append("")
    for r in worst:
        md.append(
            f"### {r['upstream_ts']}  (dt={r['dt_s']}s, "
            f"wer u-ref={r['wer_upstream_as_ref']}, d-ref={r['wer_dotnet_as_ref']})"
        )
        md.append(f"- upstream: {r['upstream_text']}")
        md.append(f"- dotnet  : {r['dotnet_text']}")
        md.append("")

    return "\n".join(md), {
        "stats": stats,
        "pairs": rows,
        "function_word_swaps": function_swaps,
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--upstream",
        type=Path,
        default=Path.home() / "AppData/Roaming/com.pais.handy/history.db",
        help="upstream Handy SQLite history",
    )
    ap.add_argument(
        "--dotnet",
        type=Path,
        required=True,
        help="handy-dotnet history.json (copy off Citrix to a local path first)",
    )
    ap.add_argument(
        "--tolerance-s",
        type=float,
        default=2.0,
        help="pair matching tolerance in seconds (default 2.0)",
    )
    ap.add_argument(
        "--lang",
        default="en",
        help="language code for the transcript filter (default en)",
    )
    ap.add_argument(
        "--no-filter",
        action="store_true",
        help="skip the transcript filter before WER (exposes filter-port bugs)",
    )
    ap.add_argument(
        "--out",
        type=Path,
        default=Path.cwd() / "wer_bench_report",
        help="output basename - writes <out>.md and <out>.json",
    )
    args = ap.parse_args(argv)

    if not args.upstream.exists():
        print(f"upstream db not found: {args.upstream}", file=sys.stderr)
        return 2
    if not args.dotnet.exists():
        print(f"dotnet json not found: {args.dotnet}", file=sys.stderr)
        return 2

    upstream = load_upstream(args.upstream)
    dotnet = load_dotnet(args.dotnet)
    pairs = pair_entries(upstream, dotnet, args.tolerance_s)

    md, payload = build_report(
        pairs, upstream, dotnet, args.tolerance_s, args.lang, not args.no_filter
    )

    out_md = args.out.with_suffix(".md")
    out_json = args.out.with_suffix(".json")
    out_md.write_text(md, encoding="utf-8")
    out_json.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"wrote {out_md}")
    print(f"wrote {out_json}")
    print()
    print(md.split("## Top 10")[0].rstrip())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
