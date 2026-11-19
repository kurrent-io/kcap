---
name: need-to-know
description: >-
  This skill should be used when the user asks for need-to-know formatting,
  need-to-know marks or graded replies — "use need to know formatting",
  "enable need-to-know formatting", "grade your replies", "mark the detail" — or asks to stop using it. It says
  how to mark the detail and footnote parts of a chat reply with corner
  brackets, which the kcap desktop app can fade or hide.
---

# Need-to-know formatting

The reader can fade or hide the less important parts of your replies. Once
they ask for this, grade every chat reply until they ask you to stop.

## The levels

Grade each part by what the reader does with it:

- **Must know** — the answer, anything you need them to decide or do, and
  anything that would mislead them if missed (a failure, a risk, a change of
  plan). Someone who reads only this can reply. Leave it unmarked.
- **Detail** — what explains or supports the must know: how things work,
  reasons, background, options they need not weigh. They read it to check or
  dig in. Wrap it in `⌞` and `⌟`.
- **Footnote** — evidence and references: file paths, line numbers, setting
  names, counts, commands, what you checked. Wrap it in `⌞⌞` and `⌟⌟`.

Hidden text is one hover away, so when unsure, grade down. In a typical reply
most of the text is detail or footnote.

## Writing the marks

- Every paragraph, bullet, heading and table cell starts at must know.
- A marked span must be removable: start and end it at sentence or clause
  boundaries, so what is left still reads correctly when it is hidden. Wrap a
  whole paragraph or bullet to grade all of it.
- A footnote can sit inside a detail span.
- Use the marks only in chat replies: never in files, code, commit messages,
  PR descriptions, comments or any other tool input. Marks inside code spans
  and blocks are shown as typed.

Example:

```
Tests pass and the fix is committed. ⌞The hover check now looks at the line's
width, so a pointer past the end of a line no longer lights it.⌟ ⌞⌞30 tests
ran; the new one fails without the fix.⌟⌟

Should I push it?
```

## Stopping

When the user asks you to stop, write unmarked replies from then on. Earlier
replies keep their marks; there is nothing to undo.
