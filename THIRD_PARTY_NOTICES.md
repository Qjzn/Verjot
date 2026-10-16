# Third-party components

verbit (formerly 片言 and 听记/Tingji) loads these components separately. Their respective licenses and model terms apply; runtime packages and weights are not included in this repository.

- [NAudio 1.10.0](https://www.nuget.org/packages/NAudio/1.10.0): MIT; see the package's license metadata and [upstream license](https://github.com/naudio/NAudio/blob/master/license.txt).
- [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR) and [Qwen3-ASR-1.7B-hf](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf): see the upstream repository and model card for terms.
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), [3D-Speaker](https://github.com/modelscope/3D-Speaker), and the speaker segmentation model downloaded by `download_models.py`: see each upstream project and the specific model's terms.
- Python dependencies listed in `requirements.txt` / `requirements-api.txt` and PyTorch retain their own licenses.
- [imageio-ffmpeg 0.6.0](https://github.com/imageio/imageio-ffmpeg/tree/v0.6.0): BSD-2-Clause for the Python wrapper. Its Windows wheel separately includes an FFmpeg executable; that executable and its compiled codecs retain their upstream licenses. See [FFmpeg legal information](https://ffmpeg.org/legal.html) and the installed binary's `-L` / `-buildconf` output. The executable is installed locally, not committed to this repository.

The user-approved logo is the text-free folded-paper and waveform symbol in `assets/verbit-logo.png`, generated for this project with the built-in image tool using a user-supplied visual reference; its exact model version was not exposed. The reference image itself is not distributed in this repository. The project name `verbit` is rendered separately from the symbol. `assets/Create-Icons.ps1` packages the PNG into the current ICO resources and adds the recording-state indicator. The blue/red waveform SVG files are legacy artwork, not the source of the current icons. See [brand and asset notes](documentation/brand.md) for the approved design and small-size rules.
