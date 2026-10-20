# MC 下载器（McDownloader）

一款**只负责下载**的轻量化《我的世界》资源下载器：原版 / Fabric / Quilt / Forge / NeoForge 版本安装、
Modrinth 模组与光影/资源包/数据包下载、地图存档导入。所有文件只写入 **程序所在目录** 的 `.minecraft`，
不写注册表、不写用户目录、不启动游戏、不登录账号。

界面为白 + 绿扁平风格，左侧导航 + 右侧卡片内容区 + 底部状态栏。

---

## 一、技术选型与编译方式

### 1. 技术选型

| 项 | 选择 | 说明 |
| --- | --- | --- |
| 语言 / 运行时 | C# 14 + .NET 10 | 与参考项目 PCL2 CE 一致（`net10.0-windows`） |
| UI 框架 | WPF（`UseWPF`） | 与 PCL 同栈；无边框窗口 + 自定义主题，启动快、内存占用低 |
| 第三方依赖 | **0 个 NuGet 包** | 网络 `HttpClient`、JSON `System.Text.Json`、解压 `System.IO.Compression`、Java 探测 `Microsoft.Win32.Registry` 全为 BCL |
| 打包 | 单文件框架依赖 exe（约 0.3 MB） | 目标机需安装 .NET 10 Desktop Runtime；如需免运行时见下方“自包含发布” |
| 架构 | View（XAML）+ 轻量 MVVM + 服务层 | 服务层承载全部 IO/网络，View 只做绑定与事件转发 |

### 2. 目录结构

```
Minecraft_downloader/
├─ build.bat                     # 一键编译 + 发布到 dist
├─ README.md
├─ src/McDownloader/
│  ├─ McDownloader.csproj        # net10.0-windows / UseWPF / 无第三方引用
│  ├─ app.manifest               # PerMonitorV2 DPI、长路径支持
│  ├─ App.xaml(.cs)              # 主题装载、全局异常兜底、启动即初始化工作空间
│  ├─ MainWindow.xaml(.cs)       # 无边框标题栏 + 侧边导航 + 状态栏
│  ├─ Themes/Colors.xaml         # 白绿调色板（#22C55E / #DCFCE7 / #E5E7EB …）
│  ├─ Themes/Controls.xaml       # 按钮 / 输入框 / 下拉 / 复选 / 进度条 / 列表样式
│  ├─ Models/                    # AppConfig、GameVersion、ContentItem、DownloadJob、FileEntry…
│  ├─ Services/                  # 见下表
│  ├─ Converters/                # 布尔/字符串→可见性、状态→颜色、URL→图片
│  └─ Views/                     # 版本下载 / 内容下载 / 下载任务 / 设置
└─ dist/McDownloader.exe         # 发布产物（单文件）
```

服务层一览：

| 文件 | 职责 |
| --- | --- |
| `WorkspaceService` | 工作空间根定位、路径越界守卫、`.minecraft` 目录模板、磁盘剩余 |
| `ConfigService` | `.minecraft/mcdownloader/config.json` 原子读写 |
| `LogService` | 按天滚动日志，保留 7 天，同时广播给界面 |
| `HttpService` | UA、超时、镜像回退、流式下载、`.part` 原子替换 |
| `DownloadService` | 批量下载并发限流、聚合进度、可选文件容错 |
| `DownloadQueueService` | 任务队列（串行任务 + 任务内并发）、取消、重试 |
| `VersionCatalogService` | 版本清单（30 分钟缓存）、Fabric/Quilt/Forge/NeoForge 版本抓取、Java 版本要求 |
| `VanillaInstaller` | 原版 json/jar/libraries/assets/natives |
| `FabricInstaller` | Fabric/Quilt profile json 拼装与库下载 |
| `ForgeInstaller` | installer jar 下载 + 本机 Java 调用 + 输出解析 |
| `JavaScanner` | 注册表 / JAVA_HOME / PATH / 常见目录探测 Java |
| `ModrinthService` | 搜索、版本列表、类型→落地目录 |
| `WorldImporter` | 地图直链/本地 zip、level.dat 校验、zip-slip 防护 |

### 3. 编译

前置：Windows 10/11 + [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bat
:: 方式一：双击 build.bat（推荐，自动编译并发布到 dist）
build.bat

:: 方式二：手动
dotnet build   src\McDownloader\McDownloader.csproj -c Release
dotnet publish src\McDownloader\McDownloader.csproj -c Release -r win-x64 ^
    --self-contained false -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
```

产物：`dist\McDownloader.exe`（单文件，约 0.3 MB），目标机需安装 **.NET 10 Desktop Runtime**。

自包含发布（免安装运行时，体积约 70 MB）：

```bat
dotnet publish src\McDownloader\McDownloader.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist-selfcontained
```

> `build.bat` 会设置 `NUGET_PACKAGES=%~dp0.nuget-packages`（若环境未指定），避免个别机器全局包目录异常导致还原失败。

---

## 二、界面布局

```
┌────────────────────────────────────────────────────────────────────────────┐
│ [M] MC 下载器  轻量·仅下载                              —   □   ✕          │ 48px 标题栏
├──────────────┬─────────────────────────────────────────────────────────────┤
│ 下载中心      │                                                             │
│ ▎版本下载     │   页面标题 + 说明                        [次要操作按钮]      │
│  内容下载     │   ┌───────────────────────────────────────────────────┐     │
│  下载任务     │   │ 卡片：筛选 / 列表 / 表单                          │     │
│  设置         │   └───────────────────────────────────────────────────┘     │
│              │                                                             │
│ ┌──────────┐ │                                                             │
│ │绿色下载   │ │                                                             │
│ │说明卡片   │ │                                                             │
│ └──────────┘ │                                                             │
├──────────────┴─────────────────────────────────────────────────────────────┤
│ 工作空间 D:\...\.minecraft                        3.2 MB/s   磁盘剩余 120 GB │ 32px 状态栏
└────────────────────────────────────────────────────────────────────────────┘
```

- **标题栏**：绿色渐变徽标 + 产品名 + “轻量 · 仅下载”标签；右侧最小化/最大化/关闭（自绘，可拖动、双击最大化）。
- **侧边导航**（218px）：选中项为浅绿底 + 绿色左竖条 + 深绿加粗文字。
- **内容区**：白卡片承载所有内容，绿色仅用于主操作、选中态、进度与徽标，圆角 10–14px。
- **状态栏**：常驻显示工作空间路径、实时总速度、磁盘剩余。

**窗口与动效**

- 启动时按屏幕可用区域自动计算尺寸并居中（默认 1180×760，最小 900×560）：高 DPI 或 1366×768 这类小屏上，标题栏与右上角三个按钮不会被挤出屏幕。
- 无边框窗口最大化时通过 `WM_GETMINMAXINFO` 贴合工作区，不会遮挡任务栏；还原后恢复原尺寸与位置。
- 动效：页面切换 180ms 淡入 + 10px 上移；主按钮/次按钮悬停颜色渐变、按下透明度反馈；导航项悬停浅绿、选中浅绿底 + 左侧指示条渐显；内容卡片悬停边框变绿、阴影加深并轻微放大。
- 动画实现注意：WPF 会把控件模板里的 `Freezable`（如 `ScaleTransform`）冻结，直接动画会抛“对象已密封或已冻结”。因此模板内只做颜色/透明度动画，缩放变换在代码里按实例创建（见 `ContentDownloadView.EnsureScale`）。
- 初始焦点固定在左侧导航，避免小窗口下文本框抢焦点把右侧面板滚动、或弹出输入法。

四个页面：

1. **版本下载**：加载器胶囊（原版 / Fabric / Quilt / Forge / NeoForge）→ 左侧版本列表（搜索 + 全部/正式/快照筛选，条目显示版本号、类型、发布日期、“已安装”徽标）→ 右侧安装面板（加载器版本下拉、安装名称、资源文件开关、目标路径预览、开始下载、阶段进度条、速度、日志）。
2. **内容下载**：关键词搜索 + 类型胶囊（模组 / 光影 / 资源包 / 数据包）+ 游戏版本 / 加载器 / 排序筛选；结果以 2 列卡片网格展示（图标、标题、作者、下载量、简介、最新支持版本）；点击卡片在右侧详情面板选择具体文件并下载；右上角“导入地图存档”打开浮层。
3. **下载任务**：四张概览卡（进行中 / 已完成 / 失败 / 当前速度）+ 任务列表（标题、副标题、状态徽标、进度条、阶段、速度、取消）+ 日志面板（最近 200 行，自动滚动）+ 批量操作（清除已完成 / 全部取消 / 打开目录）。
4. **设置**：下载源（官方源 / BMCLAPI / MCIM，含说明文字）、并发文件数、超时、默认下载资源文件、Java 运行时列表（重新扫描 / 手动指定 / 清除）、工作空间与磁盘信息、一键打开 `.minecraft` / 配置 / 日志 / 缓存、关于信息。

---

## 三、各功能下载流程

### 0. 下载源选择（设置页）

设置页提供三档源，切换后立即生效，并写入 `.minecraft/mcdownloader/config.json`：

| 选项 | 优先源 | 覆盖范围 | 回退顺序 |
| --- | --- | --- | --- |
| 官方源 | Mojang / Modrinth 官方 | 全部 | 官方 → BMCLAPI → MCIM |
| 国内镜像 · BMCLAPI（默认） | BMCLAPI | 版本清单、版本 JSON、客户端 jar、libraries、assets、Fabric/Quilt/Forge/NeoForge Maven | BMCLAPI → MCIM → 官方 |
| 国内镜像 · MCIM | MCIM | Modrinth 搜索/版本 API（`/modrinth`）与文件 CDN；Mojang 资源仍走 BMCLAPI | MCIM → BMCLAPI → 官方 |

实现要点：

- `UrlResolver.Candidates(url, source)` 负责把官方 URL 改写成镜像 URL 并排好优先级，去重后返回；`HttpService` 与 `ModrinthService` 的所有请求都通过它取候选。
- BMCLAPI 改写沿用 PCL 的字符串替换规则（`piston-meta`/`launchermeta` → `bmclapi2.bangbang93.com`，库 → `/libraries`，资源 → `/assets`，Maven → `/maven`，Fabric → `/fabric-meta`）。
- MCIM 改写对应 PCL 的 `DlSourceModGet` 与 `DlSourceModDownloadGet`：`api.modrinth.com` → `mod.mcimirror.top/modrinth`，`cdn.modrinth.com` → `mod.mcimirror.top`。
- **自动回退**：任一首选源出现 404、超时、连接中断或哈希不符，都会自动尝试下一候选；404 会立刻换源（不重试），5xx/超时则在同一源重试一次后换源。
- 旧版本配置（仅有 `"MirrorFirst": true/false`）会在读取时自动迁移为新的 `Source` 枚举，无需手动改配置。

### 1. 原版（`VanillaInstaller`）

1. 拉取版本清单：镜像 `bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json` → 官方 `launchermeta.mojang.com/...`，成功即写缓存（30 分钟有效），全部失败时回读本地缓存并提示。
2. 选中版本后下载该版本的 `<id>.json`，解析 `downloads.client`、`libraries`、`assetIndex`、`logging`。
3. **先把版本 JSON 写入 `versions/<实例名>/<实例名>.json`**（`id` 改为实例名），再开始下载文件——与 PCL 一致：即使后续文件因网络失败，版本也能被 PCL 识别并通过「补全文件」修复。
4. 生成文件清单 `FileEntry`（URL / 目标路径 / SHA1 / 大小）：
   - `client jar` → `versions/<实例名>/<实例名>.jar`
   - `libraries` 按 `rules` 过滤（仅 Windows x64；无规则命中时取首条规则的反面，与官方一致），按 Maven 路径落盘；旧版 `natives-windows` 分类器额外下载并解压到 `versions/<实例名>/<实例名>-natives`
   - `assetIndex` → `assets/indexes/<id>.json`
   - `logging.client.file` → `assets/log_configs/`
5. 批量下载（并发、SHA1 校验、已存在则跳过），失败文件在末尾串行重试一轮；极端情况下仍失败会保留 JSON 并明确报错。
6. 若勾选“下载资源文件”，解析索引 `objects`，按 `resources.download.minecraft.net/<前2位>/<hash>` 下载到 `assets/objects/<前2位>/<hash>`（单文件失败不中断，仅记日志）；`virtual` / `map_to_resources` 的旧版本额外建立映射目录。

### 2. Fabric / Quilt（`FabricInstaller`）

1. 若 `versions/<mc>` 不存在，先自动安装原版（保证 `inheritsFrom` 可解析）。
2. 取官方 profile：`meta.fabricmc.net/v2/versions/loader/{mc}/{loader}/profile/json`（镜像 `bmclapi2.bangbang93.com/fabric-meta/...`；Quilt 为 `meta.quiltmc.org/v3/...`）。
3. 直接以 profile 作为版本 JSON，写入 `id`（默认 `fabric-loader-<loader>-<mc>`）与 `inheritsFrom`，落盘 `versions/<实例名>/<实例名>.json`。
4. 遍历 `libraries`：优先使用 `downloads.artifact`，否则用 `name`（`group:artifact:version`）+ `url` 拼 Maven 路径，下载到 `.minecraft/libraries`。
5. 安装过程**不需要 Java**。

### 3. Forge / NeoForge（`ForgeInstaller`）

1. 从 Maven 元数据获取版本列表（`net/minecraftforge/forge`、`net/neoforged/neoforge`，1.20.1 的 NeoForge 走 `net/neoforged/forge`）。
2. 下载 `*-installer.jar` 到 `.minecraft/mcdownloader/cache`。
3. **探测本机 Java**（不自动下载）：注册表（JavaSoft / Adoptium / Microsoft / Zulu / Corretto / Liberica / Semeru）、`JAVA_HOME`、`PATH`、`Program Files\Java` 等常见目录、`.minecraft/runtime`；逐个执行 `java -version` 解析大版本，按“版本够用优先、大版本升序”挑选。
   - 最低要求：≤1.16 → Java 8；1.17 → 16；1.18–1.20.4 → 17；≥1.20.5 → 21；NeoForge 至少 17。
4. 以 `java -jar <installer> --installClient <gameDir>` 无界面运行（失败自动换工作目录方式重试），逐行解析输出更新阶段（Extracting / Downloading / Building / Processor / Remapping），错误行写日志。
5. 退出码为 0 且 `versions/` 出现新版本 JSON 才算成功，最终版本名由安装器决定（如 `1.20.1-forge-47.2.0`）。

### 4. 模组 / 光影 / 资源包 / 数据包（`ModrinthService`）

1. `GET https://api.modrinth.com/v2/search`，`facets` 组合 `project_type` + `versions:<mc>` + `categories:<loader>`，`index` 为 relevance/downloads/newest/updated，分页 20。
2. 选中工程后 `GET /v2/project/{id}/version?game_versions=[...]&loaders=[...]`；筛选无结果时回落到全部版本并提示。
3. 下载 `files[0]`（含 SHA1），按类型落地：

   | 类型 | 目录 |
   | --- | --- |
   | 模组 | `.minecraft/mods` |
   | 光影 | `.minecraft/shaderpacks` |
   | 资源包 | `.minecraft/resourcepacks` |
   | 数据包 | `.minecraft/datapacks` |

4. 请求携带 `User-Agent: McDownloader/1.0`（Modrinth 要求），无需 API Key。

### 5. 地图（`WorldImporter`）

1. 两种来源：直链 zip（下载到 `mcdownloader/cache/worlds`）或本地 zip。
2. 校验压缩包内必须存在 `level.dat`（根目录或唯一一层子目录），否则判定“不是地图存档”。
3. 存档名清洗（去除 `\ / : * ? " < > |`），若 `saves/<名称>` 已存在则拒绝覆盖并提示改名。
4. 逐条目解压，每个目标路径都经 `WorkspaceService.SafeCombine` 前缀校验，防止 `../../` 越界写入（zip-slip）。

### 6. 下载引擎的横切能力

- **下载源与回退**：按设置页所选的源生成候选列表（首选源 → 其它镜像 → 官方）。库文件同时准备 BMCLAPI 的 `/libraries` 与 `/maven` 两套路径；404 立即换源，超时/SSL 错误在同源重试 3 次（200ms 间隔）。
- **耐心等待镜像首次响应**：BMCLAPI 冷启动首字节常需 10–25 秒，因此空闲看门狗设为 60 秒、单次尝试硬超时 120–240 秒；并发默认 6，避免把镜像压垮。
- **两轮下载**：第一轮并发下载；失败文件在第二轮串行重试，仍失败才报错，并保留已写入的版本 JSON 供启动器补全。
- **取消即回滚**：任务会记录本次实际写入的文件与新建的版本目录（`DownloadJob.CreatedFiles` / `InstanceDirectory`）。点击「取消」后删除这些文件、移除未完成的 `versions/<新实例>` 目录，并清理产生的空目录；**已存在且校验通过的文件不会被删**。清理在线程池执行，几千个资源文件也不会卡界面。日志里会给出「已清理本次下载：N 个文件（XX MB）」。
  - 例外：Forge / NeoForge 由官方安装器写入的文件不在追踪范围内，取消只终止安装器进程，残留文件需手动删除。
- **校验**：按哈希长度自适应 MD5(32)/SHA1(40)/SHA256(64)；只给出大小时校验大小；已存在且校验通过的文件直接跳过（增量安装）。
- **原子写**：先写 `<文件>.part`，校验通过后 `File.Move(..., overwrite)`，异常不留半成品。
- **并发**：任务内文件并发默认 8（1–16 可调），单文件失败即中止并保留首个错误信息；可选资源文件失败只记日志。
- **日志**：任务尾部日志 200 行 + 按天文件日志，便于排障。

---

## 三·补、与 PCL 的兼容性

下载产物按官方启动器规范生成，可直接用 PCL2 打开同一个 `.minecraft` 目录启动，无需额外转换：

| PCL 的检查点 | 本程序的做法 |
| --- | --- |
| `versions/<id>/<id>.json` 存在且 `id` 与文件夹同名 | 先写 JSON 再下载文件，`id` 统一改写为实例名 |
| `versions/<id>/<id>.jar` 与 JSON 中的 sha1 一致 | 下载后校验 sha1，通过才改名落盘 |
| `libraries` 路径与 `downloads.artifact.path` 一致 | 严格按 JSON 的 path 落盘，仅过滤非 Windows 规则 |
| `assets/indexes/<assetIndex.id>.json` 与 objects | 索引与对象按 `assets/objects/<前2位>/<hash>` 存放 |
| 旧版本 natives | 解压到 `versions/<id>/<id>-natives` |
| 文件不全时“补全” | 版本 JSON 保留原始 `downloads` 段，PCL 可直接按 URL+sha1 修复缺失文件 |

实测校验（26.3）：`id` 一致、client jar 的 SHA1 与 JSON 一致、Windows 规则下 74 个库全部存在、资源索引与日志配置齐全。

> 注意：游戏本体运行需要匹配的 Java（26.3 为 Java 25），本程序只负责下载，Java 选择由 PCL 启动时决定。

## 四、.minecraft 目录结构设计

程序启动即在**自身所在目录**创建：

```
<程序所在目录>/
├─ McDownloader.exe
└─ .minecraft/
   ├─ versions/
   │  └─ <实例名>/
   │     ├─ <实例名>.json          # 版本 JSON（原版 / Fabric profile / 安装器生成）
   │     ├─ <实例名>.jar           # 原版客户端主 jar
   │     └─ <实例名>-natives/      # 旧版原生库解压目录（仅旧版本）
   ├─ libraries/                   # 所有依赖库，按 Maven 路径存放
   ├─ assets/
   │  ├─ indexes/<assetIndex>.json # 资源索引
   │  ├─ objects/<xx>/<hash>       # 资源对象（按哈希分桶）
   │  ├─ log_configs/              # 日志配置
   │  └─ virtual/<index>/          # 旧版本虚拟资源映射（按需）
   ├─ mods/                        # 模组
   ├─ shaderpacks/                 # 光影
   ├─ resourcepacks/               # 资源包
   ├─ datapacks/                   # 数据包
   ├─ saves/<存档名>/              # 地图（含 level.dat）
   └─ mcdownloader/                # 工具自身数据（不属于游戏）
      ├─ config.json               # 设置
      ├─ cache/                    # 版本清单、installer、地图 zip 等缓存
      │  └─ worlds/
      └─ logs/YYYYMMDD.log         # 日志，保留 7 天
```

设计要点：

- 完全遵循官方目录约定，下载完成后可直接被 PCL2 / HMCL / 官方启动器识别。
- `mcdownloader/` 与游戏数据隔离，删掉不影响游戏；缓存可随时手动清理。
- 下载的版本 JSON、jar、库、资源索引全部**保留**，不做自动清理。
- 工作空间根固定为 `AppContext.BaseDirectory`，不可配置到外部；所有写入路径都经过守卫校验。

---

## 五、如何参考 PCL 源码实现对应逻辑

参考对象：`D:\PCL2-Custom`（PCL2 CE 定制版，WPF / `net10.0-windows`）。

| 功能点 | PCL 源码位置 | 本项目实现 |
| --- | --- | --- |
| 版本清单与镜像 | `Modules/Minecraft/ModDownload.cs:350`（官方）、`:435`（BMCLAPI） | `VersionCatalogService.GetManifestAsync` |
| 镜像 URL 改写 | `ModDownload.cs:2207-2221` `DlSourceLauncherOrMetaGet`；`:2153` assets、`:2169` libraries | `UrlResolver.Candidates` |
| 模组镜像（MCIM） | `ModDownload.cs:2229` `DlSourceModGet`、`:2240` `DlSourceModDownloadGet` | `UrlResolver.McimRules` |
| 客户端 jar 与校验 | `ModDownload.cs:23` `DlClientJarGet`；`Modules/Base/ModBase.cs:1198` `FileChecker` | `VanillaInstaller` + `HashUtil` |
| 库文件清单 | `Modules/Minecraft/ModLibrary.cs:402` `McLibNetFilesFromInstance` | `LibraryResolver` + `VanillaInstaller` |
| 资源索引与对象 | `Modules/Minecraft/ModAssets.cs:20` `McAssetsGetIndex` | `VanillaInstaller.DownloadAssetsAsync` |
| Fabric profile | `Pages/PageDownload/ModDownloadLib.cs:2913` `McDownloadFabricLoader` | `FabricInstaller` |
| Forge/NeoForge 安装 | `ModDownloadLib.cs:1698-1743` `ForgelikeInjector`；进度与成功判据 `:1847`、`:1862` | `ForgeInstaller` |
| 并发与增量 | `Modules/Network/Loaders/LoaderDownload.cs:65,106-109,119` | `DownloadService` |
| 重试与分块 | `Modules/Network/Downloader/FileDownloader.cs:80-85,154,198` | `HttpService.DownloadFileAsync` |
| Modrinth 搜索/facets | `Modules/Minecraft/ModComp.cs:2092` | `ModrinthService.SearchAsync` |
| 内容落地目录 | `ModComp.cs:3699-3706` `_GetSubFolder`；地图解压 `:3674` | `ModrinthService.SubFolder`、`WorldImporter` |
| .minecraft 定位 | `Modules/Minecraft/ModFolder.cs:110-120` | `WorkspaceService` |
| Java 探测 | `PCL.Core/Minecraft/Java/JavaManager.cs:109`、`Scanner/*.cs` | `JavaScanner` |

**有意做的差异**（避免引入第三方闭源组件或过度耦合）：

1. **不内嵌 PCL 的 `com.bangbang93.ForgeInstaller` 入口类**，改为调用官方 installer 的 `--installClient` 无界面参数，并以“退出码 + 新版本 JSON”判定成功；失败时自动换一种参数形态重试。
2. **不接入 CurseForge**：其 API 需要 `x-api-key`（PCL 从环境变量读取，不硬编码），本项目改为 Modrinth + 地图直链/本地导入。
3. **不下载 Java**：按需求只探测本机 Java，未找到时明确提示。
4. **不做启动与账户**：不读取 `launcher_profiles.json`、不拼启动参数、不接触玩家凭据。

---

## 六、安全边界

- 所有落盘路径经 `WorkspaceService.Resolve` / `SafeCombine` 校验，必须位于 `exe 目录` 内，越界直接抛异常。
- 启动自检写权限（`.write-test`），不可写则提示后退出。
- 不写注册表、不写 `%APPDATA%`，日志与配置都在 `.minecraft/mcdownloader`。
- 下载均走 HTTPS，写盘前做哈希/大小校验；`.part` 校验失败即删除。

---

## 七、已知限制与排障

| 现象 | 处理 |
| --- | --- |
| 提示 .NET Desktop Runtime 缺失 | 安装 .NET 10 Desktop Runtime，或改用自包含发布 |
| 版本清单获取失败 | 设置里依次尝试“BMCLAPI / 官方源 / MCIM”，或检查网络与代理；程序会回落到本地缓存 |
| 想验证某个源本身 | 在设置页选择该源即可；失败仍会自动回退，日志里能看到每次尝试的 URL 与原因 |
| Forge/NeoForge 报“未找到 Java” | 安装 JDK 17/21 后在设置中“重新扫描”或手动指定 `java.exe` |
| 安装器退出码非 0 | 打开“下载任务”查看日志尾部，通常是 Java 版本过低或网络中断 |
| 地图导入失败 | 确认是含 `level.dat` 的世界压缩包，而非资源包；重名请改名 |
| 下载卡住 | 任务页取消后重试；`.part` 文件会在下次下载时自动重写 |

---

## 八、数据来源与声明

- 版本与库文件来自 Mojang 官方源及其社区镜像 BMCLAPI；内容来自 Modrinth 公开 API。
- 本项目为独立实现，仅参考 PCL2 的公开源码逻辑，不包含其任何二进制或私有密钥。
- Minecraft 为 Mojang Studios 商标，本工具与 Mojang / Microsoft 无关。
