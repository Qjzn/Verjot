import json
import unittest
from pathlib import Path

import tingji_config
from tingji_config import parse_hotkey, validate_config
from meeting_worker import audio_link


class ConfigurationTests(unittest.TestCase):
    def test_record_only_default_and_explicit_transcription(self):
        self.assertIs(self.validate()["auto_transcribe"], False)
        self.assertIs(self.validate(auto_transcribe=True, api_base_url="https://example.invalid/v1", api_model="asr")["auto_transcribe"], True)
        for value in ("true", 1, None):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.validate(auto_transcribe=value)

    def test_terms_are_explicit_deduplicated_and_frozen_per_meeting(self):
        config = self.validate(asr_context="产品会议", asr_terms="挚达，BOM\n挚达;OA")
        self.assertEqual(tingji_config.transcription_context(config), "产品会议\n术语：挚达、BOM、OA。")
        metadata = {"asr_context": "原提示", "asr_terms": "原术语"}
        self.assertEqual(tingji_config.transcription_context(config, metadata), "原提示\n术语：原术语。")
        self.assertEqual(tingji_config.transcription_context({"asr_context": "  old  "}), "  old  ")
        for changes in ({"asr_terms": "x" * 65}, {"asr_context": "x" * 2000, "asr_terms": "BOM"},
                        {"asr_segmentation": "auto"}, {"speaker_anchor_mode": "six"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError): self.validate(**changes)

    def test_quick_note_defaults_and_customization(self):
        config = self.validate()
        self.assertEqual(config["quick_note_hotkey"], "Ctrl+Alt+J")
        self.assertEqual(config["quick_note_folder"], "速记")
        custom = self.validate(quick_note_hotkey="Ctrl+Shift+F10", quick_note_folder="资料/速记")
        self.assertEqual(custom["quick_note_hotkey"], "Ctrl+Shift+F10")
        self.assertEqual(custom["quick_note_folder"], "资料/速记")

    def test_quick_note_conflicts_and_unsafe_paths_rejected(self):
        for changes in ({"quick_note_hotkey": "Alt+Ctrl+L"}, {"quick_note_hotkey": "Alt+Space"},
                        {"quick_note_hotkey": "Ctrl+Alt+K"}, {"quick_note_folder": "../outside"},
                        {"quick_note_folder": "C:\\outside"}, {"quick_note_folder": "NUL"},
                        {"quick_note_folder": "folder."}, {"quick_note_folder": ""}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.validate(**changes)

    def test_segmented_recording_and_mp3_defaults(self):
        config = self.validate()
        self.assertEqual(config["recording_segment_seconds"], 300)
        self.assertEqual(config["mp3_bitrate_kbps"], 192)
        self.assertEqual(self.validate(recording_segment_seconds=10)["recording_segment_seconds"], 10)
        self.assertEqual(self.validate(mp3_bitrate_kbps=320)["mp3_bitrate_kbps"], 320)

    def test_recording_protection_defaults_and_integer_boundaries(self):
        self.assertEqual(self.validate()["recording_protection_seconds"], 5)
        for seconds in (0, 1, 5, 300):
            with self.subTest(seconds=seconds):
                self.assertEqual(self.validate(recording_protection_seconds=seconds)["recording_protection_seconds"], seconds)
        example = json.loads(Path(tingji_config.__file__).with_name("config.example.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(example["recording_protection_seconds"], 5)

    def test_recording_protection_rejects_out_of_range_and_non_integer_values(self):
        for seconds in (-1, 301, 5.0, 0.5, "5", True, False, None, []):
            with self.subTest(seconds=seconds), self.assertRaises(ValueError):
                self.validate(recording_protection_seconds=seconds)

    def test_invalid_segment_limits_and_mp3_rates_rejected(self):
        for changes in ({"recording_segment_seconds": 9}, {"recording_segment_seconds": 601},
                        {"recording_segment_seconds": True}, {"recording_segment_seconds": "300"},
                        {"mp3_bitrate_kbps": 193}, {"mp3_bitrate_kbps": "192"}, {"mp3_bitrate_kbps": True}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.validate(**changes)

    def test_multi_device_capture_defaults_and_explicit_override(self):
        self.assertEqual(self.validate()["system_capture_mode"], "all_active")
        self.assertEqual(self.validate(system_capture_mode="default")["system_capture_mode"], "default")
        self.assertIs(self.validate()["asr_echo_filter"], True)
        self.assertIs(self.validate(asr_echo_filter=False)["asr_echo_filter"], False)
        for changes in ({"system_capture_mode": "all"}, {"system_capture_mode": True},
                        {"asr_echo_filter": "true"}, {"asr_echo_filter": 1}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.validate(**changes)

    def test_asr_denoise_defaults_to_light_and_can_be_disabled(self):
        self.assertEqual(self.validate()["asr_denoise"], "light")
        self.assertEqual(self.validate(asr_denoise="off")["asr_denoise"], "off")
        for value in (True, None, "aggressive", "LIGHT"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.validate(asr_denoise=value)

    def test_productivity_defaults_and_custom_shortcuts(self):
        config = self.validate()
        self.assertEqual(config["bookmark_hotkey"], "Ctrl+Alt+K")
        self.assertEqual(config["silence_warning_seconds"], 60)
        self.assertEqual(self.validate(bookmark_hotkey="Ctrl+Shift+F8")["bookmark_hotkey"], "Ctrl+Shift+F8")
        self.assertEqual(self.validate(silence_warning_seconds=0)["silence_warning_seconds"], 0)

    def test_bookmark_shortcut_conflicts_and_invalid_silence_limits(self):
        for changes in ({"bookmark_hotkey": "Alt+Ctrl+L"}, {"bookmark_hotkey": "Alt+Space"},
                        {"silence_warning_seconds": -1}, {"silence_warning_seconds": True},
                        {"silence_warning_seconds": 3601}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.validate(**changes)

    def validate(self, **changes):
        return validate_config({"vault_root": "D:\\Notes\\Example", **changes}, check_directory=False)

    def test_old_obsidian_preference_and_explicit_local_value_are_preserved(self):
        config = self.validate()
        self.assertTrue(config["open_in_obsidian"])
        self.assertEqual(config["transcription_mode"], "api")
        self.assertEqual(self.validate(transcription_mode="local")["transcription_mode"], "local")
        self.assertEqual(config["pause_hotkey"], "Alt+Space")

    def test_new_example_explicitly_selects_plain_markdown(self):
        example = json.loads(Path(tingji_config.__file__).with_name("config.example.json").read_text(encoding="utf-8-sig"))
        self.assertFalse(validate_config(example, check_directory=False)["open_in_obsidian"])

    def test_legacy_config_enables_lightweight_feedback_defaults(self):
        config = self.validate()
        for field in ("show_processing_window", "notify_device_changes", "auto_open_completed_note"):
            with self.subTest(field=field):
                self.assertIs(config[field], True)

    def test_animation_mode_defaults_to_system_and_allows_app_override(self):
        self.assertEqual(self.validate()["animation_mode"], "auto")
        for mode in ("auto", "on", "off"):
            with self.subTest(mode=mode):
                self.assertEqual(self.validate(animation_mode=mode)["animation_mode"], mode)

    def test_animation_mode_rejects_ambiguous_values(self):
        for value in (True, False, None, "true", "ON", "", 1):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.validate(animation_mode=value)

    def test_feedback_can_be_disabled_without_changing_storage_or_recording(self):
        changes = {field: False for field in
                   ("show_processing_window", "notify_device_changes", "auto_open_completed_note")}
        config = self.validate(**changes)
        self.assertTrue(config["open_in_obsidian"])
        self.assertEqual(config["hotkey"], "Ctrl+Alt+L")
        self.assertEqual(config["meeting_folder"], "会议记录")
        for field in changes:
            with self.subTest(field=field):
                self.assertIs(config[field], False)

    def test_feedback_fields_require_json_booleans(self):
        for field in ("show_processing_window", "notify_device_changes", "auto_open_completed_note"):
            for value in ("true", "false", 0, 1, None, []):
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    self.validate(**{field: value})

    def test_new_example_enables_all_three_feedback_options(self):
        example = json.loads(Path(tingji_config.__file__).with_name("config.example.json").read_text(encoding="utf-8-sig"))
        for field in ("show_processing_window", "notify_device_changes", "auto_open_completed_note"):
            with self.subTest(field=field):
                self.assertIs(example[field], True)
        validate_config(example, check_directory=False)

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
        self.assertFalse(self.validate(transcription_mode="api")["auto_transcribe"])
        with self.assertRaises(ValueError):
            self.validate(transcription_mode="api", auto_transcribe=True)
        for fields in ({"api_base_url": "https://api.example/v1"},
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
