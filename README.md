<h1 align="center">Cinderbox 星露谷 · Steam 在线 &amp; 读档加速</h1>

<p align="center"><a href="README.en.md">English</a> · 中文</p>

<p align="center">
两个 SMAPI 模组，给在 Android 上用 Cinderbox 跑 PC 版《星露谷物语》的人。<br>
一个让 Steam 好友看见你在玩、把成就写回账号；一个把大模组存档的读档时间砍掉一半。
</p>

<p align="center">
  <img alt="SMAPI 4.x" src="https://img.shields.io/badge/SMAPI-4.x-7bb241?style=flat-square">
  <img alt="Stardew Valley 1.6.15" src="https://img.shields.io/badge/Stardew%20Valley-1.6.15-f2a73b?style=flat-square">
  <img alt="Cinderbox 0.8" src="https://img.shields.io/badge/Cinderbox-0.8.x-6c5ce7?style=flat-square">
  <img alt="Android arm64" src="https://img.shields.io/badge/Android-arm64-3ddc84?style=flat-square">
</p>

---

## 为什么会有这两个模组

我在 AYN Thor 上用 Cinderbox 玩星露谷，挂了六十多个模组、七十多个内容包（SVE、RSV、East Scarp 那一套）。玩起来没问题，但有两件事一直别扭：

1. Cinderbox 把游戏自带的 Steamworks 换成了空壳，Steam 上永远看不到"正在玩"，时长不涨，游戏里拿到的成就也进不了账号。
2. 读档要等将近五十秒。

查了一圈发现 Cinderbox 其实自带一份完整的 SteamKit2（它拿来登录和下载游戏），所以第一件事不用重新造轮子，直接在同一个进程里借用就行。第二件事则是量出来的：读档时间里有一大半花在反复解码同一批 PNG 和解析 XML 格式的地图上，而这两样都能缓存。

于是就有了这两个模组。它们互相独立，装一个或两个都可以。

## SteamPresence —— 让 Steam 知道你在玩

<table>
<tr><td width="140"><b>在线状态</b></td><td>游戏在前台时，好友列表显示「正在玩 Stardew Valley」。切到桌面或别的应用会自动下线，切回来再上线；点下屏不算离开。</td></tr>
<tr><td><b>游戏时长</b></td><td>Steam 照常累计，和桌面客户端一样。</td></tr>
<tr><td><b>成就</b></td><td>游戏里拿到成就的瞬间写进 Steam 账号。读档后游戏会把存档里已有的成就再报一遍（PC 版也这样），所以老存档里早就拿到、Steam 上却没有的成就，第一次读档就会补上。</td></tr>
<tr><td><b>解锁提示</b></td><td>右下角升起一张 Steam 风格的卡片：成就图标、名称，停五秒落下。只在 Steam 确认写入成功后才弹，账号里已有的不会重复弹；一次补同步太多会合成一张。</td></tr>
<tr><td><b>登录</b></td><td>优先用你在 Cinderbox 启动器里登录过的 Steam 账号，什么都不用做。没有的话，标题画面会出现一个二维码，用手机 Steam App 扫一次就记住了。</td></tr>
<tr><td><b>不抢位置</b></td><td>同一账号在别的设备上正在玩时，这边不会上报，和 Steam 客户端的行为一致。</td></tr>
</table>

### 它是怎么做的

- Cinderbox、游戏、SMAPI 和所有模组跑在同一个 Mono 进程里，SteamKit2 也在。模组直接拿它开一个自己的会话（独立的 LoginID，不会和 Cinderbox 下载游戏时的会话互相踢），上线后发 `ClientGamesPlayed`。
- 凭据只读：通过反射调 Cinderbox 自己的凭据类读账号名和刷新令牌，不写、不删、不改。扫码登录得到的令牌存在应用私有目录，用模组自己的 AndroidKeyStore 密钥加密，和 Cinderbox 的密钥分开。
- 成就走 Harmony：拦下 `Game1.getPlatformAchievement`，读一次 Steam 的成就表（schema），按位号把还没置位的位写回去，再等 Steam 的回执。
- 只在 WebSocket 上连 Steam。手机上开着代理时裸 TCP 常断，这是在别的移植上踩过的坑。

## LoadCache —— 读档快一倍

先说结论。同一个存档、同一套模组，在 Thor 上：

| | 之前 | 之后 |
|---|---:|---:|
| 读取存档到第一天开始 | 46.3 秒 | **24.3 秒** |
| 其中：PNG 解码 | 18.8 秒 | 2.5 秒 |
| 其中：TMX 地图解析 | 9.8 秒 | 4.5 秒 |
| 读档期间的大内存回收 | 23 次 | 3 次 |

### 时间原来花在哪

用一个只计时的小模组量了一次读档：2400 多次图片解码、200 多张 TMX 地图。最离谱的是重复解码——Content Patcher 在读档过程中会清几次缓存，很多补丁又共用同一张源图，结果一张 `Cursors_Fall.png` 解了 34 次，一张小贴图解了 194 次。Cinderbox 其实有一个图片缓存开关，但默认关着，而且开了以后解码结果会一直留在内存里。

### 做了三件事

**读档期间不重复解码。** 解码过的图留在内存里，同一张图再要就直接给。按字节上限做 LRU，进入第一天几秒后整体释放，平时玩不占内存。

**PNG 直接解成游戏要的格式。** SMAPI 原来的路径是 Skia 解码 → 预乘 alpha → 逐像素转成 MonoGame 的 `Color`，最后那个循环在 Mono 上比解码本身还慢。现在让 Skia 一步写进目标数组，没有中间缓冲、没有转换循环。结果和原来逐字节相同（用真实模组文件比对过）。

**TMX 地图缓存成 TBIN。** 模组地图几乎全是 TMX（Tiled 的 XML），解析速度只有二进制 TBIN 的四分之一。第一次解析某张地图后，把它存成 TBIN，马上读回来和原地图逐图层、逐格、逐属性比对，完全一致才留下；以后只要 TMX 文件没变就直接读 TBIN。存不下的地图会被标记，照常走原解析。

这三件事都不改变游戏实际加载的内容——Content Patcher 的修改照常执行，模组更新后缓存自动失效。删掉模组文件夹就回到原样。

## 安装

1. 从 Releases 下载 `SteamPresence-x.y.z.zip` 和/或 `LoadCache-x.y.z.zip`。
2. 解压，把文件夹放进 `/sdcard/StardewValley/desktop/Mods/`。
3. 重启游戏。

SteamPresence 第一次进游戏时，如果 Cinderbox 里没登录过 Steam，标题画面会显示二维码；不想现在扫可以按 B 关掉，这次启动不再提示。

LoadCache 第一次读档会建地图缓存（在 `Mods/LoadCache/cache` 下，大约几十 MB），这一次会比平时稍慢一点，从第二次开始见效。

## 配置

两个模组第一次运行后都会在自己的文件夹里生成 `config.json`。

**SteamPresence**

| 项 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关 |
| `Achievements` | `true` | 是否把成就写进 Steam |
| `AchievementToasts` | `true` | 是否弹解锁提示 |
| `ToastTestOnStart` | `false` | 启动时弹一张测试卡片看外观，不碰 Steam |

**LoadCache**

| 项 | 默认 | 说明 |
|---|---|---|
| `ImageCache` | `true` | 读档期间的图片去重缓存 |
| `ImageCacheMB` | `256` | 该缓存的内存上限；设备内存紧张可以调到 128 |
| `ReleaseAfterSeconds` | `5` | 第一天开始后几秒释放图片缓存 |
| `CollectAfterRelease` | `false` | 释放后强制一次 GC。立刻还内存，代价是每天早上卡半秒 |
| `FastDecode` | `true` | PNG 直接解码 |
| `MapCache` | `true` | TMX → TBIN 地图缓存 |
| `LogStats` | `true` | 每次读档后在 SMAPI 日志里打一段统计 |

控制台命令：`steam_presence`（在线状态）、`steam_login`（重新显示二维码）、`steam_logout`（忘掉模组自己的扫码登录）、`steam_toast_test`、`load_cache`（缓存统计）、`load_cache_clear`。

## 从源码构建

每个模组都分两层：

- 主层是普通 SMAPI 模组（net6.0），引用 PC 版游戏和 SMAPI。
- `*.Core` 层对着从 Cinderbox APK 里拆出来的 .NET 10 程序集编译——SteamKit2、SkiaSharp、Mono.Android——由主层在运行时 `Assembly.LoadFrom` 加载。这么绕是因为 SMAPI 的程序集重写器只认磁盘上的程序集，而这些都打包在 APK 的 `libassembly-store.so` 里（`tools/extract_store.py` 负责拆）。

需要 .NET SDK 8、一份自己拥有的 PC 版星露谷 1.6.15 加 SMAPI，以及拆出来的程序集目录。把 `local.props.example` 复制成 `local.props`，填上这两个路径（`GamePath`、`AsmDir`）；或者设环境变量 `STARDEW_GAME_PATH`、`CINDERBOX_ASM_DIR`。

```bash
bash tools/build.sh         # SteamPresence → out/SteamPresence
bash tools/build-cache.sh   # LoadCache     → out/LoadCache
```

`tests/` 下面有三样东西：`CoreSmoke` 在 PC 的 .NET 10 上冒烟测试 Steam 连接和二维码编码；`CacheCheck` 用真实模组文件校验地图往返一致、两种 PNG 解码字节相同；还有两份 Thor 上的读档计时报告，开缓存前后各一份。

开发过程中的细节、踩过的坑、每一版改了什么，都在 [HANDOFF.md](HANDOFF.md)。

## 已知限制

- 只针对 Cinderbox。在桌面 SMAPI 里加载会直接什么都不做（LoadCache 可以用 `AllowOutsideCinderbox` 强开，SteamPresence 不行，桌面上有正经的 Steam 客户端）。
- Cinderbox 更新后如果改了凭据类的名字，SteamPresence 会退回扫码登录；如果不再内置 SteamKit2，它就不工作了（可能性不大，Cinderbox 下载游戏离不开它）。
- 引用了外部 `.tsx` 图块集的 TMX 地图，只有 `.tmx` 本身变化才会让缓存失效。
- 云存档没做。手机上挂着一百多个模组的存档和 PC 存档混同步，互相覆盖的风险太大，这事还是手动来。
- 启动到标题画面那四十多秒基本是 Content Patcher 初始化上百个内容包，这个模组帮不上忙。

## 仓库里没有的东西

游戏文件、Cinderbox 文件、任何模组的资源都不在这里，拆出来的程序集也不在。只有这两个模组的源码、构建脚本和测试。

---

<p align="center"><sub>English version: <a href="README.en.md">README.en.md</a></sub></p>
