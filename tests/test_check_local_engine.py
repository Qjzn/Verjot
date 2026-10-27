import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('local_check', ROOT / 'src/check_local_engine.py')
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)


class LocalEngineTests(unittest.TestCase):
    def test_fixed_sample_and_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in engine.FILES + ['models/segmentation/model.onnx']:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'fixture')
            closed = []
            speech = types.SimpleNamespace(read=lambda *a, **k: ([0.1] * 16000, 16000))
            class Backend:
                def __init__(self, *args):
                    self.args = args
                def transcribe(self, row):
                    self_row = Path(row['text_audio'])
                    if self_row != root / 'assets/engine-check.wav':
                        raise AssertionError('must only read bundled sample')
                    return '这是本地转写测试。'
                def close(self):
                    closed.append(True)
            modules = dict(soundfile=speech, asr_engine=types.SimpleNamespace(QwenBackend=Backend),
                           prepare_audio=types.SimpleNamespace(make_diarizer=lambda *a, **k: types.SimpleNamespace(process=lambda a: None)))
            with patch.dict(sys.modules, modules):
                result = engine.check(root)
                self.assertEqual(result['state'], 'ready')
                self.assertEqual(len(result['files']), 15)
                self.assertEqual(result['scope'], 'bundled-synthetic-sample-only')
                self.assertEqual(closed, [True])
                with patch.object(Backend, 'transcribe', return_value='无关输出'):
                    with self.assertRaisesRegex(RuntimeError, '预期内容'):
                        engine.check(root)
                (root / engine.FILES[6]).write_bytes(b'')
                with self.assertRaisesRegex(RuntimeError, '缺少本地转写文件'):
                    engine.check(root)

    def test_receipt_published_only_on_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(sys, 'argv', ['check', '--root', directory]), patch.object(engine, 'check', side_effect=RuntimeError('synthetic failure')):
                self.assertEqual(engine.main(), 1)
            self.assertFalse((root / 'installation/local-ready.json').exists())
            with patch.object(sys, 'argv', ['check', '--root', directory]), patch.object(engine, 'check', return_value=dict(state='ready')):
                self.assertEqual(engine.main(), 0)
            self.assertEqual(json.loads((root / 'installation/local-ready.json').read_text())['state'], 'ready')
            self.assertFalse(list(root.rglob('*.tmp')))


if __name__ == '__main__':
    unittest.main()
