# Cinderbox 星露谷 · Steam「正在玩」—— 交接

## 0.3.1（2026-11-08 20:52 编好，20:5x 用户同意后已放 Thor，md5 已核对，ToastTestOnStart 仍开着，待实测）
- 0.3.0 Thor 实测（20:46）：读档后闪退。**原因是系统内存不足被 lowmemorykiller 杀掉**（`Kill 'com.game.cinderbox' ... 3359588kB rss ... critical pressure`），不是异常；当时后台还开着潜水员戴夫（drmobile，约 1.5 GB）、GameHub 等，20:48:03 起系统连杀了二十多个后台应用。被杀前日志停在「解锁 6/11/24/32（stat 1 位 6/9/20/28）」，写入有没有到 Steam 不确定，下次读档看日志（`核对成就 N 个，新解锁 M 个`）。
- 同一日志里另一个问题：大模组读档超过 15 秒不出 tick，被当成切后台，Steam 会话断开重连两次。
- 改动：
  - 前后台判断改用 `ActivityManager.GetMyMemoryState` 的 importance ≤ 200（前台/可见，下屏有焦点时也算）；Android 调用放在单独的 NoInlining 方法里（缺程序集时 JIT 失败落在调用方的 try 里，PC 冒烟测试就是这样崩过一次）。读不到时退回帧心跳，阈值放宽到 120 秒。
  - 成就写入防抖：最后一次上报后安静 10 秒再写，避开读档时的内存峰值；失败放回队列并重新计时。
- 建议用户玩大模组星露谷时先关掉其他大应用（尤其是另一个游戏）。

## 0.3.0（2026-11-08 20:44 编好，20:5x 用户同意后已放 Thor，md5 已核对；Thor 上 config.json 的 ToastTestOnStart=true，看完外观要改回 false）—— 成就 + 解锁提示
- 0.2.0 Thor 实测通过（20:33）：扫码登录成功 → 上线 → `正在玩 413150`，Steam 回 PlayingSessionState app 413150。
- 成就来源：Harmony 后置补丁 `Game1.getPlatformAchievement(string)`（游戏所有成就都走这里：`getAchievement(int)` 传编号，`getSteamAchievement` 传 Steam 名）。`"0"` 按 SteamHelper 改成 `"a0"`。**游戏每次读档后第一天开始时会把存档里已有的成就全部再报一遍**（`Stats.AllowRetroactiveAchievements`，SteamHelper 返回 true，PC 同样如此），所以不需要持久化队列。
- 写 Steam（`Achievements.cs`，移植 lom 的 flushAchievements）：`ClientGetUserStats`（schema_local_version -1）→ 二进制 KV schema `stats/<id>/bits/<键名=位号>/name` → 只 OR 进还没置位的位 → `ClientStoreUserStats2`（带刚读到的 crc_stats）→ 等 `ClientStoreUserStatsResponse`。回包靠自定义 `StatsReplies : ClientMsgHandler`（连接前 AddHandler）。本次进程内核对过的名字记在 done，失败的放回队列下次连上再试。日志逐条写「解锁 X（stat 位）」和 schema 里找不到的名字。
- 提示（`AchievementToast.cs`）：只在 Steam 写入成功后弹，已有的不弹；右下角深色卡片 + 64px 图标 + 「已解锁成就」+ 名称（schema 的 schinese→tchinese→english）；从屏幕下沿升起，停 5 秒，原路落下；一次新解锁 >3 个合成一张「已同步 N 个成就到 Steam」。画在菜单层/HUD 层，两层都没画的帧（过场）退到 Rendered。字体缺字替换成默认字符。时间只在画出来时走。
- 图标：`files/steam-presence/icons/<hash>.jpg`，依次试 akamai 旧路径、shared.akamai、shared.fastly 的 community_assets（本机连不上 steamcommunity，没验证到具体文件）。
- 配置：`Achievements`（默认 true）、`AchievementToasts`（true）、`ToastTestOnStart`（false，开了启动时弹一张测试卡，不碰 Steam）。控制台 `steam_toast_test`。
- 没验证：Thor 上的读 schema/写入、图标下载、卡片外观。**别为测试去解锁用户成就**；看外观用 ToastTestOnStart。

## 0.2.0（2026-11-08 20:30 编好，20:3x 用户同意后已覆盖到 Thor，md5 已核对，待扫码实测）
- 0.1.0 Thor 实测（20:20）：SMAPI 正常加载两层、SteamKit2 3.4.0 可用、找到 Cinderbox 凭据类，但**Cinderbox 里没有 Steam 登录**（logcat 无解密错误 → `steam_credentials.enc` 不存在；用户当初是手动导入游戏文件）。所以没上线。
- 0.2.0 加扫码登录兜底：
  - 凭据顺序：Cinderbox 的登录 → 模组自己的扫码登录 → 都没有就扫码。Cinderbox 的令牌被拒时这次启动不再用它；模组自己的令牌被拒就删掉重扫。
  - 扫码：SteamKit2 `BeginAuthSessionViaQRAsync`（设备名「AYN Thor · Cinderbox 星露谷」，持久会话），`ChallengeURLChanged` 时重画。
  - 存储：`TokenStore`，`<files>/steam-presence/login.enc`，AndroidKeyStore 别名 `steampresence_login`（模组自己的，不碰 Cinderbox 的 `cinderbox_credentials`），AES-GCM，格式同 Cinderbox。核心层直接引用 Cinderbox 的 Mono.Android/Java.Interop 编译。
  - 二维码：自写编码器 `QrCode.cs`（字节模式、纠错 M、版本 1–10）。`tests/qr_decode.py`（ComfyUI 的 Python 带 OpenCV 4.10）：60 个码用微信引擎全部解码；OpenCV 默认检测器对版本 ≥8 的部分码失败，是检测器问题（各掩码都有成有败），登录链接约 40 字符是版本 3，两种都过。
  - 界面：`RenderedActiveMenu`/`RenderedHud` 画在最上层（白底黑码 + 提示），B/Esc/右键/点面板外关闭，本次启动不再提示；显示期间吞掉所有按键，免得点到后面的标题菜单。字体没中文时改英文提示。控制台命令 `steam_login`（重新显示）、`steam_logout`（删模组登录）、`steam_presence`（状态）。
- 下一步：用户同意后推 `out/SteamPresence` 覆盖 Thor 上的 0.1.0，打开游戏应在标题画面看到二维码，用手机 Steam App 扫码。

## 0.1.0 状态（2026-11-08 20:20）
用户选了路线 C（SMAPI 模组）。**0.1.0 已于 11-08 20:2x 经用户同意放到 Thor**（`/sdcard/StardewValley/desktop/Mods/SteamPresence`，md5 已核对；没启动游戏），等用户打开游戏实测。下次先读 `SMAPI-latest.txt` 里本模组的行。

- 产物：`out/SteamPresence/`（`SteamPresence.dll` + `SteamPresence.Core.dll` + `manifest.json`）和 `out/SteamPresence-0.1.0.zip`。构建：`bash tools/build.sh`。
- 结构：
  - `src/SteamPresence`（net6.0，普通 SMAPI 模组，引用 PC 沙盒的 SMAPI/游戏）：不在 Cinderbox 里就什么都不做；用 `UpdateTicked` 记心跳，15 秒没 tick 就算不在屏幕上（切到下屏不会停 tick，切后台会）；运行时 `Assembly.LoadFrom` 加载核心层；控制台命令 `steam_presence` 看状态；`config.json` 的 `Enabled` 可关。
  - `src/SteamPresence.Core`：**对着 Cinderbox 拆出来的 .NET 10 程序集编译**（`DisableImplicitFrameworkReferences`，AsmDir 指向 `asm/`），因为 SMAPI 的 Cecil 解析器只认已加载或磁盘上的程序集，而 SteamKit2 在 APK 里，模组加载时不一定已加载。主层不能直接引用 SteamKit2。
  - 核心逻辑照搬 lom 的 SteamPlay：WebSocket、LoginID `0x53445650`、上线后 SetPersonaState(Online)、等 PlayingSessionState 3 秒、别处在玩就不报、`ClientGamesPlayedWithDataBlob` 413150、退避 30→600 秒、登录被拒（令牌失效等）就停到下次启动并提示到 Cinderbox 重新登录。
  - 凭据：反射 `Cinderbox.Launcher.SteamLogin.AndroidSteamCredentialStore`（只读，`Load()` 后取 AccountName/RefreshToken）。日志里账号打码，不打印令牌。没有凭据时每分钟再查一次。**一期没有扫码兜底。**
- 已验证：`tests/CoreSmoke`（`DOTNET_ROLL_FORWARD=LatestMajor dotnet tests/CoreSmoke/bin/Release/net8.0/CoreSmoke.dll <asm> <core dll>`）在 PC .NET 10.0.9 上加载 Cinderbox 的 SteamKit2 3.4.0，经 WebSocket 连上 CM，无效令牌被拒（InvalidPassword）后停下。
- 没验证：SMAPI 在 Cinderbox 里加载主层、`LoadFrom` 核心层、反射读凭据、真实上线和「正在玩」、切后台/回来、PC 冲突。PC 沙盒里跑游戏会弹窗，没做。
- Thor 实测步骤：`MSYS_NO_PATHCONV=1 adb -s <Thor 序列号> push out/SteamPresence /sdcard/StardewValley/desktop/Mods/`（先问用户；游戏开着时要重启游戏才生效）→ 用户正常打开游戏 → 看 `/sdcard/StardewValley/desktop/ErrorLogs/SMAPI-latest.txt` 里 `[Steam 在线（Cinderbox）]` 的行 → 好友列表看是否「正在玩 Stardew Valley」。撤销：删掉 `Mods/SteamPresence` 文件夹。

---

（以下是研究阶段的记录）

2026-11-08 研究阶段结束。目标：Thor 上用 Cinderbox 玩星露谷时，Steam 好友列表显示「正在玩 Stardew Valley」（413150），并累计游戏时长；成就、云存档是可选的后续。

- 本目录：`<仓库根目录>`（工具、源码、本文件）
- 大件：`<工作目录>（仓库外，放原 APK、拆出的程序集、反编译结果、日志，不入库）`
  - `apk/cinderbox-0.8.1.apk`：从 Thor 拉的原包（只读参考，不重装）
  - `asm/`：从 `libassembly-store.so` 拆出的 256 个程序集（`tools/extract_store.py`；名字靠启发式，`KERNEL32.dll` 实际是 SteamKit2，已另存 `SteamKit2.dll`）
  - `decomp/`：Cinderbox、Cinderbox.Core、Cinderbox.Steam、SMAPI 的反编译源码
  - `smapi/`：Thor 上的 `StardewModdingAPI.dll`、`blacklist-updated.json`

## Cinderbox 是什么（Thor 实测）

| 项 | 值 |
|---|---|
| 包名 | `com.game.cinderbox`，0.8.1（versionCode 113），arm64，targetSdk 36，签名 `ce5204a8` |
| 来源 | 侧载（packageinstaller），2026-08-04 首装、08-22 更新 |
| 技术 | **.NET 10 for Android（Mono，`libmonosgen-2.0.so`）直接跑 PC 版 `Stardew Valley.dll` 1.6.15 build 24356 + MonoGame**；不是 box64，不是官方手机版。作者 PDB 路径 `Cinderbox-NET10` |
| SMAPI | Cinderbox 改过的 4.5.2.5，程序集在 `/sdcard/StardewValley/smapi-internal/`，模组在 `/sdcard/StardewValley/desktop/Mods`（用户装了一百多个） |
| 进程 | 只有一个 `LauncherActivity`，**启动器、游戏、SMAPI、所有模组在同一个进程** |
| Steam 替身 | `assets/stubs/Steamworks.NET.dll`、`GalaxyCSharp.dll` 是空壳，所以游戏自身的 Steam 调用全部无效 |
| **自带 Steam** | 程序集仓库里有完整的 **SteamKit2 3.4.0**（6111 个类型，未被裁剪，`CMsgClientGamesPlayed`、`SteamFriends`、`SteamUserStats`、`CMsgClientStoreUserStats2` 都在）+ protobuf-net、ZstdSharp、System.Net.WebSockets.Client |
| Steam 用途 | `Cinderbox.Steam.dll`：账号密码 + Steam 令牌登录（`BeginAuthSessionViaCredentials`，`IsPersistentSession`）、所有权校验（PICS token 413150）、depot 下载游戏文件。只用 WebSocket（`ProtocolTypes 4`）。**没有任何「正在玩」/GamesPlayed/成就上报** |
| 凭据 | `AppPaths.RootFolder`（应用内部目录）下的 `steam_credentials.enc`：JSON{AccountName, RefreshToken, GuardData}，用 AndroidKeyStore 别名 `cinderbox_credentials` 的 AES-GCM 加密。读写类 `Cinderbox.Launcher.SteamLogin.AndroidSteamCredentialStore`（internal） |
| 未确认 | 用户当初是用 Steam 登录下载的游戏文件，还是手动导入的（内部目录 release 包读不到）。模组运行时一看便知 |

## 三条路线

### A. 改 Cinderbox APK，注入 JavaSteam（不推荐）
- 做法：apktool 改清单 + smali 在 LauncherActivity 里起一个 JavaSteam 前台服务，复用 lom-android 的 `cloud` 模块。
- 问题：
  - 重签名后签名变了，必须先卸载 Cinderbox → **AndroidKeyStore 密钥和内部数据（Steam 凭据、所有权标记、设置）全丢**；/sdcard 上的存档和模组不受影响。
  - 以后 Cinderbox 每更新一次都要重新打补丁，也不能再直接装官方更新。
  - 进程里已经有 SteamKit2，再塞一套 JavaSteam 是重复造轮子。
- 工作量：中；风险：高（签名、数据、更新）。

### B. 独立「Steam 在线」伴侣应用（可行的备选）
- 做法：一个小 APK（或并进 StardewDS 下屏伴侣），用 JavaSteam 扫码登录，复用 lom 的 `SteamPlay.java` 逻辑；Cinderbox 在前台时上报 413150。
- 判断游戏在不在跑：`UsageStatsManager`（需要用户给「使用情况访问」权限）或一个极小的 SMAPI 模组写心跳文件/本地 socket。
- 优点：完全不碰 Cinderbox；Cinderbox 换成什么技术都不影响。
- 缺点：要再扫一次码；多一个常驻进程；成就仍要靠 SMAPI 模组把解锁事件传过来；前台判断有延迟。
- 工作量：中；风险：低。

### C. SMAPI 模组，进程内直接用 Cinderbox 自带的 SteamKit2（**推荐**）
- 做法：写一个 SMAPI 模组 `SteamPresence`（C#，net6/net8 目标，SMAPI 4.x），放进 `desktop/Mods`。
  1. 凭据：先用反射调 Cinderbox 自己的 `AndroidSteamCredentialStore.Load()` 拿 AccountName + RefreshToken（同进程、同 UID，AndroidKeyStore 能解）；拿不到（没在 Cinderbox 里登录过、或 Cinderbox 改了类名）就退到模组自己的扫码登录（`BeginAuthSessionViaQRAsync`，二维码画在游戏里的菜单上），令牌存应用内部目录并用 AndroidKeyStore 加密，**不放 /sdcard**。
  2. 会话：照搬 `lom-android/cloud/.../SteamPlay.java`：只用 WebSocket；独立 LoginID（不和 Cinderbox 自己下载/校验时的会话互踢）；`SetPersonaState(Online)`；等 `PlayingSessionState`，**别的设备在玩就不上报**；发 `ClientGamesPlayedWithDataBlob`（game_id 413150）。
  3. 生命周期：游戏到前台才上线，切后台/退出时清空 GamesPlayed 并断开（MonoGame `Game.Activated/Deactivated` 或 Mono.Android 的 ActivityLifecycleCallbacks）；断线指数退避重连。
  4. 可选二期：Harmony 补丁拦 `Game1.getSteamAchievement`（或 SDK helper），照 lom 的位号算法写 `CMsgClientStoreUserStats2`，先核对 Steam 已解锁的，**不为测试去解锁用户成就**。
- 优点：不改 APK、不换签名、不丢数据；Cinderbox 更新后只要还带 SteamKit2 就继续能用（它下载游戏离不开 SteamKit2，被删掉的可能性小）；复用 Cinderbox 已有登录可做到零额外操作；卸载模组即恢复原样。
- 风险 / 待验证（第一步就是做一个探针模组验证这几点）：
  - SMAPI 加载模组时能否解析对 SteamKit2 的编译期引用（SMAPI 用 Cecil 重写模组，`patch-deps` 里没有 SteamKit2）。不行就改成反射/`dynamic` 调用，或模组自带 SteamKit2.dll。
  - 反射读 Cinderbox 凭据：类名是 internal 的，Cinderbox 改名就失效 → 退到扫码，所以不致命。
  - Thor 的网络/代理下 WebSocket CM 是否稳定（其他移植在代理下只用 WebSocket 没问题）。
  - 星露谷在 Cinderbox 下的成就调用路径（替身里 SteamUserStats 是空的）——二期再查。
- 工作量：小到中（一期只做「正在玩」+ 时长约一天内）；风险：低。

### 共同风险
- **多端冲突**：同一账号同时只能有一处「在玩」。PC 上在玩别的游戏时，Steam 会通过 `PlayingSessionState.blocked` 告诉我们，此时不上报（和 Steam 客户端行为一致）；反过来 Thor 先上报后，PC 再开游戏会弹「在另一台设备上游戏」——这是 Steam 的正常行为，和其他移植一样。
- 登录会话：Steam 允许同账号多个会话并存，用独立 LoginID 就不会把 Cinderbox 自己的会话或手机 Steam App 挤掉。
- 云存档：**一期不做**。星露谷 PC 版用 Steam Auto-Cloud 同步存档，而 Thor 上存档挂了一百多个模组，和 PC 存档混同步容易互相覆盖。要做也是单独一期、先只下载对比。
- 不改 Cinderbox 自身数据；模组只读它的凭据，不写不删。

## 推荐步骤（路线 C）
1. 探针模组（不连 Steam）：在 Thor 上确认 SteamKit2 能被模组引用、能读到 Cinderbox 凭据（只打印「有/无」和账号名，不打印令牌），日志看 SMAPI-latest.txt。**装到 Thor 前问用户**；模组只是往 `desktop/Mods` 放一个文件夹，删掉即恢复。
2. 一期：在线 + 正在玩 + 时长，前后台切换、PC 冲突时让位。验收：好友列表显示「正在玩 Stardew Valley」，退出后时长增加。
3. （可选）扫码登录兜底界面。
4. （可选）成就。

## 工具
- `tools/extract_store.py <libassembly-store.so> <输出目录>`：拆 .NET for Android 程序集仓库（XALZ + LZ4 块，纯 Python）。本机 Python：`python -I`（3.11）（`python` 命令被商店别名占着）。
- 反编译：`ilspycmd -p -o <目录> <dll>`（ILSpy 命令行版）。
- 模组构建：本机 .NET SDK 8.0.422；星露谷/SMAPI 引用可用 `<自己拥有的 PC 版游戏目录>`（同为 1.6.15，见 StardewDS 交接）。
- adb：`adb -s <Thor 序列号>`，`/sdcard` 路径前加 `MSYS_NO_PATHCONV=1`。

## 参考
- 另一个移植项目 lom-android 里的 `cloud/SteamPlay.java`（JavaSteam 版，未公开）：正在玩 + 成就的成熟实现（PlayingSessionState、独立 LoginID、成就位号取 bits 子项键名）。
- StardewDS（Thor 下屏伴侣，另一个项目）：已有 SMAPI 模组 + 下屏 App，路线 B 可以并进去。

## 读档速度（2026-11-08 21:05）
- 用户嫌读档慢、问能否缓存。实测（0.3.1 那次）：启动到标题约 90 秒；读档 `getLoadEnumerator` 46.7 秒，期间约 2900 次资源载入/修改（地图 1240、Data 669、Characters 634、Portraits 284；SVE 484 载入 + 219 修改，RSV 298 + 251，Aquarium 151），Content Patcher 读档中两次全量失效。模组地图 1110 张全是 TMX（XML）。
- Cinderbox 版 SMAPI 有 `RawFileCache`（`useRawFileCache`，默认开），**只在内存里**缓存解码后的图片，跨启动无效，也是内存大的原因之一。`config.ini` 可有 `[Performance]` 的 `enableEventProfiling/eventProfilingThreshold/performanceLogging/useRawFileCache`（用户的 config.ini 里没有这一节）。
- 可安全做磁盘缓存的只有「文件→对象」：PNG 解码（`ModContentManager.DecodeRawImageData(FileInfo)`）、地图解析（`xTile FormatManager.LoadMap(string)`），按路径+大小+修改时间失效；CP 的修改和模组逻辑照常跑。地图转 TBIN 需逐张校验往返一致。
- 先测量：`src/LoadProfiler`（`out/LoadProfiler`，只计时）：Harmony 挂上面两个入口和 `AssetDataForImage.PatchImage`/`AssetDataForMap.PatchMap`，反射打开 `AndroidPaths.EnableEventProfiling`（100 ms，进入游戏一分钟后关），记录阶段时间线和 GC 次数；进入游戏约一分钟后写 `Mods/LoadProfiler/report-*.txt`，SMAPI 日志里也有。控制台 `load_profile` 立即写。11-08 21:0x 用户同意后已放 Thor（Mods/LoadProfiler，md5 已核对），等用户重启读档后读报告；测完要删。

### LoadProfiler 实测结果（2026-11-08 21:11，Thor，报告存 tests/report-thor-20261108-2111.txt）
- 时间线：模组 Entry 阶段约 21 s → GameLaunched → 24.6 s 后标题画面（其中 Content Patcher 首帧初始化 22.5 s、SDV-Radiance GameLaunched 5.9 s）；读档 SaveParsed→DayStarted **46.3 s**，其中 SaveParsed→SaveAddedLocations 34.6 s 是大头。
- 读档 46.3 s 里可计时的部分 31.1 s：PNG 解码 18.8 s（2419 次、188 MB）、TMX 解析 9.8 s（224 张，0.45 ms/KB）、TBIN 0.85 s（191 张，0.12 ms/KB）、贴图修改 1.3 s、地图修改 0.4 s。
- **重复解码严重**：同一文件一次读档里解码 30～194 次（Cursors_Fall.png 34 次 1.6 s、spring_z_dirtpath.png 194 次…），仅前 15 个文件就浪费 ≥ 8.8 s。原因：Cinderbox 的 `RawFileCache` 默认关（`/sdcard/StardewValley/desktop/config.ini` `[ContentLoading] useRawFileCache=false`）。开了会把所有解码像素留在内存直到回标题，有 OOM 风险（之前 3.36 GB 被杀）。
- 读档期间 CP 的 LoadStageChanged 处理 3.7 + 2.4 s，FTM DayStarted 1.9 s（不可缓存）。
- 方案（未动手）：自写 LoadCache 模组：① 读档期间去重缓存（只在 SaveParsed→DayStarted 后几秒内保留，之后释放）≈ 省 9 s+；② Harmony 替换 `DecodeRawImageData`，让 Skia 直接解成预乘 RGBA 后整块拷贝，去掉逐像素托管循环，估计再省 3～5 s；③ TMX 首次解析后写 .tbin 到缓存目录（按路径+大小+mtime 失效，写后回读校验层/图块一致），下次读 .tbin，9.8→≈2.6 s。合计目标 46 s → 25 s 左右。启动阶段的 CP 初始化 22.5 s 不归我们。

## LoadCache 0.1.0（2026-11-08 21:30 编好，21:3x 用户同意后已放 Thor Mods/LoadCache，md5 已核对；游戏当时开着，重启后生效；待读两次档对比）—— 读档加速
- 结构：`src/LoadCache`（SMAPI 模组，Harmony）+ `src/LoadCache.Core`（对 Cinderbox APK 里的 SkiaSharp 2.88.9 编译，运行时 LoadFrom；asm/ 里 SkiaSharp 被拆包脚本误命名为 ole32.dll，已另存 SkiaSharp.dll）。构建 `bash tools/build-cache.sh` → `out/LoadCache`。
- 三项，均可在 config.json 关：
  1. `ImageCache`（上限 `ImageCacheMB`=256）：prefix `ModContentManager.DecodeRawImageData`，按路径+大小+mtime 命中则直接返回；LRU 按字节上限；DayStarted 后 `ReleaseAfterSeconds`=5 秒清空并 GC.Collect，回标题也清。解决同文件重复解码（上次 ≥9 s）。
  2. `FastDecode`：`SKCodec.GetPixels(Rgba8888, Premul)` 直接写进 pinned 的 Color[]，去掉 SMAPI 的 PreMultiply + 逐像素循环；失败回退原解码（前 3 次记 Warn）。
  3. `MapCache`：prefix/postfix `FormatManager.LoadMap`，只管 .tmx：`Mods/LoadCache/cache/maps/<sha1 前 20 位>.tbin/.meta/.bad`；首次解析后 Store 到内存 → Load 回来 → `MapCompare.Diff` 全等才落盘；命中条件 `1|length|mtimeTicks`。TMX 引用外部 .tsx 时只认 .tmx 的变化（已知限制）。
- PC 校验 `tests/CacheCheck`（样本在 `<工作目录>samples`：Thor 上抽的 65 张 TMX + 29 张 PNG）：94/94 通过。TMX 解析 2227 ms vs TBIN 读 737 ms（约 3 倍；TBIN 体积是 TMX 的 2.8 倍，224 张全缓存约 65 MB）；PNG 原解码 707 ms vs 快速 202 ms（3.5 倍），字节全同。PC 上跑 TMXTile 要把 xTile 2.0.4.0 请求重定向到 1.0.0.0，且 TMXTile 类型不能在挂处理器前被 JIT 到（RegisterTmx 单独方法）。
- 控制台：`load_cache`（统计）、`load_cache_clear`。DayStarted+5 s 后 Info 打一段统计。
- 注意：首次读档会因为写缓存变慢一些（Store+回读+比对），第二次起才快。缓存共享 IRawTextureData 实例（Cinderbox 自己的缓存也这样）。
- 预期 Thor：46 s → 25 s 左右；用 LoadProfiler 复测（它还装在 Thor 上）。

### LoadCache 0.1.0 Thor 实测（2026-11-08 21:42 第二次读档，缓存已热；报告 tests/report-thor-20261108-2148-loadcache.txt）
- 读档 SaveParsed→DayStarted **46.3 s → 24.3 s**（SaveParsed→SaveAddedLocations 34.6→15.6）。读档期间 gen2 GC 从 23 次降到 3 次。
- 图片解码 18.8 s → 2.5 s（命中 1311 次省 4.6 s；快速解码 1227 张 2.6 s；挤出 432 张，上限 256 MB 偏紧）。
- TMX 地图 9.8 s → 4.5 s（224 张全部命中，211 个缓存文件 46 MB；Thor 上 TBIN 读约 19 ms/张，主要是 xTile 构造对象，不是 I/O）。
- 剩下的大头：CP 的 LoadStageChanged 处理、FTM DayStarted 1.9 s、贴图修改 1.0 s、游戏自身逻辑。启动到标题仍 44 s（CP 初始化 22 s）。
- 问题：Release() 的 GC.Collect 在 DayStarted+5 s 卡 560 ms，而且**每个游戏日早上都会触发**（每天 CP 失效会重新填缓存）。0.1.1 改为默认不强制 GC（`CollectAfterRelease`=false）。
- 本次进程 VmHWM 3.83 GB（含游戏一分钟），没被杀；之前被杀时 RSS 3.36 GB 是因为后台有别的游戏。图片缓存上限可按需调低（`ImageCacheMB`）。
- 第一次（冷缓存）读档没有报告（用户没等满一分钟），冷启动的额外开销未量化。
- 0.1.1 已编好未放 Thor。

## 收尾（2026-11-08 21:5x，用户同意）—— Thor 上现状
- `Mods/SteamPresence` 0.3.1，`ToastTestOnStart` 已改回 false（成就提示仍开）。
- `Mods/LoadCache` 0.1.1（CollectAfterRelease=false），地图缓存 211 张已建好。
- `Mods/LoadProfiler` 已删除；它的两份报告在 tests/（2111 原始、2148 开缓存后）和 <工作目录>\logs。
- 游戏改动时正在运行，重启后生效。
- 可能的下一步（未做）：标题画面时后台预加载图片/地图（预计读档再省 ~5 s）；SMAPI 模组重写缓存（启动省 ~5 s，风险高）。
