# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | [한국어](README.ko-KR.md) | [日本語](README.ja-JP.md) | **简体中文**

ContentLink 是面向 Unity 6.6 的一整套内容工作流，构建在引擎的 **内容目录** 之上。内容目录是播放器在运行时注册并从中
加载的、已构建资源的文件夹。ContentLink 把一个资源从工程文件夹一路带到屏幕上。你 **按名称** 指代它、构建它、把它随播放器
发布或从 CDN 分发，并从脚本或 Inspector 字段加载它。当加载它的那一方消失时，它会自行释放。

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        // Assets/Content/Icons/sword.png —— 按名称加载，在这个 GameObject 被销毁时释放
        image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
    }
}
```

> **按名称指代它，从编辑器到 CDN —— 然后忘掉释放。** 一个资源恰好活得和它的所有者一样久 —— GameObject、场景、应用 ——
> 而 `Release()` 永远只是一种优化，从不是必须。

---

## 目录

1. [你能得到什么](#你能得到什么)
2. [安装](#安装)
3. [整体结构](#整体结构)
4. [教程：第一个内容目录](#教程第一个内容目录)
5. [设置目录表](#设置目录表)
6. [构建](#构建)
7. [加载](#加载)
8. [释放](#释放)
9. [从 CDN 下载内容](#从-cdn-下载内容)
10. [本地化内容](#本地化内容)
11. [编辑器模拟](#编辑器模拟)
12. [诊断](#诊断)
13. [从 Addressables、Resources 迁移](#从-addressablesresources-迁移)
14. [须知](#须知)
15. [许可证](#许可证)

---

## 你能得到什么

| 环节 | ContentLink 提供什么 |
|---|---|
| **命名** | 文件夹（`Assets/Content`）下的每个资源都从路径得到名称：`Icons/sword`、`Prefabs/Cat`、`Scenes/Arena` |
| **选取** | 持有名称（`ContentLinkSprite icon;`）或 GUID（`ContentRef<Sprite>`）的 Inspector 字段 —— 支持拖放，并以颜色显示状态 |
| **工作** | 在编辑器中，Play Mode 直接从工程加载 —— 工作时无需构建 |
| **构建** | 管理配置档、目录表、压缩和按大小分割归档的构建窗口，以及供 CI 使用的命令行 |
| **发布** | 放进播放器（`StreamingAssets`），或从 CDN 下载 —— 只下载变化的归档，支持断点续传和 MD5 校验 |
| **加载** | 精灵、预制体、音频、任意资源类型、预制体实例和场景 —— 异步或同步，按名称 |
| **释放** | 自动：每次加载都属于一个 **作用域**（GameObject、场景、应用，或你自己创建的），作用域结束时随之释放 |
| **检查** | 作用域树转储、存活引用快照，以及点名提示忘记结束的作用域的警告 |

唯一的依赖是 Unity 6000.6。

## 安装

*Window > Package Manager > + > Add package from git URL*：

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

或者，从本地检出添加到 `Packages/manifest.json`：

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

本仓库中的 `samples/LinkSample` 是一个按下面教程原样设置的小型 Unity 工程：`Assets/Content` 下的贴图、精灵图集、预制体、
场景和音频，以及注册并加载它们的引导脚本。

## 整体结构

```
 在编辑器中                                       运行时（播放器，或 Play Mode）
 ─────────────────────────────────────────────   ─────────────────────────────────────────────────────
 Assets/Content/Icons/sword.png                  1. 启动时注册一次文件夹
        │                                            ContentService.RegisterDirectory(".../MainContent")
        │  配置档 "Main"                          2. 按名称加载到作用域
        ▼                                            this.Scope().LoadAsync<Sprite>("Icons/sword[sword]")
 目录表  "Icons/sword" → 引擎 id                  3. 作用域结束 → 资源被释放
        │
        │  构建
        ▼
 StreamingAssets/MainContent/  （内容目录：构建好的资源 + 目录表）
```

| 术语 | 是什么 |
|---|---|
| **源根目录** | 要构建其中资源的文件夹。默认是 `Assets/Content`。 |
| **名称** | 用来加载的东西：资源在源根目录下的路径，去掉扩展名 —— `Icons/sword`。 |
| **目录表** | 列出每个名称及其背后引擎 id 的 `ContentCatalog` 资源。由窗口写入，从不手工编辑。构建会把它放进内容目录。 |
| **配置档** | 一个内容目录的构建设置：源根目录、输出文件夹、目录表、归档格式。 |
| **内容目录** | 构建的输出文件夹。运行时注册它，然后按名称从中加载。 |
| **作用域** | 所加载之物的所有者。作用域结束时，它加载的一切都会被释放。 |
| **链接、引用** | 持有名称（链接）或资源 GUID（引用）的序列化字段。在 Inspector 中设置。 |

## 教程：第一个内容目录

这里走一遍一个配置档、一个目录表、一次构建：随播放器一起发布的内容。

### 第 1 步 —— 把资源放到源根目录下

创建文件夹 `Assets/Content`，把内容放进去，子文件夹随意划分：

```
Assets/Content/Icons/sword.png        (Texture Type: Sprite)
Assets/Content/Prefabs/Cat.prefab
Assets/Content/Audio/click.wav
Assets/Content/Scenes/Arena.unity
```

每个文件都会得到一个名称 —— 它在 `Assets/Content` 下的路径，去掉扩展名：

| 文件 | 名称 | 加载为 |
|---|---|---|
| `Icons/sword.png` | `Icons/sword` | `Texture2D` |
| —— 它的精灵 `sword` | `Icons/sword[sword]` | `Sprite` |
| `Prefabs/Cat.prefab` | `Prefabs/Cat` | 预制体（`GameObject`） |
| `Audio/click.wav` | `Audio/click` | `AudioClip` |
| `Scenes/Arena.unity` | `Scenes/Arena` | 场景 —— 用 `LoadSceneAsync` 加载 |

这些文件所用的资源 —— 材质的贴图、预制体的网格 —— 会自动一起构建。只有按名称加载的东西才需要放在根目录下。

### 第 2 步 —— 打开构建窗口

*Window > ContentLink > Build Window*。第一次打开时，窗口里有一个配置档 `Main`：

| 字段 | 值 | 含义 |
|---|---|---|
| Name | `Main` | 配置档的名称 |
| Name style | `PathWithoutExtension` | 不带扩展名的名称（`Icons/sword`）—— 见 [名称风格](#名称风格) |
| Source root | `Assets/Content` | 要构建其中资源的文件夹 |
| Output path | `Assets/StreamingAssets/MainContent` | 构建写到哪里 —— `StreamingAssets` 会随播放器发布 |
| Categories | 一行：*(whole root)* | 整个根目录进一个目录表，创建在 `Assets/ContentCatalog.asset` |
| Archive | `None` | 平铺文件 —— 见 [归档格式](#归档格式) |
| Compression | `Uncompressed` | 只对归档生效 |
| Target | `NoTarget` | 为当前激活的平台构建 |

设置保存在 `ProjectSettings/ContentLinkBuild.asset`。请提交这个文件。

### 第 3 步 —— 创建目录表

按窗口底部的 **Update catalogs**。它扫描 `Assets/Content` 并写入 `Assets/ContentCatalog.asset`。在 Project 窗口中选中这个
资源：Inspector 会列出所有名称。

- 请提交目录表资源。Inspector 字段读取它来显示名称是否有效。
- 不要手工编辑它。每次 **Update catalogs** 和每次构建都会重写它。
- 把它放在源根目录 **之外** —— 根目录下的目录表会被拒绝。

不必在每次构建前都按 **Update catalogs**：构建会自己更新目录表。当你添加或移动了资源、想让 Inspector 立刻认出新名称时
再按它。

### 第 4 步 —— 注册目录并加载

在应用启动时、任何加载之前，注册一次内容目录。第一个场景里的一个 `Boot` 组件就够了：

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
        ContentService.RegisterDirectory(path);   // Archive = None 或 Engine
        // Archive = Packed：  await ContentService.RegisterPackedDirectoryAsync(path);  （在 async 方法中）
    }
}
```

然后按名称加载。`this.Scope()` 是这个 GameObject 的作用域：GameObject 被销毁时精灵就会释放。

```csharp
using UnityEngine;
using UnityEngine.UI;
using xpTURN.ContentLink;

public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start() => image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
}
```

或者让 Inspector 选取资源 —— 把 `Assets/Content/Icons/sword.png` 拖到字段上：

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;
    public ContentLinkSprite icon;   // Inspector 保存 "Icons/sword" + 精灵 "sword"

    async void Start() => image.sprite = await icon.LoadAsync(this);
}
```

按 Play。**无需构建** 就能运行：在编辑器中，注册配置档的输出文件夹时，该配置档由工程提供（[编辑器模拟](#编辑器模拟)）。
新增、修改或删除资源后再按 Play —— 所见即所得。

### 第 5 步 —— 构建

按 **Build 'Main'**。窗口在 *Last build* 下显示结果，`Assets/StreamingAssets/MainContent` 中出现内容目录：引擎的文件和
目录表。

然后照常构建播放器（*File > Build Profiles*）。`StreamingAssets` 会进入播放器，`Boot` 在那里找到这个文件夹。

- 内容有变化时，**每次构建播放器之前都要先构建内容**。播放器携带的是最近一次内容构建写出的东西。
- **内容目录是为一个平台构建的** —— 即编辑器中激活的平台。切换平台后，请重新构建内容。

### 第 6 步 —— 在编辑器中检查构建（可选）

关闭 *Window > ContentLink > Editor Simulation* 再按 Play：编辑器会像播放器一样加载构建好的文件夹。重新打开它，就回到无需
构建的工作方式。

这就是完整的一圈：把资源放到根目录下，按名称加载，发布前构建。

## 设置目录表

### 名称

- 名称是资源在源根目录下的路径，去掉扩展名：`Assets/Content/Icons/sword.png` → `Icons/sword`。
- **精灵** 是 `<文件>[<精灵>]`：`Icons/sword[sword]`。精灵表 `sheet.png` 给出 `Icons/sheet[run_01]`、
  `Icons/sheet[run_02]`、……
- 根目录下的 **精灵图集**（Sprite Atlas V2）还会为它打包的每个精灵再起一个经由图集的名称：`Atlas/UI[icon]`。精灵自己的
  名称和图集的名称加载的是同一个精灵。
- **场景** 像资源一样命名（`Scenes/Arena`），用 `LoadSceneAsync` 加载。
- 名称在 **配置档内唯一**。会得到同一名称的两个资源 —— 同一文件夹中的 `sword.png` 和 `sword.prefab` —— 会让构建失败，并
  指出这两个文件。
- 名称区分大小写，保持文件的拼写。韩文、假名和带重音符号的名称在每台机器上都一样可用。

### 名称风格

配置档的 **Name style** 决定名称是否保留扩展名：

| Name style | `Icons/sword.png` | 并排放着的 `sword.png` 和 `sword.prefab` |
|---|---|---|
| `PathWithoutExtension`（默认） | `Icons/sword` | 拒绝 —— 两者都是 `Icons/sword` |
| `PathWithExtension` | `Icons/sword.png` | 允许 —— `Icons/sword.png` 和 `Icons/sword.prefab` |

**尽早决定。** 更改它会在下一次目录表更新时重命名根目录下的所有名称：持有旧名称的链接、代码和数据将无法加载。
`ContentRef` 字段持有 GUID，不受影响。在配置档和目录表不一致期间，窗口会显示提示。

### 类别：一个目录中的多个目录表

默认情况下整个根目录进一个目录表。大型工程可以按子文件夹拆分根目录，每个子文件夹有自己的目录表文件 —— 文件更小，在不同
文件夹工作的人也不会改到同一个目录表。

在构建窗口的 **Categories** 中：

1. 用 **−** 删除 *(whole root)* 行；如果一个目录表就够了，保留它即可。
2. 在 **Add category** 中输入根目录的一个子文件夹 —— `Characters` —— 然后按 **Add**。`UI` 和 `Levels` 同样操作。
3. **Catalog** 列留空：每个目录表会在第一次构建或目录表更新时创建在 `Assets/ContentCatalogs/<配置档>.<类别>.asset`（窗口会
   显示路径）。也可以选一个已有的 `ContentCatalog`（*Assets > Create > xpTURN > ContentLink > Content Catalog*）。

```
配置档 "Main"                  源根目录 Assets/Content
  类别  Characters        →    Assets/ContentCatalogs/Main.Characters.asset
  类别  UI                →    Assets/ContentCatalogs/Main.UI.asset
  类别  Levels            →    Assets/ContentCatalogs/Main.Levels.asset
```

- **类别不改变名称。** `Assets/Content/UI/logo.png` 无论在哪个目录表里都是 `UI/logo`。
- 一个配置档的所有目录表都进入 **同一个** 内容目录：注册一次，所有名称都能加载。
- 类别不能重叠：*(whole root)* 行与子文件夹行不能同时使用，`UI` 与 `UI/Icons` 也不能。
- 不被任何类别覆盖的根目录下资源 **不会被构建** —— 构建会记录一条带有其数量的警告。

### 多个配置档：分别注册的内容目录

每个配置档构建一个内容目录，各自单独注册。当内容要分开发布或下载时使用多个 —— 本体和 DLC 包，或者一个
[区域设置](#本地化内容)。

| 配置档 | 源根目录 | 输出路径 | Archive |
|---|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` | `Packed` |
| `Dlc1` | `Assets/Dlc1` | `Build/Content/Dlc1` | `Packed` |

要添加一个，在 **New profile** 中输入名称并按 **Add**。新配置档的初始设置是：输出路径 `Build/Content/<名称>`、LZ4 压缩的
`Packed` 归档，以及位于 `Assets/ContentCatalogs/<名称>.asset` 的一个目录表。**Set active** 选择 *Window > ContentLink*
菜单项要构建的配置档。

- 源根目录要 **并列** 放置，不要互相嵌套。
- 一个根目录下的资源引用另一个根目录下的资源时，会把它的 **副本** 带进自己的目录。共享的资源请按名称加载，不要跨根目录
  引用。

## 构建

### 构建窗口

| 字段 | 填什么 |
|---|---|
| **Content version** | 可选。刻进每个 Packed 构建的标签 —— 下载时用来把本体和它的区域设置配成对 |
| **Name** | 配置档的名称。`Packed` 配置档只能用字母、数字、`.`、`-` 和 `_`（它会成为归档的名称） |
| **Name style** | 见 [名称风格](#名称风格) |
| **Source root** | 要构建的文件夹（用 **…** 选择） |
| **Output path** | 构建写到哪里（用 **…** 选择）—— 见 [输出放在哪里](#输出放在哪里) |
| **Target** | `NoTarget` 为当前激活的平台构建。其他值则在其他平台上拒绝构建 |
| **Categories** | 子文件夹及其目录表 —— 见 [类别](#类别一个目录中的多个目录表) |
| **Archive** | `None`、`Engine`、`Packed` —— 见 [归档格式](#归档格式) |
| **Max archive MB** | 仅 `Packed`：一个归档大约装多少内容（默认 20） |
| **Compression** | `Uncompressed`、`LZ4`、`LZMA` —— 仅对归档。`LZ4` 是稳妥的默认值 |
| **Clean build cache** | 忽略引擎的构建缓存，全部重新构建 |
| **Disable type trees** | 输出更小，但 **只能在完全相同的 Unity 版本上** 加载 |
| **Fail when errors are logged** | 默认开启：构建期间记录了错误，构建即失败 |
| **Partitioner** | 仅 `Packed`：文件如何分组到归档（保持默认即可） |
| **Post-processors** | 构建之后运行的步骤 —— 见 [构建之后](#构建之后) |

底部：**Update catalogs**（只写目录表）、**Build '\<配置档\>'** 和 **Build all**。问题会在构建任何东西之前列在这些按钮
上方；类别的问题显示在它所在行的下方。

### 归档格式

| Archive | 输出是什么样 | 用什么注册 | 何时使用 |
|---|---|---|---|
| `None` | 引擎的平铺文件 | `ContentService.RegisterDirectory(path)` | 内容随播放器发布，并且想要最简单的设置 |
| `Engine` | 一个 `content0.archive` | `ContentService.RegisterDirectory(path)` | 播放器里只放一个压缩文件 |
| `Packed` | 若干个大约 *Max archive MB* 的归档，外加 `archives.json` 和 `archives.hash` | `await ContentService.RegisterPackedDirectoryAsync(path)` | 内容要下载，或者既要压缩又要拆分以便打补丁 |

`Packed` 构建是 **确定性的**：相同的输入会打包成逐字节相同的归档，小改动只会触及少数几个归档 —— 其余的保持名称和字节
不变，所以补丁下载量很小。*Max archive MB* 计的是压缩前的内容量；使用 LZ4 时，磁盘上的文件大约是它的一半。

### 输出放在哪里

| 输出路径 | 会怎样 |
|---|---|
| `Assets/StreamingAssets/` 之下 | 文件夹随播放器发布。注册 `Application.streamingAssetsPath/<文件夹>` |
| `Assets/` 之外（例如 `Build/Content/Main`） | 不会被导入，也不在播放器中 —— 上传到服务器（[CDN](#从-cdn-下载内容)）或自行复制 |

输出文件夹必须是空的，或者是上一次的内容构建：装有其他东西的文件夹不会被动，构建失败。在引擎运行之前失败的构建会保留
之前的输出。

### 设置示例

**全部放在应用里** —— 最简单：

| 字段 | 值 |
|---|---|
| Output path | `Assets/StreamingAssets/MainContent` |
| Archive | `None`（想要更小的文件就用 `Packed` + `LZ4`） |

```csharp
var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
ContentService.RegisterDirectory(path);                      // Archive = None
// await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

**从 CDN 下载** —— 无需重新构建应用即可更新：

| 字段 | 值 |
|---|---|
| Output path | `Build/Content/Main` |
| Archive | `Packed`，Compression `LZ4`，Max archive MB `20` |

每次构建后上传输出文件夹，启动时下载 —— 见 [从 CDN 下载内容](#从-cdn-下载内容)。

**本体加可下载的包** —— 每个各用一个配置档，如 [多个配置档](#多个配置档分别注册的内容目录) 所示。

### 常见构建错误

| 消息 | 解决方法 |
|---|---|
| `source root '…' is not a folder` | 创建该文件夹，或修正 *Source root* |
| `duplicate name '…': … / …` | 把两个文件之一改名，或把配置档改为 `PathWithExtension` |
| `'…' is a ContentCatalog under the source root` | 把目录表资源移出根目录 |
| `categories '…' and '…' overlap` | 删除 *(whole root)* 行，或内层的子文件夹行 |
| `output folder '…' is neither empty nor a previous content build` | 把 *Output path* 指向空文件夹，或清空它 |
| `… asset(s) under '…' belong to no category`（警告） | 为该文件夹添加类别 —— 那些资源没有被构建 |
| `'…' is a V1 sprite atlas under the source root` | 把图集迁移到 Sprite Atlas V2 —— 见 [须知](#须知) |
| `profile is for … but the active build target is …` | 切换平台，或把 *Target* 设为 `NoTarget` |

### 从命令行构建

在 CI 中，以批处理模式构建一个配置档：

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget Android -logFile build.log \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main
```

| 选项（`-contentlink.<键> <值>`） | 值 |
|---|---|
| `profile` | 配置档名称，或用 `all` 表示所有配置档。省略时为激活的配置档 |
| `output`、`source` | 覆盖本次运行的路径 |
| `archive` | `None`、`Engine`、`Packed` |
| `compression` | `Uncompressed`、`LZ4`、`LZMA` |
| `maxArchiveMB` | ≥ 1 的整数 |
| `contentVersion` | 刻进本次运行的 Packed 构建的内容版本 |

覆盖只对本次运行有效：设置文件不会被改动。构建失败、未知的选项或配置档会以退出码 **1** 结束。请先在其他编辑器中关闭该
工程：批处理模式需要工程锁。

`xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` 接受相同的选项，只写目录表 —— 适合 CI 在构建播放器前只需要最新
目录表的情况。在本仓库中，`tools/build-content.sh` 封装了这两个调用。

在编辑器脚本中，`ContentBuildRunner.Build(profile)` 和 `ContentBuildRunner.BuildAll()` 做同样的事，并返回
`ContentBuildResult`。

### 构建之后

后处理器会在添加了它的配置档每次构建之后运行 —— 这是上传或复制输出的地方。把它放在 Editor 程序集中：

```csharp
using xpTURN.ContentLink.Editor;

public sealed class UploadStep : IContentBuildPostProcess
{
    public void OnBuilt(ContentBuildResult r)
    {
        // r.ProfileName, r.Target, r.OutputPath, r.Files, r.Archives, r.Milliseconds
        UnityEngine.Debug.Log($"{r.ProfileName}: {r.Files.Length} files in {r.OutputPath}");
    }
}
```

然后在构建窗口的 **Post-processors** 中添加它；多个后处理器按那里列出的顺序运行。抛出异常的步骤会让构建失败，并保留
输出。

## 加载

### 先注册

在启动时、**第一次加载之前**，把每个内容目录注册一次：

```csharp
int dir = ContentService.RegisterDirectory(path);                      // Archive = None 或 Engine
// int dir = await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

两者都返回目录 id（文件夹无法使用时返回 `-1` 并记录一条错误）。同一文件夹注册两次会被拒绝。
`ContentService.UnregisterDirectory(dir)` 会把它移除 —— 只要它的资源还被持有，就会被拒绝。

### 作用域

每次加载都进入一个 **作用域**。作用域结束时，它加载的东西会被释放。

| 作用域 | 如何获得 | 何时结束 |
|---|---|---|
| GameObject | `this.Scope()` / `gameObject.Scope()` | GameObject 被销毁时 |
| Scene | `ContentScope.ForScene(scene)` | 场景卸载时 |
| Explicit | `parent.CreateChild("Stage3")` | 你调用 `Dispose()` 时 —— 或其父作用域结束时 |
| Root | `ContentScope.Root` | 应用退出时 |

```csharp
// GameObject —— 日常情况
var sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");

// Scene —— 与场景同寿
var sky = await ContentScope.ForScene(gameObject.scene).LoadAsync<Material>("Materials/Sky");

// Explicit —— 关卡、界面、弹窗：由你决定何时结束
var stage = ContentScope.Root.CreateChild("Stage3");
await stage.LoadAsync<AudioClip>("Audio/boss");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... 关卡进行中 ...
stage.Dispose();   // 关卡加载的一切都归还

// Root —— 整个应用期间
var font = await ContentScope.Root.LoadAsync<Font>("Fonts/Main");
```

在任何作用域上：

```csharp
var mat  = scope.Load<Material>("Materials/Steel");   // 同步
bool has = scope.Contains("Icons/sword");             // 已在这里加载：再次加载没有代价
scope.Release("Icons/sword");                         // 可选：提前归还
```

- 在一个作用域里把同一名称加载两次，只持有一次 —— 每帧都加载也没问题。
- 多个作用域可以加载同一名称：资源只加载一次，在最后一个作用域放手时释放。
- 要把资源交给另一个作用域，请用 `source.ShareTo(target, name)` 或 `target.Adopt(asset)`。

### Inspector 字段：链接

**链接** 持有一个名称。在 Inspector 中把源根目录下的资源拖到它上面：

```csharp
public ContentLinkSprite icon;              // 还有 ContentLinkGameObject、ContentLinkTexture2D
public ContentLink<AudioClip> music;        // 任意资源类型

image.sprite = await icon.LoadAsync(this);  // 加载到这个 GameObject 的作用域
var clip = music.Load(this);                // 同步

// 更换物品：归还旧的，加载新的
icon.Release();
icon.SetName("Icons/shield", "shield");
image.sprite = await icon.LoadAsync(this);
```

字段的颜色表示状态：普通 —— 正常；**琥珀色** —— 还不在目录表中（按 **Update catalogs**）；**红色** —— 该名称加载的东西
字段无法容纳。

### Inspector 字段：按 GUID 引用

**引用** 持有资源的 GUID 而不是名称：移动或重命名文件后依然有效。

```csharp
public ContentRef<Sprite> icon;
public ContentSceneRef level;

image.sprite = await this.Scope().LoadAsync(icon);
var scene = await this.Scope().LoadSceneAsync(level);
```

同一资源的引用和名称加载的是同一个对象：`Release(icon)` 也会释放 `LoadAsync<Sprite>("Icons/sword")` 加载的东西。

### 预制体实例

```csharp
var cat = await this.Scope().InstantiateAsync("Prefabs/Cat", parent);
```

只要实例还活着，预制体就保持加载；销毁实例它就被释放。用 `Object.Instantiate(cat)` 制作的克隆也会让它保持加载，所以不需要
生成器类。

### 场景

```csharp
// 由作用域拥有：作用域结束时卸载（additive）
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
this.Scope().ReleaseScene(arena);   // 可选：提前卸载

// 来自 Inspector 字段
public ContentSceneLink level;
var scene = await level.LoadAsync(this);

// 无所有者，任意模式 —— 自己用 SceneManager.UnloadSceneAsync 卸载
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

### Resources 文件夹

以 `res:` 开头的名称从播放器中的 `Resources` 文件夹加载 —— 经过同样的作用域和字段，所以工程可以一次一个调用地从
`Resources.Load` 迁移过来：

```csharp
var boot  = this.Scope().Load<TextAsset>("res:Config/boot");                 // Resources/Config/boot.txt
var logo  = await this.Scope().LoadAsync<Sprite>("res:UI/logo[logo]");       // 精灵：[它的名称]
var enemy = await this.Scope().InstantiateAsync("res:Prefabs/Enemy");
```

预制体及其所用的资源只有通过 `ContentService.SweepResources()` 才会离开内存 —— 请在可以接受卡顿的地方调用，比如加载画面。

### 原始句柄

供自己管理生命周期的代码（对象池、流式加载）使用：

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");
var tex = h.Asset;
h.Dispose();   // 归还 —— 或者交给一个作用域：scope.Attach(h)
```

## 释放

大多数代码从不调用 `Release`：

| 情况 | 怎么做 |
|---|---|
| 组件、弹窗、关卡或场景的内容 | 什么都不用做 —— 加载到相应的作用域 |
| 资源很多的关卡或界面 | 一个 Explicit 作用域，关卡结束时 `Dispose()` |
| 复用单元格的滚动列表 | 每个单元格一个子作用域，单元格被复用时 Dispose |
| 更换装备或皮肤 | 加载新的之前先 `Release(name)` 或 `link.Release()` |
| 把资源交给另一个系统 | `ShareTo(target, name)` 或 `target.Adopt(asset)` |

**工作原理。** 作用域结束时 —— GameObject 被销毁、场景被卸载、调用了 `Dispose()`、应用退出 —— 它会放手所加载的一切。
不再有人持有的资源会 **每帧一次、在帧末** 被释放，在你的所有代码运行之后，绝不会发生在代码中途。在同一帧内放手再加载，
不会重新加载任何东西。

**忘记结束的作用域。** 场景卸载或应用退出时仍然打开的 Explicit 作用域会被代为结束，并连同其路径记录为一条警告
（`Root/Field/ForgottenPopup`）。设置 `ContentService.CaptureStackTrace = true` 可以看到它是在哪里创建的。

## 从 CDN 下载内容

`Packed` 配置档的输出可以下载到缓存文件夹，并从那里注册。

**1. 构建并上传。** 使用输出路径在 `Assets/` 之外的 `Packed` 配置档。每次构建后，把整个输出文件夹 —— 归档、
`archives.json` 和 `archives.hash` —— 上传到每个平台、每个应用版本各一个 URL。归档名称带有其内容的哈希，可以永久缓存；
请在 CDN 上给 `archives.json` 和 `archives.hash` 设置较短的缓存时间。

**2. 启动时检查、下载、注册：**

```csharp
var options = new ContentRemoteOptions
{
    BuiltInDirectory = Path.Combine(Application.streamingAssetsPath, "MainContent"),   // 可选：应用自带的一份副本
    RetryCount = 3,
    Timeout = 30,
};
var remote = await ContentRemote.CheckAsync("Main", $"https://cdn.example.com/content/{platform}/Main", options);

if (remote.BytesToDownload > 0)
    ShowDownloadSize(remote.BytesToDownload);

if (await remote.DownloadAsync())   // 轮询 remote.Status 获取进度
{
    await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
    remote.CleanCache();            // 删除新构建不再使用的归档
}
else
    Debug.LogError(remote.Error);
```

- 只下载缓存中缺少的归档。中断的下载会续传；MD5 不匹配的文件绝不会被使用。
- 离线时，检查会回退到缓存最近一次下载的内容，然后是 `BuiltInDirectory`。`remote.Source` 说明用了哪一个，
  `remote.Error` 说明原因。
- 应用已经自带的归档（`BuiltInDirectory`）会被复制而不是下载。
- 一个配置档的缓存文件夹 **或** 内置文件夹只注册其中之一，不要两个都注册：引擎会拒绝同一构建的第二个目录。
- 在文件夹已注册的情况下下载是安全的；更新会在下一次注册该文件夹时生效。
- 在编辑器中开启模拟时，检查会直接由工程应答，不发出任何请求。要试真实下载，请关闭模拟。

| 选项 | 默认 | 含义 |
|---|---|---|
| `CacheRoot` | `persistentDataPath/ContentLink` | 缓存放在哪里：`<CacheRoot>/<profile>` |
| `BuiltInDirectory` | 无 | 应用自带的 Packed 构建 |
| `Timeout` | `0`（无） | 多少秒没有新字节就重试请求 |
| `RetryCount` | `0` | 每个请求的重试次数 |
| `VerifyCachedFiles` | `false` | 也校验已缓存文件的 MD5 |
| `WebRequestOverride`、`TransformUrl` | 无 | 调整每个请求（请求头）或 URL（签名） |

## 本地化内容

区域设置是第二个配置档，只在相同路径下放语言要替换的文件。在本体之后注册时，两者共有的名称由区域设置的文件应答，其余名称
仍是本体的。

| 配置档 | 源根目录 | 输出路径 |
|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` |
| `Main.ko` | `Assets/Content.ko` | `Assets/StreamingAssets/MainContent.ko` |

`Assets/Content.ko/Icons/flag.png` 替代 `Assets/Content/Icons/flag.png` —— 两者都是 `Icons/flag`。把区域设置的根目录放在
本体根目录 **旁边**，并让两个配置档使用相同的名称风格。

```csharp
static string Dir(string name) => Path.Combine(Application.streamingAssetsPath, name);

ContentService.RegisterDirectory(Dir("MainContent"));      // 先注册本体
ContentService.RegisterDirectory(Dir("MainContent.ko"));   // 然后是区域设置，在第一次加载之前

var flag = await this.Scope().LoadAsync<Sprite>("Icons/flag");   // 韩语的旗帜
```

**运行时切换语言。** 把区域设置要替换的东西加载到一个作用域里，这样切换时可以一次全部放手：

```csharp
ContentScope localized = ContentScope.Root.CreateChild("Localized");
int locale = -1;

bool SwitchLocale(string code)   // "ko"、"ja"、…… —— 只用本体时为 null
{
    localized.Dispose();                                    // 放手旧区域设置的资源
    if (locale >= 0 && !ContentService.UnregisterDirectory(locale))
        return false;                                       // 仍被某处持有 —— 错误信息会说明是什么
    locale = code == null ? -1 : ContentService.RegisterDirectory(Dir("MainContent." + code));
    localized = ContentScope.Root.CreateChild("Localized");
    return true;                                            // 重新加载：名称现在以新语言应答
}
```

使用 Unity 的 Localization 包时，把字符串翻译交给它，并在 `LocalizationSettings.SelectedLocaleChanged` 中调用
`SwitchLocale`。

## 编辑器模拟

*Window > ContentLink > Editor Simulation*（默认开启）。在 Play Mode 中注册配置档的输出文件夹时，该配置档 **由工程提供**：
新增、修改、删除的资源所见即所得，无需构建。文件夹甚至不必存在。

- 和播放器一样，仍然需要注册。
- 不会向磁盘写入任何东西。
- 在编辑器中，已释放的资源会留在内存里 —— 请关闭模拟来检查内存和释放行为。
- 不使用归档。要测试真实的构建、归档和下载，请关闭模拟。
- Play Mode 结束时，控制台会列出你加载过但还没有任何目录表收录的名称：发布前请构建。

## 诊断

- *Window > ContentLink > Dump Scope Tree*（Play Mode），或 `ContentScope.DumpTree()`：

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```

- `ContentService.Snapshot(list)` —— 当前持有的每个引用：它的名称、获取时的帧，以及获取的位置
  （`ContentService.CaptureStackTrace = true` 时）
- `ContentService.LiveAssetCount` —— 当前持有的资源数
- `ContentScope.DefaultWarnThreshold` —— 一个作用域持有的资源超过这个数时发出警告
- 构建窗口显示最近一次构建；控制台每次构建留一行 `[ContentLink] built …`

## 从 Addressables、Resources 迁移

| Addressables | ContentLink |
|---|---|
| 组和包 | 配置档和类别；`Packed` 归档 |
| `Addressables.LoadAssetAsync` + `Release` | `scope.LoadAsync` —— 释放是自动的 |
| `AssetReference` / `AssetReferenceSprite` 字段 | `ContentRef<T>` —— 只需改字段类型，保存的 GUID 原样沿用 |
| `ref.LoadAssetAsync()` + `ref.ReleaseAsset()` | `this.Scope().LoadAsync(ref)` |
| `InstantiateAsync` + `ReleaseInstance` | `scope.InstantiateAsync` —— 销毁实例 |
| *Use Asset Database* 播放模式 | 编辑器模拟 |
| `CheckForCatalogUpdates` / `UpdateCatalogs` | `ContentRemote.CheckAsync` |
| `GetDownloadSizeAsync` / `DownloadDependenciesAsync` | `remote.BytesToDownload` / `remote.DownloadAsync()` |
| `ClearDependencyCacheAsync` | `ContentRemote.ClearCache(profile)` / `remote.CleanCache()` |

从 `AssetReference` 沿用过来的 `ContentRef<T>`，其资源必须位于内容根目录之下（移动过去 GUID 不变）。

| Resources | ContentLink |
|---|---|
| `Resources.Load<TextAsset>("Config/boot")` | `scope.Load<TextAsset>("res:Config/boot")` |
| `Resources.Load<Sprite>("UI/logo")` | `scope.Load<Sprite>("res:UI/logo[logo]")` |
| `Resources.UnloadAsset(asset)` | 无需 —— 作用域负责释放 |
| `Resources.UnloadUnusedAssets()` | `ContentService.SweepResources()` |

## 须知

- **仅限主线程。** 在主线程上加载、注册和取消。
- **一个构建对应一个平台。** 为一个平台构建的内容目录在另一个平台上可能无法加载。
- **精灵图集** 需要把 *Project Settings > Editor > Sprite Packer > Mode* 设为 Sprite Atlas V2。旧版（V1）的
  `.spriteatlas` 文件在内容根目录下会被拒绝 —— 请迁移到 V2。
- 作为子资源加载的 **只有精灵**。模型文件中的网格不能单独加载。
- **场景不共享。** 一个场景加载两次会得到两个实例，各自单独卸载。
- **非激活的克隆。** 以非激活状态创建的克隆在第一次唤醒之前什么也不持有。如果原件可能先被销毁，请在 `Instantiate` 之后
  立刻调用 `clone.Scope()`。
- **引用和名称共用一个槽位。** 释放其中一个也会释放另一个；两个字段需要分别释放时，请使用链接或子作用域。
- **让内容根目录与 `Resources` 文件夹分开。** 同时位于两者中的文件会以两个名称被构建两次。
- **不要在内容场景加载过程中停止 Play Mode** —— 无论是否使用 ContentLink，6000.6 编辑器在这种情况下都可能崩溃。
- **在编辑器中 `Resources.UnloadAsset` 不会卸载任何东西。** 请在播放器中确认 `res:` 资源的释放。

## 许可证

Apache License 2.0
