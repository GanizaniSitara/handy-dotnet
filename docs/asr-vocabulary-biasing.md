# ASR Vocabulary Biasing

Handy.NET has two separate places to improve business terms:

1. Recognition-level biasing, where the ASR model is nudged before it emits raw text.
2. Post-ASR glossary correction, where already-emitted text is normalized by explicit rules.

The glossary correction layer is deterministic and auditable, but it cannot make raw ASR better. Recognition-level biasing is useful to test for terms whose spelling, casing, or spacing matters before post-processing, such as product names and acronyms.

## Whisper

Whisper.net 1.9.0 exposes whisper.cpp initial-prompt controls through `WithPrompt(...)` and `WithCarryInitialPrompt(...)`. Handy.NET uses those APIs when all of these are true:

- backend is `Whisper`
- `whisperVocabularyPromptEnabled` is true
- the glossary has at least one enabled canonical term

The generated prompt uses canonical glossary terms only, not mistranscribed variants. That avoids biasing Whisper toward ordinary words that are only present as post-processing match variants.

The prompt is intentionally compact:

```text
Recognize these domain terms exactly when spoken: Azure DevOps; ServiceNow.
```

Runtime logging reports prompt activation by term count, prompt length, and carry mode. It does not replace raw/final transcript logs, so a bench can still distinguish recognition improvement from post-processing correction.

Recommended use: keep disabled by default, enable only when testing Whisper against real clips, and compare raw ASR with and without the prompt. Whisper prompts are a bias, not a hard vocabulary constraint.

## Parakeet

The current Handy.NET Parakeet path is an ONNX Runtime greedy RNN-T/TDT decoder:

- preprocessor ONNX model
- encoder ONNX model
- decoder/joiner ONNX model
- argmax over vocab logits per decoder step

That runtime has no prompt input and no hotword API. Adding business vocabulary at recognition time would require decoder work, not a settings toggle.

NVIDIA NeMo does provide recognition-level customization paths outside this app runtime:

- GPU phrase boosting supports CTC, RNN-T/TDT, and AED models during decoding, without retraining. It applies shallow-fusion score boosting from a key-phrase list.
- NeMo ASR checkpoints can be fine-tuned from pretrained models using manifest datasets, then exported or converted back into a runtime format.

For Handy.NET, the viable Parakeet path is:

1. Collect a regression corpus of WAV clips and expected transcripts, including near-colliding business terms and ordinary-language counterexamples.
2. Evaluate NeMo phrase boosting or fine-tuning in a Python/GPU environment against that corpus.
3. Export a compatible ONNX model and tokenizer assets.
4. Re-run the same Handy.NET bench against the exported model.
5. Only replace the bundled/runtime Parakeet assets if WER improves without regressions on ordinary dictation.

## Bench

`bench/asr_vocab_ab.py` runs Handy's `--transcribe-file` mode against a JSON manifest of WAV clips and compares:

- Parakeet raw output
- Whisper raw output without prompt
- Whisper raw output with glossary-generated prompt
- post-processing-only glossary correction

It writes Markdown and JSON reports with raw and final WER. Use `--data-dir` isolation through the script so the bench does not mutate the real `%APPDATA%\Handy` settings.

Relevant upstream references:

- Whisper.net runtime and builder APIs: https://github.com/sandrohanea/whisper.net
- NVIDIA NeMo word boosting: https://docs.nvidia.com/nemo-framework/user-guide/26.02/nemotoolkit/asr/asr_customization/word_boosting.html
- NVIDIA NeMo ASR model/fine-tuning overview: https://docs.nvidia.com/nemo-framework/user-guide/latest/nemotoolkit/asr/models.html
