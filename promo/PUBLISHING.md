# 发布用文案和说明

成品都在 `dist/`（运行 `./build.sh` 重新生成）。

| 文件 | 用途 | 规格 |
|---|---|---|
| `tapline-bilibili-16x9.mp4` | B站，也可以发横屏朋友圈 | 1920×1080，60 fps，66 秒，中文 |
| `tapline-douyin-9x16.mp4` | 抖音、视频号、朋友圈 | 1080×1920，60 fps，66 秒，中文 |
| `tapline-github-en.mp4` | GitHub README / Release | 1920×1080，30 fps，66 秒，英文，9.5 MB |
| `tapline-github-zh.mp4` | GitHub `README.zh-CN.md` | 1920×1080，30 fps，66 秒，中文，9.5 MB |
| `cover-bilibili-16x9.png` | B站封面 | 1920×1080 |
| `cover-douyin-9x16.png` | 抖音 / 视频号封面 | 1080×1920，主要内容在中间 3:4 区域内 |
| `cover-github-16x9.png` | 英文封面（GitHub 版视频的第一帧就是它） | 1920×1080 |

背景音乐由 `music.cjs` 原创合成，可以放心发，不会触发版权识别。视频里的“校正前 / 校正后”和“一键旁通”在声音上也是真的变化，所以**不要在抖音里另配音乐**，否则这部分就听不出来了。

---

## B站

**标题**（任选一个）

- Mac 终于有不用装驱动的全局 EQ 了｜Tapline 免费开源
- 我给 Mac 写了个系统级均衡器：不装驱动，还能校正耳机和房间

**简介**

```
Tapline 是一个 macOS 系统级均衡器，免费开源（GPL-3.0）。

· 参数均衡 + 实时频谱，最多 24 个频段，A/B 对比、一键旁通
· 耳机校正：5,000+ AutoEQ 现成方案，或从 40,000+ squig.link 原始测量现场计算 EQ
· 音箱 + 房间校正：用麦克风测量，先指出摆位问题，再压掉房间驻波，可导出到 RME ADI-2 DAC
· 每个 App 单独调音量（0–200%、静音、直通）
· 不装驱动、不加虚拟声卡：基于 macOS 的 Core Audio Process Tap，退出即恢复原样

下载 / 源码：https://github.com/belugaming/tapline
系统要求：macOS 26 及以上，Apple Silicon

背景音乐为原创。
```

**标签**：Mac、macOS、均衡器、EQ、耳机、AirPods、HiFi、开源软件、音频、AutoEQ

**封面**：`cover-bilibili-16x9.png`

## 抖音 / 视频号

**文案**

```
Mac 全局 EQ，终于不用再装驱动和虚拟声卡了 🎧
耳机校正、房间校正、每个 App 单独调音量，全部免费开源。
GitHub 搜 Tapline
#Mac #macOS #耳机 #均衡器 #开源软件 #效率工具
```

抖音简介里的链接点不开，所以写“GitHub 搜 Tapline”；视频号可以直接放链接。封面用 `cover-douyin-9x16.png`。

## 朋友圈

直接发 `tapline-douyin-9x16.mp4`（竖屏在手机上更满），配一句：

```
做了个 Mac 小工具：系统级均衡器，不用装驱动。耳机、音箱都能校正，免费开源 👉 github.com/belugaming/tapline
```

## GitHub

GitHub 只接受上传到它自己服务器的视频（免费账户每个视频最大 10 MB，GitHub 版是 9.5 MB）：

1. 在 github.com 上新建 issue 的页面，把 `tapline-github-en.mp4`（`README.zh-CN.md` 用 `tapline-github-zh.mp4`）拖进输入框，不用提交 issue。
2. GitHub 会插入一行 `https://github.com/user-attachments/assets/…`，把这一行单独放在简介下面、第一张截图上面，README 里会显示成播放器。
3. 也可以用同样的方法拖进 v0.2.0 的 Release 说明里。

GitHub 版的第一帧就是封面，所以在 README 里不会显示成一个黑框。
