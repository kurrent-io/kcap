# Contributing

[CLAUDE.md](CLAUDE.md) contains the repository's build, test, comment, commit, and PR conventions.
Its comment policy applies to human contributors and coding agents alike.

Before pushing, run:

```bash
bash scripts/check-comment-rules.sh
bash scripts/check-comment-rules.test.sh
```

The checker requires Git and Python 3.9+. CI runs it for every code PR and main-branch push
in the `No Linear issue IDs in C# source` job, which feeds the `CI passed` aggregate.
It reads tracked working-tree files, so stage new files before running it.
Errors exit 2; violations exit 1.
The full shell suite (`bash scripts/run-shell-tests.sh`) runs on Linux CI and needs packaging
tools such as `dpkg-deb` locally.

Coverage includes `.cs`, `.c`, `.h`, `.axaml`, `.csproj`, `.props`, `.targets`, `.sh`, `.yml`,
`.yaml`, and `.py` across the repository. Historical material under `docs/superpowers/` is
excluded. String literals, shell heredoc bodies, XML CDATA, and YAML block scalars are data,
not comments. Linear issue IDs are rejected anywhere in the scanned files, including data;
only synthetic C# test data under `test/` may use `// linear-id-ok: <reason>`.

The comment gate catches high-confidence design coordinates, review artifacts, and change
narration. RFC section references are allowed. Normal review still needs to catch comments
that restate the code, lengthy explanations, dated facts, closed ticket references, and
less distinctive historical language. Keep a comment only when it states a current constraint
that readers cannot infer from the code. Preserve that constraint when removing a historical
reference; do not remove useful compatibility or concurrency explanations.

`scripts/comment-rules-baseline.json` is empty after the cleanup. Its format records debt by
path, rule, comment text, and occurrence count. Line movement is harmless; additional occurrences and changed text are new
violations. Rewrite a flagged comment to its current constraint instead of adding an exception.
When cleaning up baseline entries, run:

```bash
bash scripts/check-comment-rules.sh --write-baseline
```

This command prunes resolved entries and refuses new violations. CI also compares the baseline
with the PR's base commit or the preceding main-branch commit and rejects additions or count
increases, including hand edits.
Changes to the gate itself require updating its regression tests in
`scripts/check-comment-rules.test.sh`.
