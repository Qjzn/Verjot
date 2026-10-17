from datetime import datetime
import json
from pathlib import Path
import sys
import numpy as np
import soundfile as sf

home = Path(sys.argv[1])
config = json.loads((home / 'config.json').read_text(encoding='utf-8-sig'))
a, rate = sf.read(home / 'known-a.wav', dtype='float32')
b, second_rate = sf.read(home / 'known-b.wav', dtype='float32')
assert rate == second_rate == 16000
frames = rate * 3 + len(a) + len(b)
system = np.zeros(frames, dtype=np.float32)
mic = np.zeros(frames, dtype=np.float32)
system[rate:rate+len(a)] = a
mic[rate*2+len(a):rate*2+len(a)+len(b)] = b
session = home / 'work' / ('new-user-synthetic-' + datetime.now().strftime('%Y%m%d-%H%M%S'))
session.mkdir(parents=True)
sources = []
for key, samples, kind in [('system_01', system, 'loopback'), ('mic', mic, 'microphone')]:
    parts = []
    for index, start in enumerate(range(0, frames, rate * 7)):
        chunk = samples[start:start+rate*7]
        name = f'{key}.seg{index+1:06d}.wav'
        sf.write(session / name, chunk, rate, subtype='PCM_16')
        parts.append({'path': name, 'start_frame': start, 'frames': len(chunk)})
    sources.append({'source': key, 'path': key+'.wav', 'kind': kind,
                    'sample_rate': rate, 'channels': 1, 'bits_per_sample': 16, 'block_align': 2, 'segments': parts})
output = Path(config['vault_root']) / config['meeting_folder'] / '附件' / session.name
metadata = {'state': 'recorded', 'start_time': datetime.now().astimezone().isoformat(),
            'duration_seconds': frames/rate, 'storage_mode': 'minimal',
            'note_link_style': 'markdown',
            'audio_format': 'mp3', 'mp3_bitrate_kbps': 192, 'audio_output_dir': str(output),
            'transcription_mode': config['transcription_mode'], 'transcription_model': config['api_model'] if config['transcription_mode'] == 'api' else 'Qwen3-ASR-1.7B',
            'transcription_api_base_url': config['api_base_url'], 'sources': sources,
            'asr_denoise': 'light', 'asr_echo_filter': True}
(session/'session.json').write_text(json.dumps(metadata,ensure_ascii=False),encoding='utf-8')
(home/'validation-session.txt').write_text(str(session),encoding='utf-8')
print(json.dumps({'session':str(session),'seconds':frames/rate,'sources':len(sources),'segments':sum(len(s['segments']) for s in sources)}))
