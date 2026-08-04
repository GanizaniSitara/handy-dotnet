# Handy.NET

Handy.NET is a local dictation tool. Press a shortcut, talk, and your words are
typed into whatever you were working in. Everything runs on your own machine —
no audio leaves it, and it works offline.

## The Basics

Press **Ctrl+Space**, speak, then press it again to stop. Handy transcribes what
you said and types it into the window you were in.

Press **Escape** at any point to throw the dictation away — while you are still
talking, or while it is still transcribing.

The shortcuts are all configurable on the **General** tab.

| Shortcut | Does |
|---|---|
| Ctrl+Space | Start and stop dictating |
| Escape | Cancel and discard |
| Ctrl+Shift+C | Copy the last transcript to the clipboard again |
| Ctrl+Shift+Space | Send the dictation to your task inbox instead of typing it |

## Where The Text Goes

Handy remembers which window you were in when you started talking, and types
there. If something else has taken over the screen by the time it is ready —
a notification, a chat window, a dialog — **it will not type into the wrong
place**. Instead it puts the transcript on your clipboard and tells you, so you
can paste it wherever you actually meant.

The same happens if Windows refuses the keystrokes. Anything running as
administrator is protected from ordinary programs typing into it, so if you
dictate into an elevated console, Handy cannot type there. It will say so, and
your text will be on the clipboard.

If nothing appears where you expected, check the clipboard first.

## Choosing How It Types

**Paste method** on the General tab controls the mechanism.

| Method | Use when |
|---|---|
| Direct | The default. Types the characters one by one. Works in terminals and apps that swallow Ctrl+V. |
| CtrlV | Ordinary paste. Fast, but some terminals treat Ctrl+V as something else. |
| CtrlShiftV / ShiftInsert | Alternatives for terminals with their own paste key. |
| None | Transcribe only — nothing is typed. |

**Auto-submit** can press Enter for you after the text lands, which is handy for
chat boxes and prompts.

## Speed And Accuracy

The **Models** tab picks the speech engine.

**Parakeet** is the default and the fastest — roughly 0.7 s for a short clip.
**Whisper** is slower (about 1.5 s for `tiny.en`, 3 s for `base`) and is worth
choosing only if you want recognition biasing, described below.

**Fast transcription** on the General tab starts decoding during your natural
pauses instead of waiting until you stop, which noticeably shortens the wait on
longer dictations.

**Trim silence with VAD** removes silence from the ends of a recording. If words
are getting clipped, raise **Padding**; if long pauses are cutting your
dictation short, raise **Max gap**.

## Getting Names And Jargon Right

Speech recognition mangles product names, acronyms and team names. **Domain
Terms** on the **Advanced** tab fixes that: list what the recogniser tends to
hear and what it should have been, and Handy rewrites the finished text.

This works with every engine. If you use Whisper, you can additionally switch on
**Recognition biasing** on the Models tab, which hands your terms to the
recogniser up front so it is more likely to hear them right in the first place.

For the full detail — context gates, variants, worked examples — open the
**Domain Terms** help from the Advanced tab.

## History

Handy keeps your recent transcripts. Open the history window from the tray icon
to reread or recopy anything recent. **Keep last N entries** controls how many.

If a dictation seems to have vanished, history is the second place to look after
the clipboard.

## When Something Goes Wrong

The **Log** tab shows what Handy is doing. Each dictation records one summary
line covering how long each stage took and how it ended.

If dictation stops working, the usual causes are:

- **Nothing typed.** Check the clipboard — Handy refuses to type into the wrong
  window and copies instead. The log names the window that took over.
- **Nothing heard.** Check **Microphone** on the General tab is the device you
  are actually speaking into.
- **The shortcut does nothing.** Another program may have claimed the same key
  combination. Try a different one.

Set **file verbosity** on the Log tab to `Verbose` before reproducing a problem,
and the log will contain the per-stage detail needed to diagnose it. Session
start, exit and crash records are always written regardless of that setting.

## Privacy

Audio is transcribed on your machine by a local model. Nothing is uploaded, and
no account or network connection is required once the model is installed.
