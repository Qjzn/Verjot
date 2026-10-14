import json
import unittest
from pathlib import Path

from tingji_config import parse_hotkey, validate_config
from meeting_worker import audio_link


class ConfigurationTests(unittest.TestCase):
    def validate(self, **changes):
        return validate_config({"vault_root": "D:\\Notes\\Example", **changes}, check_directory=False)

    def test_old_config_keeps_obsidian_and_local_defaults(self):
        config = self.validate()
        self.assertTrue(config["open_in_obsidian"])
        self.assertEqual(config["transcription_mode"], "local")
        self.assertEqual(config["pause_hotkey"], "Alt+Space")

    def test_new_example_explicitly_selects_plain_markdown(self):
        example = json.loads(Path(__file__).with_name("config.example.json").read_text(encoding="utf-8-sig"))
        self.assertFalse(validate_config(example, check_directory=False)["open_in_obsidian"])

    def test_unknown_keys_and_wrong_types_fail_early(self):
        for changes in ({"api_key": "do-not-save-secrets"}, {"open_in_obsidian": "false"},
                        {"asr_context": None}, {"api_timeout_seconds": True}):
            with self.subTest(changes=list(changes)), self.assertRaises(ValueError):
                self.validate(**changes)

    def test_absolute_windows_root_is_required(self):
        for root in ("notes", "\\notes", "C:notes", "/notes", ""):
            with self.subTest(root=root), self.assertRaises(ValueError):
                self.validate(vault_root=root)

    def test_directory_must_exist_in_runtime_validation(self):
        with self.assertRaises(ValueError):
            validate_config({"vault_root": "Z:\\tingji-uncreated-config-test-directory"})

    def test_unsafe_meeting_folders_are_rejected(self):
        for folder in ("../notes", "会议记录/../外部", "C:\\notes", "\\notes", "会议//记录",
                       "会议/./记录", "NUL", "AUX.txt", "会议.", "会议 ", "会议?", ""):
            with self.subTest(folder=folder), self.assertRaises(ValueError):
                self.validate(meeting_folder=folder)

    def test_nested_relative_folder_is_supported(self):
        self.assertEqual(self.validate(meeting_folder="工作/会议记录")["meeting_folder"], "工作/会议记录")

    def test_pause_hotkey_supports_space_and_function_keys(self):
        self.assertEqual(parse_hotkey("Alt+Space"), (1, 32))
        self.assertEqual(parse_hotkey("Ctrl+Shift+F24"), (6, 135))

    def test_duplicate_or_equivalent_shortcuts_are_rejected(self):
        for changes in ({"hotkey": "Ctrl+Ctrl+L"}, {"pause_hotkey": "Alt+Ctrl+L"},
                        {"hotkey": "Ctrl+F25"}, {"pause_hotkey": "Space"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.validate(**changes)

    def test_shortcut_trailing_newline_does_not_register_a_different_key(self):
        with self.assertRaises(ValueError):
            self.validate(pause_hotkey="Alt+Space\n")

    def test_api_requires_audio_provider_model_and_safe_url(self):
        for fields in ({}, {"api_base_url": "https://api.example/v1"},
                       {"api_base_url": "https://secret@api.example/v1", "api_model": "asr"},
                       {"api_base_url": "https://api.example/v1?key=secret", "api_model": "asr"},
                       {"api_base_url": " https://api.example/v1", "api_model": "asr"},
                       {"api_base_url": "https://api.example/bad path", "api_model": "asr"},
                       {"api_base_url": "https://[invalid/v1", "api_model": "asr"}):
            with self.subTest(fields=fields), self.assertRaises(ValueError):
                self.validate(transcription_mode="api", **fields)

    def test_api_invalid_port_is_rejected_before_recording(self):
        with self.assertRaises(ValueError):
            self.validate(transcription_mode="api", api_base_url="https://api.example:badport/v1", api_model="asr")

    def test_api_accepts_valid_config_but_no_plaintext_key_field(self):
        config = self.validate(transcription_mode="api", api_base_url="https://api.example/v1", api_model="provider-asr")
        self.assertEqual(config["api_key_env"], "TINGJI_API_KEY")
        for seconds in (9, 601):
            with self.assertRaises(ValueError):
                self.validate(api_timeout_seconds=seconds)

    def test_legacy_wiki_links_and_new_markdown_links_use_session_style(self):
        vault = Path("D:/Notes/Example")
        audio = vault / "会议记录" / "附件" / "session-123" / "meeting.wav"
        self.assertEqual(audio_link(audio, vault, {}), "![[会议记录/附件/session-123/meeting.wav]]")
        self.assertEqual(audio_link(audio, vault, {"storage_mode": "minimal", "note_link_style": "markdown"}),
                         "[会议录音](%E9%99%84%E4%BB%B6/session-123/meeting.wav)")
        with self.assertRaises(ValueError):
            audio_link(audio, vault, {"storage_mode": "minimal", "note_link_style": "unknown"})


if __name__ == "__main__":
    unittest.main()
