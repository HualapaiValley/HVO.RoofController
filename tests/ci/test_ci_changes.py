"""build/ci-changes.py: which pull-request CI jobs a change can affect, and the checks that keep its rules true."""
import ast
import importlib.util
import io
import re
import subprocess
import tempfile
import textwrap
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ci_changes", ROOT / "build" / "ci-changes.py")
ci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci)

P = "HVO.RoofControllerV4."
EVERY = set(ci.JOBS)


def name_status(*changes):
    return "".join(f"{status}\0{path}\0" for status, path in changes)


def run_main(*arguments):
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = ci.main(list(arguments))
    lines = dict(line.split("=", 1) for line in out.getvalue().splitlines())
    return code, lines, err.getvalue()


def decide(*changes, root=ROOT):
    return {job for job, run in ci.decide(list(changes), root).items() if run}


class Fixture:
    """A repository with src/ projects, their references and the two Dockerfiles."""

    def __init__(self, folder, projects, dockerfiles=None):
        self.root = Path(folder)
        for name, items in projects.items():
            directory = self.root / "src" / (P + name)
            directory.mkdir(parents=True)
            body = "".join(f'    <{kind} Include="{path}" />\n' for kind, path in items)
            (directory / f"{P}{name}.csproj").write_text(f"<Project>\n  <ItemGroup>\n{body}  </ItemGroup>\n</Project>\n")
        dockerfiles = dockerfiles or {
            "RPi": f'COPY ["src/{P}RPi/", "x/"]\n',
            "Emulator": f'COPY ["src/{P}Emulator/", "x/"]\n',
        }
        for name, text in dockerfiles.items():
            (self.root / "src" / (P + name)).mkdir(parents=True, exist_ok=True)
            (self.root / "src" / (P + name) / "Dockerfile").write_text(text)


def ref(name):
    return ("ProjectReference", f"../{P}{name}/{P}{name}.csproj")


SMALL = {
    "Common": [], "Client": [ref("Common")], "Screens": [ref("Client")], "Mac": [ref("Screens")],
    "Kiosk": [ref("Screens")], "RPi": [ref("Common")], "Emulator": [ref("Common")],
    "TerminalUi": [ref("Client")], "Cli": [ref("TerminalUi")], "Installer": [ref("TerminalUi")],
}


class Decisions(unittest.TestCase):
    """Against this repository's own projects and Dockerfiles."""

    def test_docs_alone_skip_everything_but_the_link_check(self):
        self.assertEqual({"deploy_script"}, decide(("M", "docs/install.md"), ("A", "docs/images/x.png"),
                                                   ("M", "CHANGELOG.md"), ("M", "README.md")))

    def test_test_code_alone_skips_the_jobs_that_neither_build_nor_run_it(self):
        self.assertEqual({"installer_kiosk", "installer_mac", "mac_cli", "terminal_distributions", "scenarios",
                          "browser"}, decide(("M", "tests/HVO.RoofControllerV4.RPi.Tests/Web/WebTests.cs")))

    def test_the_link_check_runs_for_markdown_in_the_test_project_and_for_any_deletion(self):
        self.assertIn("deploy_script", decide(("M", "tests/HVO.RoofControllerV4.RPi.Tests/README.md")))
        self.assertIn("deploy_script", decide(("D", "tests/HVO.RoofControllerV4.RPi.Tests/Web/Old.cs")))
        self.assertIn("deploy_script", decide(("M", "tests/HVO.RoofControllerV4.RPi.Tests/Web/WebTests.cs"),
                                              ("D", "docs/old.md")))

    def test_projects_follow_the_mac_apps_closure_and_the_images(self):
        installer = decide(("M", f"src/{P}Installer/Installer.cs"))
        self.assertEqual(EVERY - {"mac_app", "container_scenarios"}, installer)
        self.assertEqual(EVERY - {"container_scenarios", "installer_rig"}, decide(("M", f"src/{P}Mac/App.cs")))
        self.assertEqual(EVERY - {"mac_app", "container_scenarios", "installer_rig"},
                         decide(("M", f"src/{P}LinuxAuth/Program.cs")))
        self.assertEqual(EVERY - {"mac_app"}, decide(("M", f"src/{P}RPi/Program.cs")))
        self.assertEqual(EVERY, decide(("M", f"src/{P}Client/Client.cs")))

    def test_anything_else_runs_every_job(self):
        for path in [".github/workflows/ci.yml", "build/version.sh", "Directory.Build.props", "src/global.json",
                     "deploy/linux-auth/hvo-roof-local-auth", "tests/container/supervisor-tests.sh", "LICENSE",
                     "research/auth-poc/README.md", ".devcontainer/Dockerfile"]:
            with self.subTest(path=path):
                self.assertEqual(EVERY, decide(("M", "docs/x.md"), ("M", path)))

    def test_new_project_folders_count_for_jobs_that_follow_projects_only_once_referenced(self):
        # Not in any closure: the Mac app and the images do not build it, so their jobs skip it.
        self.assertEqual(EVERY - {"mac_app", "container_scenarios", "installer_rig"},
                         decide(("A", f"src/{P}Unreferenced/Thing.cs")))


class Graph(unittest.TestCase):
    """Against fixture repositories."""

    def assert_fallback(self, root, path):
        changes = root / "name-status"
        changes.write_text(name_status(("M", path)))
        code, lines, err = run_main("--name-status", str(changes), "--root", str(root))
        self.assertEqual(0, code)
        self.assertEqual(set(ci.JOBS), set(lines))
        self.assertEqual({"true"}, set(lines.values()))
        self.assertIn("every job runs", err)

    def test_single_quoted_resources_keep_the_mac_job_enabled(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Fixture(folder, dict(SMALL, LinuxAuth=[])).root
            project = root / "src" / (P + "Mac") / (P + "Mac.csproj")
            project.write_text(project.read_text().replace(
                "</ItemGroup>", f"<EmbeddedResource Include='../{P}LinuxAuth/Program.cs' /></ItemGroup>"))
            self.assertIn("mac_app", decide(("M", f"src/{P}LinuxAuth/Program.cs"), root=root))

    def test_external_imports_run_every_job(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Fixture(folder, SMALL).root
            (root / "build").mkdir()
            (root / "build" / "external.targets").write_text(
                '<Project><ItemGroup><EmbeddedResource Include="../../docs/mac.md" /></ItemGroup></Project>')
            project = root / "src" / (P + "Mac") / (P + "Mac.csproj")
            project.write_text(project.read_text().replace(
                "</Project>", '<Import Project="../../build/external.targets" /></Project>'))
            self.assert_fallback(root, "docs/mac.md")

    def test_imports_outside_the_image_and_mac_closures_run_every_job(self):
        # Scenario/browser jobs build the test graph too, including projects outside the three skip closures.
        with tempfile.TemporaryDirectory() as folder:
            root = Fixture(folder, dict(SMALL, LinuxAuth=[])).root
            project = root / "src" / (P + "LinuxAuth") / (P + "LinuxAuth.csproj")
            project.write_text(project.read_text().replace(
                "</Project>", '<Import Project="../../build/external.targets" /></Project>'))
            self.assert_fallback(root, "docs/mac.md")

    def test_shared_build_inputs_run_every_job_even_when_conditioned(self):
        for parent in ("", "src"):
            with self.subTest(parent=parent), tempfile.TemporaryDirectory() as folder:
                root = Fixture(folder, dict(SMALL, LinuxAuth=[])).root
                (root / parent / "Directory.Build.targets").write_text(
                    f'''<Project><ItemGroup Condition="'$(MSBuildProjectName)' == '{P}Mac'">
                    <EmbeddedResource Include="../{P}LinuxAuth/Program.cs" /></ItemGroup></Project>''')
                self.assert_fallback(root, f"src/{P}LinuxAuth/Program.cs")

    def test_symlinked_project_inputs_run_every_job(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Fixture(folder, dict(SMALL, LinuxAuth=[])).root
            (root / "src" / (P + "LinuxAuth") / "Program.cs").write_text("// shared input\n")
            (root / "src" / (P + "Mac") / "Shared.cs").symlink_to(f"../{P}LinuxAuth/Program.cs")
            self.assert_fallback(root, f"src/{P}LinuxAuth/Program.cs")

    def test_broad_or_unhandled_docker_context_copies_run_every_job(self):
        for source in ("src/", ".", "./src/", "src/./", "src/../", "/src/", '"src/"'):
            with self.subTest(source=source), tempfile.TemporaryDirectory() as folder:
                root = Fixture(folder, SMALL).root
                dockerfile = root / ci.DOCKERFILES[0]
                dockerfile.write_text(dockerfile.read_text() + f"COPY {source} /review-source/\n")
                self.assert_fallback(root, f"src/{P}Mac/Program.cs")

    def test_unsupported_docker_escape_directives_run_every_job(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Fixture(folder, SMALL).root
            dockerfile = root / ci.DOCKERFILES[0]
            dockerfile.write_text("# escape=`\n" + dockerfile.read_text() + "COPY `\nsrc/ /review-source/\n")
            self.assert_fallback(root, f"src/{P}Mac/Program.cs")

    def test_default_bind_mounts_run_every_job(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Fixture(folder, SMALL).root
            dockerfile = root / ci.DOCKERFILES[0]
            dockerfile.write_text(dockerfile.read_text() +
                f"RUN --mount=source=src/{P}Mac,target=/review-source cat /review-source/Program.cs\n")
            self.assert_fallback(root, f"src/{P}Mac/Program.cs")

    def sets(self, projects, dockerfiles=None):
        with tempfile.TemporaryDirectory() as folder:
            fixture = Fixture(folder, projects, dockerfiles)
            return {name: {f.removeprefix(P) for f in found} for name, found in ci.project_sets(fixture.root).items()}

    def test_closures_follow_references_and_items_outside_the_project(self):
        projects = dict(SMALL, Installer=[ref("TerminalUi"), ("EmbeddedResource", f"../{P}Kiosk/deploy/x.service")])
        sets = self.sets(projects)
        self.assertEqual({"Mac", "Screens", "Client", "Common"}, sets["mac"])
        self.assertEqual({"RPi", "Emulator", "Common"}, sets["images"])
        self.assertEqual({"RPi", "Emulator", "Common", "Installer", "Cli", "TerminalUi", "Client", "Kiosk", "Screens"},
                         sets["rig"])

    def test_a_new_reference_moves_a_project_into_the_mac_apps_closure(self):
        self.assertNotIn("Kiosk", self.sets(SMALL)["mac"])
        self.assertIn("Kiosk", self.sets(dict(SMALL, Screens=[ref("Client"), ref("Kiosk")]))["mac"])

    def test_dockerfile_copies_in_shell_and_json_form_with_flags_and_continuations(self):
        dockerfiles = {
            "RPi": textwrap.dedent(f"""\
                FROM base AS build
                # COPY ["src/{P}Mac/", "ignored/"]
                COPY ["Directory.Build.props", "src/global.json", "/repo/"]
                COPY --chmod=0755 src/{P}Kiosk/x.sh \\
                    /x.sh
                COPY --from=build /app/publish .
                COPY ["src/{P}RPi/", "x/"]
                """),
            "Emulator": f"ADD ./src/{P}Emulator x/\n",
        }
        self.assertEqual({"RPi", "Emulator", "Common", "Kiosk", "Screens", "Client"},
                         self.sets(SMALL, dockerfiles)["images"])

    def test_what_the_rules_cannot_follow_is_unknown(self):
        cases = {
            "property": dict(SMALL, Mac=[("Compile", "$(Shared)/../x.cs")]),
            "property alone": dict(SMALL, Mac=[("EmbeddedResource", "$(RepositoryRoot)docs/mac.md")]),
            "absolute": dict(SMALL, Mac=[("None", "/etc/passwd")]),
            "docs": dict(SMALL, Mac=[("EmbeddedResource", "../../docs/mac.md")]),
            "test code": dict(SMALL, Mac=[("None", "../../tests/HVO.RoofControllerV4.RPi.Tests/x.json")]),
            "outside": dict(SMALL, Mac=[("None", "../../../elsewhere.txt")]),
            "missing": dict(SMALL, Mac=[ref("Gone")]),
        }
        for name, projects in cases.items():
            with self.subTest(name), self.assertRaises(ci.Unknown):
                self.sets(projects)
        for name, text in {"bind": "RUN --mount=type=bind,source=docs,target=/d true\n",
                           "variable": "COPY ${SRC} x/\n", "docs": "COPY docs/x.md x/\n"}.items():
            with self.subTest(name), self.assertRaises(ci.Unknown):
                self.sets(SMALL, {"RPi": text, "Emulator": f'COPY ["src/{P}Emulator/", "x/"]\n'})


class Command(unittest.TestCase):
    def test_lines_for_every_job_and_the_output_file(self):
        with tempfile.TemporaryDirectory() as folder:
            changes, output, summary = Path(folder, "changes"), Path(folder, "output"), Path(folder, "summary")
            changes.write_text(name_status(("M", "docs/install.md")))
            code, lines, _ = run_main("--name-status", str(changes), "--root", str(ROOT),
                                      "--github-output", str(output), "--summary", str(summary))
            self.assertEqual(0, code)
            self.assertEqual(list(ci.JOBS), list(lines))
            self.assertEqual("true", lines["deploy_script"])
            self.assertEqual("false", lines["scenarios"])
            self.assertEqual(output.read_text(), "".join(f"{k}={v}\n" for k, v in lines.items()))
            self.assertIn("| scenarios | skipped |", summary.read_text())

    def test_anything_it_cannot_work_out_runs_every_job(self):
        with tempfile.TemporaryDirectory() as folder:
            bad = Path(folder, "changes")
            for content in ["", "M\0", "R100\0a\0b\0", "?\0x\0"]:
                with self.subTest(content=content):
                    bad.write_text(content)
                    code, lines, err = run_main("--name-status", str(bad), "--root", str(ROOT))
                    self.assertEqual(0, code)
                    self.assertEqual({"true"}, set(lines.values()))
                    self.assertIn("every job runs", err)
            code, lines, err = run_main("--name-status", str(Path(folder, "missing")))
            self.assertEqual((0, {"true"}), (code, set(lines.values())))

    def test_a_base_it_cannot_read_runs_every_job(self):
        code, lines, err = run_main("--base", "0" * 40, "--root", str(ROOT))
        self.assertEqual((0, {"true"}), (code, set(lines.values())))
        code, lines, err = run_main("--base", "main; rm -rf /", "--root", str(ROOT))
        self.assertEqual((0, {"true"}), (code, set(lines.values())))
        self.assertIn("not a full commit SHA", err)

    def test_a_real_diff(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*arguments):
                return subprocess.run(["git", "-C", folder, "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                                       *arguments], check=True, capture_output=True, text=True).stdout.strip()
            git("init", "-q")
            Fixture(folder, SMALL)
            Path(folder, "docs").mkdir()
            Path(folder, "docs", "a.md").write_text("a\n")
            git("add", "-A")
            git("commit", "-q", "-m", "base")
            base = git("rev-parse", "HEAD")
            Path(folder, "docs", "a.md").write_text("b\n")
            git("commit", "-q", "-am", "docs")
            code, lines, _ = run_main("--base", base, "--root", folder)
            self.assertEqual({"deploy_script"}, {job for job, run in lines.items() if run == "true"})


class RulesStayTrue(unittest.TestCase):
    """Tripwires for the rules build/ci-changes.py cannot derive: each fails when a job starts to read a class of
    files it is said not to. Update the rules (and docs/ci-runners.md) before allowing a new reader."""

    def test_this_repositorys_projects_and_dockerfiles_can_be_followed(self):
        sets = ci.project_sets(ROOT)
        self.assertLessEqual({P + "Mac", P + "Common"}, sets["mac"])
        self.assertLessEqual({P + "RPi", P + "Web", P + "Emulator"}, sets["images"])
        self.assertLessEqual(sets["images"] | {P + "Installer", P + "Cli"}, sets["rig"])

    def test_only_unit_tests_read_docs(self):
        # The test classes that read docs/ run only in build-and-test; the jobs that skip docs select theirs by
        # category or by class name.
        selected = re.compile(r"TestCategory\((Scenario\.\w+|Category|\"(MacInstall|KioskInstall|TerminalPublished)\")\)"
                              r"|class (InstallerRegularFilesTests|InstallerMacCertificateImportTests|"
                              r"RoofCertificateAuthorityTests|RoofCertificatePinTests|RoofCliCaCertificateTests|"
                              r"TerminalInstalledNoticeTests)\b|ControlledDriver")
        reads_docs = re.compile(r'"docs"|"docs/|"\.\./docs|docs/[\w./-]+\.(md|json|svg|png)"')
        project = ROOT / "tests" / "HVO.RoofControllerV4.RPi.Tests"
        for path in sorted(project.rglob("*.cs")):
            if {"bin", "obj"} & set(path.relative_to(project).parts):
                continue
            text = path.read_text(encoding="utf-8")
            if reads_docs.search(text):
                with self.subTest(path=path.relative_to(ROOT).as_posix()):
                    self.assertIsNone(selected.search(text), "a test a docs-skipping job runs reads docs/")

    def test_no_script_reads_the_test_project_but_the_terminal_workflow_check(self):
        listed = subprocess.run(["git", "-C", str(ROOT), "ls-files", "-z"], check=True, capture_output=True).stdout
        allowed = {".github/workflows/ci.yml", ".github/workflows/scenarios.yml", "build/ci-changes.py",
                   "tests/ci/test_ci_changes.py",
                   # terminal-distributions, which never skips for test code, runs it.
                   "tests/releasing/check-terminal-workflows.py"}
        found = set()
        for name in listed.decode().split("\0"):
            if (not name or name.startswith(("src/", "tests/HVO.RoofControllerV4.RPi.Tests/"))
                    or name.lower().endswith(".md")):
                continue  # src/ names the test assembly (InternalsVisibleTo); a build reading it is caught above
            path = ROOT / name
            if path.is_file() and not path.is_symlink():
                try:
                    if "RPi.Tests" in path.read_text(encoding="utf-8"):
                        found.add(name)
                except UnicodeDecodeError:
                    pass
        self.assertEqual(set(), found - allowed)

    DOCS_SKIPPING_SHELL = ["tests/emulator/*.sh", "tests/installer/rig-scenario.sh", "tests/install/install-sh-tests.sh",
                           "tests/install/fakes/*", "tests/install/on-terminal", "tests/cli/*.sh"]
    DOCS_SKIPPING_PYTHON = ["tests/releasing/offline-archives-from-registry.py", "tests/installer/bootstrap_state.py",
                            "build/release-assets.py", "build/release-compose.py", "build/terminal_payload.py",
                            "build/dotnet_bundle.py", "tests/cli/ansi-to-svg.py", "tests/releasing/published-terminal.py",
                            "tests/releasing/check-terminal-workflows.py", "tests/releasing/assemble-terminal-release.py",
                            "src/HVO.RoofControllerV4.Mac/bundle/bundle.py"]

    def test_the_scripts_of_docs_skipping_jobs_name_no_docs_path(self):
        for pattern in self.DOCS_SKIPPING_SHELL:
            for path in sorted(ROOT.glob(pattern)):
                for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
                    if not line.lstrip().startswith("#") and re.search(r"(?<![\w.-])docs/", line):
                        self.fail(f"{path.relative_to(ROOT)}:{number} names docs/: {line.strip()}")
        for name in self.DOCS_SKIPPING_PYTHON:
            tree = ast.parse((ROOT / name).read_text(encoding="utf-8"))
            docstrings = {id(node.body[0].value) for node in ast.walk(tree)
                          if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef))
                          and node.body and isinstance(node.body[0], ast.Expr)
                          and isinstance(node.body[0].value, ast.Constant)}
            for node in ast.walk(tree):
                # A path, not a message: no whitespace.
                if (isinstance(node, ast.Constant) and isinstance(node.value, str) and id(node) not in docstrings
                        and (node.value == "docs" or node.value.startswith("docs/"))
                        and not any(c.isspace() for c in node.value)):
                    self.fail(f"{name}:{node.lineno} names {node.value}")

    def test_ci_never_refreshes_the_documented_captures(self):
        # tests/cli/refresh-captures.py reads docs/images only with --docs; terminal-distributions runs it without.
        for workflow in (ROOT / ".github" / "workflows").glob("*.yml"):
            text = workflow.read_text(encoding="utf-8")
            for match in re.finditer(r"refresh-captures\.py(?:[^\n]|\\\n)*", text):
                self.assertNotIn("--docs", match.group(0), workflow.name)


if __name__ == "__main__":
    unittest.main()
