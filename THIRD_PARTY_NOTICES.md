# Third-Party Notices

ScreenTranslator uses the following third-party components and model assets.

## LibreTranslate

- Project: https://github.com/LibreTranslate/LibreTranslate
- Version: 1.9.6
- License: GNU Affero General Public License v3.0
- Source for the distributed version:
  https://github.com/LibreTranslate/LibreTranslate/tree/v1.9.6

The complete Windows package distributes LibreTranslate as a separate private
Python process and communicates with it only through its loopback HTTP API. The
LibreTranslate license and the exact corresponding source archive remain
present in the private runtime under `LibreTranslate/Sources`.

## Argos Translate

- Project: https://github.com/LibreTranslate/argos-translate
- Package: `argos-translate-lt` 1.12.1
- License: MIT / CC0

Argos language packages are downloaded on first use from the package index
maintained by the Argos project and are not stored in this source repository.

## Python

- Project: https://www.python.org/
- Version: 3.13.13 for the Windows x64 private runtime
- License: Python Software Foundation License

The Python license file is included at the root of the distributed private
runtime.

## LLamaSharp

- Project: https://github.com/SciSharp/LLamaSharp
- Packages: `LLamaSharp` 0.27.0 and `LLamaSharp.Backend.Cpu` 0.27.0
- License: MIT

LLamaSharp and its CPU backend provide in-process GGUF model inference.

## Qwen2.5-0.5B-Instruct-GGUF

- Project: https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF
- File: `qwen2.5-0.5b-instruct-q4_k_m.gguf`
- Revision: `9217f5db79a29953eb74d5343926648285ec7e67`
- License: Apache License 2.0

The model is downloaded from the official Qwen repository on first use and is
not redistributed in this source repository.
