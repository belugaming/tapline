#!/bin/bash
# Builds every deliverable into dist/:
#   music -> loudness-normalized soundtrack
#   Chinese 16:9 (Bilibili), Chinese 9:16 (Douyin / WeChat), English 16:9 (GitHub, under 10 MB)
#   covers for each format
# Usage: ./build.sh [all|music|videos|covers|github|dist]   (default: all)
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out dist
STAGE=${1:-all}

music() {
  node music.cjs
  local I
  I=$(ffmpeg -hide_banner -i out/music_raw.wav -af ebur128 -f null - 2>&1 | grep -E '^\s+I:' | awk '{print $2}')
  ffmpeg -hide_banner -loglevel error -y -i out/music_raw.wav -af "volume=$(echo "-14 - ($I)" | bc -l)dB" -c:a pcm_s24le out/music.wav
}

videos() {
  node render.cjs --lang zh --fmt h --workers 10
  node render.cjs --lang zh --fmt v --workers 10
  node render.cjs --lang en --fmt h --workers 10
}

covers() {
  for l in zh en; do for f in h v; do node render.cjs --lang $l --fmt $f --cover; done; done
}

# GitHub: the first frame is the thumbnail in a README, so open on the cover and fade
# into the video; 30 fps and under the 10 MB limit for videos on free plans.
# One cut per README: English (README.md) and Chinese (README.zh-CN.md).
github() {
  local DUR=66.2 ABR=96 VBR l
  VBR=$(echo "9400 * 8 / $DUR - $ABR" | bc)
  local FV="[0]fps=30,format=yuv420p,setsar=1[p];[1:v]fps=30,setsar=1[v];[p][v]xfade=transition=fade:duration=0.4:offset=0.2,format=yuv420p[vo]"
  local ENC=(-c:v libx264 -preset slow -tune animation -b:v ${VBR}k -maxrate $((VBR * 2))k -bufsize $((VBR * 4))k
    -color_primaries bt709 -color_trc bt709 -colorspace bt709)
  for l in en zh; do
    local IN=(-loop 1 -t 0.6 -i out/covers/cover-$l-16x9.png -i out/tapline-$l-16x9.mp4)
    ffmpeg -hide_banner -loglevel error -y "${IN[@]}" -filter_complex "$FV" -map '[vo]' "${ENC[@]}" -pass 1 -passlogfile out/gh-$l -an -f mp4 /dev/null
    ffmpeg -hide_banner -loglevel error -y "${IN[@]}" -filter_complex "$FV;[1:a]adelay=200|200[ao]" -map '[vo]' -map '[ao]' "${ENC[@]}" -pass 2 -passlogfile out/gh-$l \
      -c:a aac -b:a ${ABR}k -movflags +faststart dist/tapline-github-$l.mp4
  done
}

dist() {
  cp out/tapline-zh-16x9.mp4 dist/tapline-bilibili-16x9.mp4
  cp out/tapline-zh-9x16.mp4 dist/tapline-douyin-9x16.mp4
  cp out/covers/cover-zh-16x9.png dist/cover-bilibili-16x9.png
  cp out/covers/cover-zh-9x16.png dist/cover-douyin-9x16.png
  cp out/covers/cover-en-16x9.png dist/cover-github-16x9.png
  ls -lh dist
}

case $STAGE in
  all) music; videos; covers; github; dist ;;
  *) $STAGE ;;
esac
