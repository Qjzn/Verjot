"""Offline smoke test using only the bundled synthetic Mandarin sample.

Never opens work/, user recordings, the microphone, or an output audio device.
The receipt certifies this fixed smoke test, not real meeting accuracy.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import time
import uuid

FILES = [
    '.venv/Scripts/python.exe', 'check_local_engine.py', 'asr_engine.py', 'prepare_audio.py',
    'assets/engine-check.wav', 'models/Qwen3-ASR-1.7B-hf/config.json',
    'models/Qwen3-ASR-1.7B-hf/model.safetensors',
    'models/Qwen3-ASR-1.7B-hf/processor_config.json',
    'models/Qwen3-ASR-1.7B-hf/tokenizer_config.json', 'models/Qwen3-ASR-1.7B-hf/tokenizer.json',
    'models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx',
    '.venv/Lib/site-packages/torch/__init__.py',
    '.venv/Lib/site-packages/transformers/__init__.py',
    '.venv/Lib/site-packages/sherpa_onnx/__init__.py',
]


def ordinary(root, relative):
    path = root / relative
    if not path.is_file() or not path.stat().st_size:
        raise RuntimeError('缺少本地转写文件：' + relative)
    for parent in (path, *path.parents):
        if parent.is_symlink() or (hasattr(parent, 'is_junction') and parent.is_junction()):
            raise RuntimeError('自检路径不能经过符号链接或重解析目录')
        if parent == root:
            break
    return path


def check(root):
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
    os.environ.setdefault('OMP_NUM_THREADS', '4')
    os.environ.setdefault('MKL_NUM_THREADS', '4')
    root = root.absolute()
    segmentation = next((name for name in [
        'models/segmentation/model.onnx',
        'models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx',
        'models/sherpa-onnx-pyannote-segmentation-3-0/model.int8.onnx',
    ] if (root / name).is_file()), None)
    if not segmentation:
        raise RuntimeError('缺少说话人分段模型')
    manifest = []
    for name in FILES + [segmentation]:
        stat = ordinary(root, name).stat()
        manifest.append(dict(path=name, size=stat.st_size, mtime_ns=stat.st_mtime_ns))
    import soundfile as sf
    from asr_engine import QwenBackend
    from prepare_audio import make_diarizer
    diarizer = make_diarizer(root / segmentation, root / FILES[10], threads=2)
    sample = root / 'assets/engine-check.wav'
    audio, rate = sf.read(sample, dtype='float32')
    # Exercise both local speaker models, not just their file existence.
    diarizer.process(audio)
    del diarizer
    backend = QwenBackend(root / 'models/Qwen3-ASR-1.7B-hf', 'cpu', '', 128)
    try:
        text = backend.transcribe(dict(text_audio=str(sample), start=0, end=len(audio) / rate))
    finally:
        backend.close()
    normalized = re.sub(r'[^\u4e00-\u9fff]', '', text)
    if not all(word in normalized for word in ('本地', '转写', '测试')):
        raise RuntimeError('内置中文样本未识别出预期内容，请查看本地自检日志')
    return dict(version=1, state='ready', checked_at=time.time(), files=manifest,
                sample_text=text, scope='bundled-synthetic-sample-only')


def main():
    # Redirected stdout on English Windows may default to cp1252. A status
    # message must not turn successful inference/receipt publication into failure.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8', errors='backslashreplace')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    args = parser.parse_args()
    root = Path(args.root).absolute()
    receipt = root / 'installation/local-ready.json'
    temporary = receipt.with_suffix('.' + uuid.uuid4().hex + '.tmp')
    try:
        result = check(root)
        receipt.parent.mkdir(parents=True, exist_ok=True)
        temporary.write_text(json.dumps(result, ensure_ascii=False), encoding='utf-8')
        os.replace(temporary, receipt)
        print('内置中文转写与说话人组件试运行通过；不代表真实会议质量验收。')
        return 0
    except Exception as error:
        print('本地转写自检失败：' + str(error), file=sys.stderr)
        return 1
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == '__main__':
    raise SystemExit(main())
