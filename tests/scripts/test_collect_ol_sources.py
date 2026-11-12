"""验证公开素材的解析、离线完整性及拒绝错误映射；不联网、不作规则验收。"""
import contextlib
import copy
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("collect_ol", ROOT / "scripts/collect-ol-sources.py")
COLLECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COLLECTOR)
CAPTURE = ROOT / "content/evidence/ol-public-20261112"


class CollectOlSourcesTests(unittest.TestCase):
    def test_all_downloaded_files_skills_and_atlas_coordinates_verify_offline(self):
        with contextlib.redirect_stdout(io.StringIO()):
            COLLECTOR.verify(CAPTURE)

    def test_inactive_skill_tab_with_trailing_space_is_not_lost(self):
        page = (CAPTURE / "official/hero-22.html").read_text(encoding="utf-8")
        self.assertEqual([s["name"] for s in COLLECTOR.parse_skills(page)], ["急救", "青囊"])
        with self.assertRaisesRegex(ValueError, "遗漏或错配"):
            COLLECTOR.parse_skills(page.replace('<div class="character-tab ">青囊</div>', ""))

    def test_altered_file_digest_is_rejected(self):
        catalog = copy.deepcopy(COLLECTOR.read_json(CAPTURE / "catalog.json"))
        catalog["files"][0]["sha256"] = "0" * 64
        original = COLLECTOR.read_json
        with patch.object(COLLECTOR, "read_json", side_effect=lambda p:
                          catalog if p.name == "catalog.json" else original(p)):
            with self.assertRaisesRegex(ValueError, "摘要或长度不符"):
                COLLECTOR.verify(CAPTURE)

    def test_out_of_bounds_atlas_frame_is_rejected(self):
        path = CAPTURE / "pc/res/assets/game/gameBase.atlas"
        atlas = COLLECTOR.read_json(path)
        next(iter(atlas["frames"].values()))["frame"]["x"] = 999999
        original = COLLECTOR.read_json
        with patch.object(COLLECTOR, "read_json", side_effect=lambda p:
                          atlas if p == path else original(p)):
            with self.assertRaisesRegex(ValueError, "图集坐标越界"):
                COLLECTOR.verify(CAPTURE)

    def test_changed_variant_mapping_is_rejected(self):
        catalog = COLLECTOR.read_json(CAPTURE / "catalog.json")
        catalog["generals"][0]["officialGeneralId"] = 999
        original = COLLECTOR.read_json
        with patch.object(COLLECTOR, "read_json", side_effect=lambda p:
                          catalog if p.name == "catalog.json" else original(p)):
            with self.assertRaisesRegex(ValueError, "标准武将属性映射"):
                COLLECTOR.verify(CAPTURE)

    def test_paths_cannot_escape_capture_or_follow_symlinks(self):
        for relative in ("../outside", "/tmp/outside", "folder/../../outside"):
            with self.subTest(relative=relative), self.assertRaises(ValueError):
                COLLECTOR.safe_path(CAPTURE, relative)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "link").symlink_to(CAPTURE, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, "符号链接"):
                COLLECTOR.safe_path(root, "link/catalog.json")


if __name__ == "__main__":
    unittest.main()
