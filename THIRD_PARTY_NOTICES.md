# Third-party notices

DVTapeMerger does not contain, link to, or redistribute any third-party code or binaries.
It starts the following programs as separate processes when the user has installed them.

## dvrescue

- Project: https://github.com/mipops/dvrescue
- License: BSD 3-Clause License, Copyright (c) 2019-2020, Moving Image Preservation of Puget Sound
- Used for: frame/block level merging of DV captures (`dvrescue --merge`)

The names of the dvrescue copyright holders and contributors are mentioned only to identify the
external tool. This project is not affiliated with, or endorsed by, them.

If you redistribute dvrescue together with DVTapeMerger, include dvrescue's own license text
(`LICENSE.txt` in the dvrescue package) as required by its license.

## FFmpeg

- Project: https://ffmpeg.org/
- License: LGPL 2.1 or later / GPL 2 or later, depending on the build
- Used for: extracting DV streams from AVI files and writing AVI/MOV/MKV containers (stream copy only)

If you redistribute an FFmpeg binary together with DVTapeMerger, follow the license of that build
(provide its license text and the corresponding source code offer).
