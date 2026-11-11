#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
python3 - "$here" <<'PY'
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

scripts = Path(sys.argv.pop()).resolve()
spec = importlib.util.spec_from_file_location("comment_rules", scripts / "check-comment-rules.py")
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class CommentSyntaxTests(unittest.TestCase):
    def found(self, suffix, source):
        return [(line, rule) for line, text in checker.comments(Path("file" + suffix), source)
                for rule, regex in checker.RULES.items() if regex.search(checker.RFC_SECTION.sub("", text))]

    def test_banned_patterns(self):
        examples = {
            "spec-coordinate": ["§4.2", "§ 3", "Task 12", "finding 2", "Decision 4", "Phase B", "Phase 2", "(D2a)"],
            "review-artifact": ["Qodo #1", "Bugbot", "CodeRabbit", "Codex review", "round-4 review", "round4 fix", "round 2 finding", "BLOCKER-1"],
            "change-narration": ["pre-fix", "an earlier revision", "an earlier version", "an earlier draft"],
        }
        for rule, examples in examples.items():
            for example in examples:
                with self.subTest(example=example):
                    self.assertEqual(self.found(".cs", "// " + example), [(1, rule)])

    def test_allowed_language(self):
        for text in ["a token the server no longer accepts", "previously accepted wire shape", "review round 2", "Task runner", "Phase encoding", "RFC 8628 §3.4"]:
            with self.subTest(text=text):
                self.assertEqual(self.found(".cs", "// " + text), [])
        self.assertEqual(self.found(".cs", "// RFC 8628 §3.4; §4.2"), [(1, "spec-coordinate")])

    def test_c_comments_and_line_numbers(self):
        source = 'var x = 1; // §4\n/* first\n * Task 2\n */\n/// Qodo\n'
        for suffix in [".cs", ".c", ".h"]:
            self.assertEqual(self.found(suffix, source), [(1, "spec-coordinate"), (3, "spec-coordinate"), (5, "review-artifact")])

    def test_csharp_strings(self):
        strings = [
            '"// Qodo"', '"escaped \\" // Qodo"', '@"double "" // Qodo"',
            '$"// Qodo"', '$@"// Qodo"', '@$"double "" // Qodo"',
            '"""\n// Qodo\n"""', '$$"""\n// §4\n"""',
            '""""\n// Qodo with """ quotes\n""""', "'\"'", "'/'",
            '$"{Call("// Qodo")}"', '$@"{Call(@"// Qodo")}"',
            '$"{{ escaped }} {Call($"{Nested("// Qodo")}")}"',
        ]
        for value in strings:
            with self.subTest(value=value):
                self.assertEqual(self.found(".cs", 'var x = ' + value + ';\n// Task 3'), [(value.count("\n") + 2, "spec-coordinate")])
        self.assertEqual(self.found(".cs", '$"{Call(/* §4 */ 1)}"'), [(1, "spec-coordinate")])

    def test_xml_comments_and_cdata(self):
        for suffix in [".axaml", ".csproj", ".props", ".targets"]:
            self.assertEqual(self.found(suffix, '<![CDATA[<!-- Qodo -->]]>\n<X Value="Task 2"/><!--\n§4\n-->'), [(3, "spec-coordinate")])

    def test_shell_comments_and_data(self):
        source = "echo '# Qodo' # §4\ncat <<'END'\n# Task 2\nEND\n# Finding 3\n"
        self.assertEqual(self.found(".sh", source), [(1, "spec-coordinate"), (5, "spec-coordinate")])
        self.assertEqual(self.found(".sh", 'echo "multi\n# Qodo\nline"\n# Task 2'), [(4, "spec-coordinate")])
        self.assertEqual(self.found(".sh", 'cat <<-END\n\t# Qodo\n\tEND\n# Task 2'), [(4, "spec-coordinate")])
        self.assertEqual(self.found(".sh", 'cat <<A <<"B"\n# Qodo\nA\n# Qodo\nB\n# Task 2'), [(6, "spec-coordinate")])

    def test_shell_here_strings_and_arithmetic(self):
        self.assertEqual(self.found(".sh", 'cat <<< word\n# Task 2'), [(2, "spec-coordinate")])
        self.assertEqual(self.found(".sh", '(( bits = value << shift ))\n# Task 2'), [(2, "spec-coordinate")])

    def test_yaml_comments_and_data(self):
        for suffix in [".yaml", ".yml"]:
            source = 'value: "# Qodo" # Task 2\ntext: |\n  # Qodo\nnext: \'it\'\'s # Task 3\'\n# §4'
            self.assertEqual(self.found(suffix, source), [(1, "spec-coordinate"), (5, "spec-coordinate")])
        self.assertEqual(self.found(".yml", 'value: >- # Qodo\n  # Task 2\n# §4'), [(1, "review-artifact"), (3, "spec-coordinate")])

    def test_python_comments_and_data(self):
        self.assertEqual(self.found(".py", 'text = "# Qodo"\ntext = """\n# Task 2\n"""\n# §4'), [(5, "spec-coordinate")])


class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="comment-rules-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1")
        self.git("init", "-q")
        self.git("config", "user.name", "Guard tests")
        self.git("config", "user.email", "guard@example.invalid")
        self.write(checker.BASELINE, "[]\n")

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, env=self.env, check=True, stdout=subprocess.PIPE).stdout.decode().strip()

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        self.git("add", "--", name)

    def run_check(self, want, *args, wrapper="check-comment-rules.sh", cwd=None):
        result = subprocess.run(["bash", str(scripts / wrapper), *args], cwd=cwd or self.root, env=self.env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.assertEqual(result.returncode, want, result.stdout)
        return result.stdout

    def seed(self):
        (self.root / checker.BASELINE).unlink()
        self.run_check(0, "--write-baseline")
        self.git("add", checker.BASELINE)

    def test_every_extension_and_directory(self):
        for suffix in sorted(checker.EXTENSIONS):
            with self.subTest(suffix=suffix):
                comment = "<!-- §4 -->" if suffix in {".axaml", ".csproj", ".props", ".targets"} else "// §4" if suffix in {".cs", ".c", ".h"} else "# §4"
                name = "nested/path/file" + suffix
                self.write(name, comment + "\n")
                self.assertIn(name + ":1: spec-coordinate:", self.run_check(1))
                self.git("rm", "-qf", "--", name)

    def test_filename_quoting_color_and_subdirectory(self):
        self.git("config", "color.grep", "always")
        name = 'src/é weird:line\\file.cs'
        self.write(name, "// Qodo\n")
        self.assertIn(name + ":1: review-artifact:", self.run_check(1, cwd=self.root / "src"))

    def test_baseline_line_movement_and_duplicates(self):
        self.write("src/a.cs", "// §4\n")
        self.seed()
        self.write("src/a.cs", "\n// §4\n")
        self.run_check(0)
        self.write("src/a.cs", "\n// §4\n// §4\n")
        self.assertIn("src/a.cs:3: spec-coordinate:", self.run_check(1))
        before = (self.root / checker.BASELINE).read_bytes()
        self.run_check(1, "--write-baseline")
        self.assertEqual((self.root / checker.BASELINE).read_bytes(), before)

    def test_baseline_changed_comment(self):
        self.write("src/a.cs", "// §4\n")
        self.seed()
        self.write("src/a.cs", "// §4 updated\n")
        self.assertIn("src/a.cs:1: spec-coordinate:", self.run_check(1))

    def test_baseline_pruning_and_deleted_files(self):
        self.write("src/a.cs", "// §4\n")
        self.seed()
        self.git("rm", "-qf", "src/a.cs")
        self.assertIn("stale entries", self.run_check(1))
        self.run_check(0, "--write-baseline")
        self.assertEqual(json.loads((self.root / checker.BASELINE).read_text()), [])
        self.run_check(0)

    def test_baseline_growth_in_pr(self):
        self.git("commit", "-qm", "Seed baseline")
        base = self.git("rev-parse", "HEAD")
        self.write("src/a.cs", "// §4\n")
        self.seed()
        self.assertIn("baseline may only shrink", self.run_check(1, "--baseline-ref", base))

    def test_baseline_count_growth_in_pr(self):
        self.write("src/a.cs", "// §4\n")
        self.seed()
        self.git("commit", "-qm", "Seed baseline")
        base = self.git("rev-parse", "HEAD")
        self.write("src/a.cs", "// §4\n// §4\n")
        rows = json.loads((self.root / checker.BASELINE).read_text())
        rows[0]["count"] = 2
        self.write(checker.BASELINE, json.dumps(rows))
        self.assertIn("baseline may only shrink", self.run_check(1, "--baseline-ref", base))

    def test_baseline_shrinks_in_pr(self):
        self.write("src/a.cs", "// §4\n")
        self.seed()
        self.git("commit", "-qm", "Seed baseline")
        base = self.git("rev-parse", "HEAD")
        self.write("src/a.cs", "// The caller holds the lock.\n")
        self.run_check(0, "--write-baseline", "--baseline-ref", base)
        self.run_check(0, "--baseline-ref", base)

    def test_initial_baseline_and_unknown_ref(self):
        self.git("rm", "-qf", checker.BASELINE)
        self.write("src/a.cs", "// §4\n")
        self.git("commit", "-qm", "Before baseline")
        base = self.git("rev-parse", "HEAD")
        self.run_check(2)
        self.run_check(0, "--write-baseline", "--baseline-ref", base)
        self.run_check(0, "--baseline-ref", base)
        self.run_check(2, "--baseline-ref", "0" * 40)
        self.run_check(2, "--baseline-ref", "--invalid")

    def test_invalid_baseline(self):
        for invalid in ['{', '{}', '[{"path": "x"}]', '[{"path": "x", "rule": "spec-coordinate", "comment": "§4", "count": -1}]']:
            with self.subTest(invalid=invalid):
                self.write(checker.BASELINE, invalid)
                self.run_check(2)

    def test_git_and_read_failures(self):
        with tempfile.TemporaryDirectory() as outside:
            self.run_check(2, cwd=outside)
        self.write("src/a.cs", "// §4\n")
        (self.root / "src/a.cs").unlink()
        self.run_check(2)

    def test_tokenizer_failure(self):
        self.write("scripts/broken.py", "if True:\n  x = 1\n x = 2\n")
        self.run_check(2)

    def test_symlinks_and_invalid_encoding(self):
        self.write("src/a.cs", "")
        (self.root / "src/a.cs").unlink()
        (self.root / "src/a.cs").symlink_to("missing")
        self.run_check(2)
        (self.root / "src/a.cs").unlink()
        (self.root / "src/a.cs").write_bytes(b"\xff")
        self.run_check(2)

    def test_linear_id_coverage_and_boundaries(self):
        identifier = "AI-" + "1234"
        for suffix in sorted(checker.EXTENSIONS):
            with self.subTest(suffix=suffix):
                name = "nested/file" + suffix
                self.write(name, '"prefix-' + identifier + '"\n')
                for wrapper in ["check-comment-rules.sh", "check-linear-ids.sh"]:
                    self.assertIn(name + ":1: linear-id:", self.run_check(1, wrapper=wrapper))
                self.git("rm", "-qf", "--", name)
        self.write("src/a.cs", '"x' + identifier + ' ' + identifier + '_ ' + identifier + 'x"')
        self.run_check(0)

    def test_linear_suppression_scope(self):
        identifier = "AI-" + "1234"
        self.write("test/a.cs", 'var x = "' + identifier + '"; // linear-id-ok: synthetic input')
        self.run_check(0)
        for name in ["src/a.cs", "test/a.sh", "scripts/a.sh", "test/a.cs"]:
            with self.subTest(name=name):
                reason = "" if name == "test/a.cs" else "synthetic input"
                self.write(name, '// ' + identifier + ' // linear-id-ok: ' + reason)
                self.run_check(1, wrapper="check-linear-ids.sh")
                self.git("rm", "-qf", "--", name)
        self.write("test/linear-id-ok: synthetic name.cs", '// ' + identifier)
        self.run_check(1, wrapper="check-linear-ids.sh")

    def test_reference_material_and_untracked_files(self):
        self.write("docs/superpowers/specs/a.sh", "# §4\n# AI-" + "1234")
        self.write("notes.md", "# §4\n")
        (self.root / "untracked.cs").write_text("// §4\n")
        self.run_check(0)


unittest.main(verbosity=1)
PY
