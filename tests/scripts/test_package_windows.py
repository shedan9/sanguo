"""合成文件仅用于打包器回归，不是实际Windows运行证据。"""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location("package_windows", Path(__file__).resolve().parents[2] / "scripts/package-windows.py")
PACKAGER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGER)


class PackageWindowsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "build/windows"
        self.managed = self.output / "data_Sanguo.Client_windows_x86_64"
        self.managed.mkdir(parents=True)
        for name in ("Sanguo.Client.dll", "Sanguo.Application.dll", "Sanguo.BattleCore.dll", "Sanguo.CampaignCore.dll", "Sanguo.Content.dll", "Sanguo.Infrastructure.dll", "coreclr.dll", "hostfxr.dll"):
            (self.managed / name).write_bytes(b"synthetic-test-fixture")
        self.config = self.managed / "Sanguo.Client.runtimeconfig.json"
        self.config.write_text(json.dumps({"runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.15"}]}}))
        self.log = self.root / "build/evidence/g0/windows-export.log"
        self.log.parent.mkdir(parents=True)
        self.log.write_text("synthetic export success")
        (self.root / "scripts").mkdir()
        (self.root / "scripts/verify-windows.ps1").write_text("# synthetic script")
        (self.root / "third-party").mkdir()
        (self.root / "third-party/NOTICE.txt").write_text("synthetic license")
        executable = bytearray(256)
        executable[:2] = b"MZ"
        struct.pack_into("<I", executable, 0x3C, 128)
        executable[128:134] = b"PE\x00\x00\x64\x86"
        (self.output / "Sanguo.exe").write_bytes(executable)

    def package(self):
        with contextlib.redirect_stdout(io.StringIO()):
            PACKAGER.package(self.root)

    def test_archive_contains_verifier_and_every_manifest_digest_matches(self):
        self.package()
        # 再次打包也应稳定，不把旧清单纳入自身摘要。
        first = (self.output / "package-manifest.json").read_bytes()
        self.package()
        self.assertEqual(first, (self.output / "package-manifest.json").read_bytes())
        manifest = json.loads(first)
        self.assertFalse(manifest["testedOnWindows"])
        paths = {entry["path"] for entry in manifest["files"]}
        self.assertIn("verify-windows.ps1", paths)
        self.assertIn("licenses/NOTICE.txt", paths)
        for entry in manifest["files"]:
            data = (self.output / entry["path"]).read_bytes()
            self.assertEqual(len(data), entry["bytes"])
            self.assertEqual(hashlib.sha256(data).hexdigest(), entry["sha256"])
        with zipfile.ZipFile(self.root / "build/sanguo-g0-windows-x64.zip") as archive:
            self.assertEqual({"sanguo-g0-windows-x64/" + path for path in paths | {"package-manifest.json"}}, set(archive.namelist()))

    def test_rejects_truncated_executable(self):
        (self.output / "Sanguo.exe").write_bytes(b"MZ")
        with self.assertRaisesRegex(ValueError, "PE头"):
            self.package()

    def test_rejects_non_x64_executable(self):
        path = self.output / "Sanguo.exe"
        data = bytearray(path.read_bytes())
        data[132:134] = b"\x4c\x01"
        path.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "x64 PE"):
            self.package()

    def test_rejects_export_error_even_when_files_exist(self):
        self.log.write_text("ERROR: synthetic export failed")
        with self.assertRaisesRegex(ValueError, "导出日志"):
            self.package()

    def test_rejects_missing_runtime(self):
        (self.managed / "coreclr.dll").unlink()
        with self.assertRaisesRegex(ValueError, "缺少导出文件"):
            self.package()

    def test_rejects_non_self_contained_runtime(self):
        self.config.write_text('{"runtimeOptions":{"tfm":"net10.0"}}')
        with self.assertRaisesRegex(ValueError, "自包含"):
            self.package()

    def test_rejects_symlinks(self):
        (self.output / "linked.dll").symlink_to(self.managed / "coreclr.dll")
        with self.assertRaisesRegex(ValueError, "符号链接"):
            self.package()


if __name__ == "__main__":
    unittest.main()
