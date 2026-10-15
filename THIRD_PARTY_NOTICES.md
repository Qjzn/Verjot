# Third-party components

听记 loads these components separately. Their respective licenses and model terms apply; runtime packages and weights are not included in this repository.

- [NAudio 1.10.0](https://www.nuget.org/packages/NAudio/1.10.0): MIT; see the package's license metadata and [upstream license](https://github.com/naudio/NAudio/blob/master/license.txt).
- [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR) and [Qwen3-ASR-1.7B-hf](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf): see the upstream repository and model card for terms.
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), [3D-Speaker](https://github.com/modelscope/3D-Speaker), and the speaker segmentation model downloaded by `download_models.py`: see each upstream project and the specific model's terms.
- Python dependencies listed in `requirements.txt` / `requirements-api.txt` and PyTorch retain their own licenses.
- [imageio-ffmpeg 0.6.0](https://github.com/imageio/imageio-ffmpeg/tree/v0.6.0): BSD-2-Clause for the Python wrapper. Its Windows wheel separately includes an FFmpeg executable; that executable and its compiled codecs retain their upstream licenses. See [FFmpeg legal information](https://ffmpeg.org/legal.html) and the installed binary's `-L` / `-buildconf` output. The executable is installed locally, not committed to this repository.

The blue/red waveform artwork in `assets/` was created for this project. SVG sources and `Create-Icons.ps1` reproduce the PNG/ICO assets.
