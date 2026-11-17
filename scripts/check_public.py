"""Validate shared assets and offline preview dependencies."""
from pathlib import Path
import json
import re
import subprocess
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[1]
catalog = json.loads((ROOT / 'art/v39-timed/catalog.json').read_text('utf-8'))
assert len(catalog) == 40
assert sum(len(c['rects']) for c in catalog) == 124
for entry in catalog:
    assert (ROOT / 'art/v39-timed' / entry['file']).resolve().is_file(), entry['id']

html_files = list(ROOT.glob('*.html')) + list((ROOT / 'art').rglob('*.html'))
for html in html_files:
    text = html.read_text('utf-8')
    for ref in re.findall(r'(?:src|href|data)=["\']([^"\']+)["\']', text):
        if ref.startswith(('#', 'https:', 'http:', 'data:')):
            continue
        path = unquote(ref.split('#')[0].split('?')[0])
        if path:
            assert (html.parent / path).is_file(), (html.name, path)

emotion = (ROOT / '情緒動畫預覽.html').read_text('utf-8')
for filename in re.findall(r'"file":\s*"([^"]+)"', emotion):
    assert (ROOT / 'art/character/Interactions' / filename).is_file(), filename
for i in range(43):
    assert (ROOT / f'art/speech-audio/EMO-{i:03}.mp3').is_file()

if (ROOT / '.git').exists():
    files = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().split('\0')
    forbidden = {'api_key', 'voice-profile.json', 'settings.json', 'conversation.jsonl'}
    for name in filter(None, files):
        path = Path(name)
        assert path.name not in forbidden, name
        assert not any(p in {'bin', 'obj', 'dist', '.tools', '.mia-runtime'} or p.startswith('.test') for p in path.parts), name
        assert (ROOT / name).stat().st_size < 100 * 1024 * 1024, name
assert not any((ROOT / name).exists() for name in ('src', 'tests', 'integration', 'build.ps1', 'Start-Mia.ps1'))
print('PASS: 40 animations / 124 frames, preview links, 43 audio samples, public file boundaries')
