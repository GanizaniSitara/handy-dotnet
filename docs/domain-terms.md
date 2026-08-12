# Domain Terms

Domain Terms are explicit transcript corrections for names, acronyms, products,
systems, teams, and business phrases that speech recognition often gets wrong.

They run after the raw transcript and normal filler-word cleanup. They do not
teach Parakeet to hear differently; they rewrite the finished text when a rule
matches.

## Two Ways Your Terms Are Used

Domain Terms feed two separate mechanisms. Knowing which is which explains why
one of them is sometimes unavailable.

**Corrections after transcription — every backend.** Your rules rewrite the
finished text. This always runs, whichever backend you use, and is what the rest
of this page describes.

**Recognition biasing — Whisper only.** Whisper accepts a vocabulary prompt, so
your canonical terms can be handed to the recogniser up front, making it more
likely to hear them correctly in the first place. Enable it under **Recognition
biasing** on the **Models** tab. Parakeet has no equivalent, so the option is
disabled when Parakeet is selected. Your corrections still apply.

The trade-off is speed. Parakeet transcribes a short clip in roughly 0.7 s;
Whisper `tiny.en` takes about 1.5 s and `base` about 3 s. So biasing costs you
noticeably slower dictation. If corrections alone get your terms right — and for
most vocabularies they do — staying on Parakeet is the faster choice.

## Where To Find Them

Open **Settings**, choose **Advanced**, then use the **Domain Terms** table.

Click **Apply** after editing. Rows with no canonical term or no variant are
ignored.

## Columns

| Column | Meaning |
|---|---|
| On | Enables or disables this rule without deleting it. |
| Canonical | The exact text Handy should output. |
| Variants | What speech recognition tends to hear instead. Separate multiple values with semicolons or commas. |
| Require Any | Optional context gate. If set, at least one of these words or phrases must be near the variant. |
| Block | Optional safety gate. If any of these words or phrases are near the variant, the rule is skipped. |
| Case | Match variants case-sensitively. Usually leave this off. |
| Notes | Free text for why the rule exists. It does not affect matching. |

## Basic Example

If Handy hears:

```text
open monica needs better matching
```

Add:

| On | Canonical | Variants | Require Any | Block | Case | Notes |
|---|---|---|---|---|---|---|
| checked | Project Aurora | project or aura; project a roar |  |  | unchecked | Project name |

Output becomes:

```text
Project Aurora needs better matching
```

## Context Example

Use **Require Any** when a phrase is only safe in a specific domain.

| On | Canonical | Variants | Require Any | Block | Case | Notes |
|---|---|---|---|---|---|---|
| checked | ABAC | a back; aback | access control; permission; policy |  | unchecked | Only in IAM/security context |

This changes:

```text
the access control model uses a back rules
```

to:

```text
the access control model uses ABAC rules
```

But it will not change an unrelated sentence such as:

```text
move a back button to the toolbar
```

because the required context is missing.

## Block Example

Use **Block** when a correction is usually right but has a known false-positive.

| On | Canonical | Variants | Require Any | Block | Case | Notes |
|---|---|---|---|---|---|---|
| checked | Northstar | north star; north's tar |  | astronomy; constellation | unchecked | Avoid changing astronomy terms |

If the surrounding text mentions `astronomy` or `constellation`, this rule is skipped.

## Matching Rules

- Matches are whole phrases, not partial words.
- Spaces inside a variant are flexible, so `open monica` can match normal
  whitespace variations.
- Matching is case-insensitive unless **Case** is checked.
- Required and blocked context are checked in nearby text around the matched
  variant, not the whole transcript.
- Rules are applied in table order.

## Practical Guidance

- Start with the smallest safe rule: one canonical term and one or two variants.
- Use **Require Any** for short or common variants such as `a back`, `aim`,
  `arc`, or `om`.
- Leave **Block** empty until you see a real false-positive.
- Prefer semicolons for lists: `open monica; open monarch; open marker`.
- Check the Log tab after Apply and a test dictation. Applied rules are logged
  separately from raw ASR and filler/stutter filtering.

## Troubleshooting

If a rule does nothing:

- Confirm **On** is checked.
- Confirm **Canonical** is not empty.
- Confirm **Variants** contains what Handy actually heard.
- If **Require Any** is set, confirm the transcript contains one of those context
  phrases near the variant.
- If **Block** is set, confirm none of those phrases are near the variant.
- Click **Apply** after editing.

If a rule changes too much:

- Add one or more **Require Any** phrases.
- Add a **Block** phrase for the false-positive context.
- Split broad rules into more specific rows.
