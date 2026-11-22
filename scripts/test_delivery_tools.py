import contextlib
import importlib.util
import io
import os
from pathlib import Path
import tempfile
import unittest


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parent / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


coverage = load("coverage_gate", "check-coverage.py")


class CoverageGateTests(unittest.TestCase):
    def report(self, directory, filename, hits, names=None):
        packages = []
        for name in names or sorted(coverage.PROJECTS):
            lines = "".join(f'<line number="{index + 1}" hits="{count}" />' for index, count in enumerate(hits))
            packages.append(f'<package name="{name}"><classes><class filename="/runner/src/{name}/Flow.cs"><lines>{lines}</lines></class>'
                            f'<class filename="/runner/src/{name}/obj/Generated.cs"><lines><line number="1" hits="0" /></lines></class></classes></package>')
        path = Path(directory) / filename
        path.write_text('<coverage><packages>' + ''.join(packages) + '</packages></coverage>')
        return path

    def test_union_complementary_suites_without_double_counting_or_generated_sources(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            first = self.report(directory, "unit.xml", [1, 0])
            second = self.report(directory, "integration.xml", [0, 1])
            result = coverage.collect([first, second, first])
            self.assertTrue(all(len(lines) == 2 and all(lines.values()) for lines in result.values()))
            self.assertEqual(coverage.main([first, second]), 0)

    def test_missing_module_or_below_threshold_fails(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            low = self.report(directory, "low.xml", [1, 0])
            missing = self.report(directory, "missing.xml", [1], ["Unrelated.Module"])
            self.assertEqual(coverage.main([low]), 1)
            self.assertEqual(coverage.main([missing]), 1)
            self.assertEqual(coverage.main([]), 1)
