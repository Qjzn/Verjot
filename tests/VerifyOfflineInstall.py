import json
import hashlib
from pathlib import Path
import re
import sys
import unicodedata
import numpy as np
import soundfile as sf
from scipy.signal import correlate, resample_poly

home = Path(sys.argv[1])
sys.path.insert(0, str(home))
from mp3_audio import validate_mp3
session = Path((home/'validation-session.txt').read_text(encoding='utf-8'))
status = json.loads((session/'status.json').read_text(encoding='utf-8-sig'))
metadata = json.loads((session/'session.json').read_text(encoding='utf-8-sig'))
audio = Path(status['published_audio']['path'])
note = Path(status['note_path'])
export = json.loads((home/'new-user-export-report.json').read_text(encoding='utf-8-sig'))
assert metadata['audio_export_version'] == export['audio_export_version'] == 1
assert hashlib.sha256(audio.read_bytes()).hexdigest() == export['mp3_sha256'], 'ASR must reuse the original native MP3'
a, rate = sf.read(home/'known-a.wav', dtype='float32')
b, _ = sf.read(home/'known-b.wav', dtype='float32')
expected_frames = rate*3 + len(a)+len(b)
mp3 = validate_mp3(audio, frames=expected_frames*3, bitrate_kbps=192)
decoded, decoded_rate = sf.read(audio, dtype='float32', always_2d=True)
mono = resample_poly(decoded.mean(axis=1), 1, 3)
positions = []
for name, sample, expected in [('system', a, rate), ('mic', b, rate*2+len(a))]:
    offset = int(np.argmax(correlate(mono, sample, mode='valid', method='fft')))
    positions.append({'source':name,'expected_frame':expected,'decoded_frame':offset,
                      'offset_ms':(offset-expected)*1000/rate})
text = note.read_text(encoding='utf-8')
rows = re.findall(r'^\*\*\[[^\n]+\*\*\n([^\n]+)', text, flags=re.M)
reference = '今天讨论产品发布。录音文件和会议转录保存在本地。请在周五下午三点前完成验证。我来补充两点。首次打开请先选择保存目录，快捷键可以在设置中修改。'
def normalize(value):
    return ''.join(c for c in value if not c.isspace() and not unicodedata.category(c).startswith('P'))
ref, hypothesis = normalize(reference), normalize(''.join(rows))
previous = list(range(len(hypothesis)+1))
for i, char in enumerate(ref, 1):
    current = [i]
    for j, other in enumerate(hypothesis, 1):
        current.append(min(current[-1]+1, previous[j]+1, previous[j-1]+(char != other)))
    previous = current
assert status['state']=='completed' and status['result_complete'] and status['artifacts_compacted']
assert not list(session.glob('*.wav')) and not list(session.glob('clips/*.wav'))
assert sorted(p.name for p in audio.parent.iterdir() if p.is_file()) == ['meeting.mp3']
assert rows and previous[-1] / len(ref) <= .08, 'Known Mandarin sample exceeds 8% character error rate'
assert all(abs(item['offset_ms']) <= 1 for item in positions), 'Synthetic source alignment exceeds 1 ms'
report = {'mode':metadata['transcription_mode'], 'synthetic_seconds':expected_frames/rate,
          'sources':len(metadata['sources']), 'segments':sum(len(row['segments']) for row in metadata['sources']),
          'mp3':mp3, 'timeline_positions':positions, 'reference_characters':len(ref),
          'character_edits':previous[-1], 'character_error_rate':previous[-1]/len(ref),
          'transcript_rows':len(rows),'uncertain_speaker_rows':text.count('说话人待确认'),
          'output_pair_present':True,'raw_tracks_compacted':True, 'native_mp3_reused':True,
          'scope':'synthetic audio through real models; no microphone or hardware timeline claim'}
(home/'new-user-transcription-report.json').write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps(report,indent=2,ensure_ascii=False))
