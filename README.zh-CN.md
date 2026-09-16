# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | [한국어](README.ko-KR.md) | [日本語](README.ja-JP.md) | **简体中文**

ContentLink 从 Unity 6.6 内置的 **内容目录** 中 **按名称** 加载游戏内容 —— 精灵、预制体、场景、音频 ——
并接管了所有加载系统都留给你的那一件事：决定加载后的资源何时可以释放。

**问题所在。** 用 `Resources`、AssetBundle 或 Addressables，每一次加载都必须在每个脚本里手工配上一次释放。
漏掉一次，内存就一直涨到应用退出；释放得太早，贴图就不见了。找出元凶的工具都是事后诸葛，靠在 Profiler 里猜。

**ContentLink 做什么。** 每一次加载都属于一个 **作用域** —— 发出请求的 GameObject、它所在的场景、整个应用，
或者你为某个关卡、某个弹窗自己创建的一个。作用域结束时（对象被销毁、场景被卸载、应用退出、你把它 Dispose 掉），
它持有的一切都会被释放。加载完就完事了：忘掉的一次释放只多占一点峰值内存，绝不会影响正确性。同样的思路也覆盖
序列化引用（带 Inspector 绘制器的 `ContentLinkSprite icon;`）、实例化的预制体和内容场景。

**盒子里还有什么。** 一条为内容目录本身服务的构建管线 —— 保存在 `ProjectSettings` 中的配置档、一个构建窗口、
面向下载管线的确定性按大小分割的归档、后处理钩子和命令行 —— 以及一种编辑器模式：Play Mode 直接从工程加载，
工作时什么都不用构建。唯一的依赖是 Unity 6000.6。

> **加载它，然后忘掉释放。** 一个资源恰好活得和它的所有者一样久 —— GameObject、场景、应用 ——
> 而 `Release()` 永远只是一种优化，从不是必须。

---

## 目录

1. [你得到什么](#你得到什么)
2. [要求与安装](#要求与安装)
3. [五个概念](#五个概念)
4. [快速开始](#快速开始)
5. [名称](#名称)
6. [构建内容](#构建内容)
7. [加载内容](#加载内容)
8. [何时调用 Release](#何时调用-release)
9. [释放是如何发生的](#释放是如何发生的)
10. [编辑器模拟](#编辑器模拟)
11. [诊断](#诊断)
12. [从 AssetLink 或 Addressables 迁移](#从-assetlink-或-addressables-迁移)
13. [注意事项与限制](#注意事项与限制)
14. [仓库结构与测试](#仓库结构与测试)
15. [许可证](#许可证)

---

## 你得到什么

| 领域 | 功能 |
|---|---|
| 加载 | `scope.LoadAsync<T>("Icons/sword")` —— 资源属于作用域；没有什么需要释放 |
| 作用域 | 自动结束的 Root / Scene / GameObject / Explicit 作用域；按作用域去重；带路径的泄漏警告 |
| 序列化引用 | `ContentLink<T>` 与 `ContentSceneLink` 字段，Inspector 绘制器负责填入并校验名称 |
| 实例 | `scope.InstantiateAsync("Prefabs/Cat")` —— 预制体引用随实例及其克隆体一起存在 |
| 场景 | 由作用域或链接拥有的内容场景；任意模式的非拥有加载；跟踪已打开的场景 |
| 确定性释放 | 向引擎的释放在每帧的一个点（`Collect()`）发生，绝不在你的代码中途 |
| 构建配置档 | 源根目录、输出、每个目录多个类别（目录表）、归档模式、压缩、引擎选项 —— 保存在 `ProjectSettings` |
| 构建窗口 | *Window > ContentLink > Build Window*：可撤销地编辑配置档、查看校验信息、构建一个或全部 |
| 打包归档 | 构建被确定性地分割成约 `MaxArchiveMB` 大小的归档；运行时把它们重新挂载为一个目录 |
| 后处理 | `IContentBuildPostProcess` 各步骤在构建后按你排列的顺序运行 |
| 命令行 | 通过 `-executeMethod` 调用 `ContentBuildCLI.Build`，支持覆盖参数、`profile all`、退出码；`tools/build-content.sh` |
| 编辑器模拟 | Play Mode 中配置档的目录由工程提供：新增、修改、删除的资源即所见，无需构建 —— 什么都不写入 |
| 诊断 | 作用域树转储、引用快照、上次构建摘要 |

## 要求与安装

- Unity **6000.6** 或更高（`Unity.Loading` 内容目录 API）。
- 没有其他依赖。

**从 git 安装** —— *Window > Package Manager > + > Add package from git URL*：

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

**从本地检出安装** —— 在 `Packages/manifest.json` 中：

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

本仓库中的 `samples/LinkSample` 就是一个正是这样接好线的 Unity 工程。

## 五个概念

| 概念 | 它是什么 |
|---|---|
| **内容目录** | 引擎从你的资源构建出的一个文件夹（`BuildPipeline.BuildContentDirectory`）。运行时注册它并从中加载。它是为一个构建目标构建的。 |
| **目录表** | 构建放进目录里的 `ContentCatalog` 资源：名称及其映射到的引擎 id 的列表。一个目录可以携带多个目录表（每个 *类别* 一个）。 |
| **名称** | 你用来加载的东西：资源相对于源根目录的路径，去掉扩展名 —— `Icons/sword`。子资源是 `Icons/sword[sword]`，场景是 `Scenes/Arena`。 |
| **作用域** | 你加载的一切的所有者。作用域构成一棵树（Root → Scene → GameObject → Explicit）并自动结束。 |
| **链接** | 持有一个名称的序列化字段（`ContentLinkSprite icon;`）。在 Inspector 中设置，带上所有者加载。 |

## 快速开始

**1. 把资源放到源根目录下** —— 默认是 `Assets/Content`：

```
Assets/Content/Icons/sword.png        →  "Icons/sword"            (Texture2D)
                                         "Icons/sword[sword]"     (它的 Sprite 子资源)
Assets/Content/Prefabs/Cat.prefab     →  "Prefabs/Cat"
Assets/Content/Scenes/Arena.unity     →  "Scenes/Arena"
```

**2. 构建** —— *Window > ContentLink > Build Window*。第一次打开时窗口会显示一个 `Main` 配置档：
源根目录 `Assets/Content`，一个覆盖整个根目录的类别，输出 `Assets/StreamingAssets/MainContent`，归档 `None`
（平铺文件）。按下 **Build 'Main'**。（*Window > ContentLink > Build Content Directory* 不打开窗口，直接构建
活动配置档。）

`StreamingAssets` 随播放器一起发布，所以下面的代码能找到这个文件夹。若用于下载管线，把 `Archive` 设为 `Packed`，
输出到 `Assets/` 之外，并自行分发归档 —— 见 [构建内容](#构建内容)。

**3. 启动时注册一次目录**，在任何加载之前：

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    async void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");

        // Archive = None（平铺文件，迁移而来的 Main 配置档）或 Engine（单个 content0.archive）：
        ContentService.RegisterDirectory(path);

        // Archive = Packed（新配置档的默认值）：几个归档 + archives.json
        // await ContentService.RegisterPackedDirectoryAsync(path);
    }
}
```

**4. 加载到作用域里。** 没有什么需要释放：精灵随 GameObject 一起消失。

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

**5. 或者使用序列化链接**，在 Inspector 里选资源：

```csharp
public ContentLinkSprite icon;                       // 把 Assets/Content/Icons/sword.png 拖上去 → "Icons/sword[sword]"
async void Start() => image.sprite = await icon.LoadAsync(this);   // this = 这个 GameObject 的作用域（与第 4 步相同）；也可以传任何别的作用域
```

在编辑器里可以完全跳过第 2 步：启用 [编辑器模拟](#编辑器模拟)（默认开启）后，注册某个配置档的输出文件夹时
Play Mode 会由工程来提供该目录 —— 新增、修改、删除的资源即所见，那个文件夹甚至不必存在。发布时再构建。

## 名称

- 名称是 **相对于配置档源根目录** 的资源路径，去掉扩展名：`Icons/sword`。
  类别不会改变名称 —— 不管 `Icons/sword` 在哪个目录表里，它都是 `Icons/sword`。
- **子资源** 是 `<主资源>[<子资源>]`：`sword.png` 里的精灵 `sword` 是 `Icons/sword[sword]`；精灵表 `sheet.png`
  给出 `Icons/sheet[run_01]`、`Icons/sheet[run_02]`、…
- **场景** 的命名和资源一样：`Scenes/Arena`。资源和场景共用一个名称空间。
- 名称 **在配置档内唯一**。会得到同一名称的两个资源（两个类别重叠，或两个文件夹里同名）会让构建失败，并在
  控制台里打印两条路径。
- Inspector 绘制器显示名称的状态：为空时无色，还没有任何目录表认识的名称显示 **琥珀色**（构建，或运行
  *Update Content Catalog*），名称解析到字段装不下的东西时显示 **红色**。

## 构建内容

### 配置档

一个 **配置档** 描述一个内容目录。配置档保存在 `ProjectSettings/ContentLinkBuild.asset`（`ContentBuildSettings`）中，
在构建窗口里编辑。有一个配置档是 **活动的**：菜单项和旧的静态 API 作用于它。

| 字段 | 默认值（新配置档） | 含义 |
|---|---|---|
| `Name` | —— | 唯一；活动配置档和命令行按它选取 |
| `SourceRoot` | `Assets/Content` | 名称相对于的文件夹 |
| `OutputPath` | `Build/Content/<Name>` | 目录写到哪里。默认在 `Assets/` 之外 —— 见下文 |
| `Catalogs` | 一个类别，整个根目录 | 每个类别 = 根目录的一个子文件夹 + 它填充的目录表资源 |
| `Archive` | `Packed` | `None`（平铺） · `Engine`（引擎的单一归档） · `Packed`（按大小分割的归档） |
| `MaxArchiveMB` | `20` | `Packed`：归档在多大时封口，误差约一个文件 |
| `Compression` | `LZ4` | `Uncompressed` · `LZ4` · `LZMA` —— 只对归档生效；平铺构建总是未压缩 |
| `CleanBuildCache` | 关 | 引擎选项：忽略构建缓存 |
| `DisableWriteTypeTree` | 关 | 更小的输出，但 **只能在完全相同的引擎版本** 上加载（`SerializeUnityVersion` 会自动加上） |
| `FailBuildWhenErrorsLogged` | 开 | 构建期间记录的错误会让构建失败 |
| `Target` | `NoTarget` | `NoTarget` = 任何当前活动的构建目标；其他值在别的目标上拒绝构建 |
| `PostProcessors` | 无 | `IContentBuildPostProcess` 实现的类型名，按运行顺序排列 |

**类别。** 添加一个子文件夹（`Icons`、`Prefabs`、`Levels/Forest`，或用 `""` 表示整个根目录），构建就会把该文件夹的
资源放进它们自己的目录表；一个配置档的所有目录表进入 **同一个** 目录，所以运行时看到的是一个名称空间。重叠的类别、
放在自己根目录下面的目录表、重复的名称，都会在写入任何东西之前被拒绝；含有路径字符（`/ \ : * ? " < > |` ——
名称会进入归档名和默认路径）的配置档名、末尾带分隔符的源根目录、位于源根目录之内或包含源根目录的输出文件夹也一样。
新类别的目录表资源会在第一次构建时创建在 `Assets/ContentCatalogs/<配置档>.<类别>.asset`（文件夹缺失时一并创建）；
你也可以把字段指向一个已有的目录表。

**输出文件夹。** 写入任何东西之前先检查该文件夹：只接受空文件夹或以前的内容构建（有 `BuildManifestHash.txt`）；
装着别的东西的文件夹会原封不动，构建失败。清空本身发生在引擎运行之前的那一刻，所以更早失败的构建 —— 校验、
目录表 —— 会让以前的输出仍然可用。
位于 `Assets/` 之下的输出（例如 `StreamingAssets`）会被 Unity 导入并在构建后刷新；`Assets/` 之外的输出不会，
这正是你单独分发的内容所需要的。

**构建目标。** 内容目录是为编辑器的 **活动构建目标** 构建的，在别的目标上可能加载不了。窗口、日志和上次构建
摘要总会说明一次构建是为哪个目标做的。

**从静态构建器迁移。** 在脚本里设置过 `ContentDirectoryBuilder.SourceRoot` / `CatalogPath` / `OutputPath` 的工程
可以继续工作：没有设置的工程会得到一个由那些默认值生成的 `Main` 配置档（`Assets/Content` →
`Assets/StreamingAssets/MainContent`，平铺，未压缩），静态属性则保留为活动配置档的一个视图。给它们赋值只在本次
编辑器会话内覆盖配置档 —— 窗口会这样提示。

### 归档模式与注册

| `Archive` | 写出什么 | 用什么注册 |
|---|---|---|
| `None` | 引擎的平铺文件（`*.cf`、`*.resS`、清单） | `ContentService.RegisterDirectory(path)` |
| `Engine` | 一个 `content0.archive`（引擎的 `UseArchive`） | `ContentService.RegisterDirectory(path)` |
| `Packed` | `<配置档>.manifest-<哈希>.archive`、`<配置档>.<位串>-<哈希>.archive`、… 外加 `archives.json` | `await ContentService.RegisterPackedDirectoryAsync(path)` |

`Packed` 面向分发：单个归档是很差的下载单位，而引擎又没有大小选项，所以由包自己来打包平铺构建。默认规则是
**哈希前缀 trie**：清单及其哈希文件得到一个自己的小归档；超过上限的文件独占一个归档；其余每个文件按其键的 MD5
的各位分桶 —— 序列化文件的键是清单 ID（引擎由资源的身份推导，内容改变时仍保持不变），`.resS`/`.resource` 的键是
文件名 —— 装不进上限的桶再按下一位继续分割。因此被修改的资源留在原来的归档里，新增或删除的资源只触及一个归档
（桶分裂或合并时再加一个相邻的）—— 其他所有归档的名称和字节都保持不变，这正是补丁管线想要的。（`.resS` 以名称为键，
所以修改一张贴图也会把它的像素数据从一个归档搬到另一个。）归档按其位路径加上文件列表的哈希命名
（`Main.0110-a3f9c2e1.archive`），所以同一个名称绝不会指两份不同的内容。**相同的输入总是打包成逐字节相同的结果。**
预计归档数量约为普通填充的 1.5 倍（叶子大约在上限的 0.6 倍）；想让它们更接近上限就调高 `MaxArchiveMB`。上限按
*输入* 字节计 —— 用 LZ4 时磁盘上的归档大约是它的一半。`archives.json` 列出每个归档的大小、输入大小、CRC 和其中的文件：
补丁器先取它，下载自己没有的名称，删除已消失的名称。CRC 是引擎对归档内容报告的值 —— 它不是磁盘文件的 CRC32
（实测），所以请用大小或你自己的哈希来校验下载。

**自定义分割。** 在 Editor 程序集里实现 `IContentArchivePartitioner`，并在配置档里选择它（`Partitioner`；命令行上用
`-contentlink.partitioner <类型名>`、`trie` 或 `sequential`）。它接收带大小的文件、清单文件、解析后的清单，以及带目录表
和磁盘文件的已构建 loadable，返回归档 —— 名称加文件。内置的 `SequentialFillPartitioner` 是朴素的排序填充，用局部性
换取更少、更满的归档。打包器会检查任何答案（每个文件恰好一次、没有空归档、朴素且唯一的名称），并以不同的文件顺序
再调用分割器一次，答案不同时让构建失败。像内置实现那样把内容哈希放进名称里，这样缓存的下载永远不会过期。切换
分割器或修改 `MaxArchiveMB` 会重新分割 —— 一次性的较大下载。

运行时 `RegisterPackedDirectoryAsync` 把每个归档挂载到同一个命名空间下，并像平铺目录一样注册挂载路径；
`UnregisterDirectory` 再把它们卸载。当文件夹没有 `archives.json`、索引没有列出任何归档，或某个归档在磁盘上的大小与
索引不符时，它返回 `-1`（并记录错误），什么都不挂载；某个归档挂载不上时，它抛出异常并指明是哪一个。

通过下载到达设备的内容，在注册之前值得检查一下。`archives.json` 记录了每个归档的 `Bytes` 和 `Crc`：
`RegisterPackedDirectoryAsync` 会在挂载任何东西之前拒绝磁盘大小与索引不符的归档，但长度相同的损坏只能通过比较 `Crc`
发现，而这是调用方的工作 —— 引擎遇到损坏的内容文件会阻塞而不是失败，所以请先校验。

### 后处理器

```csharp
using xpTURN.ContentLink.Editor;

public sealed class UploadStep : IContentBuildPostProcess
{
    public void OnBuilt(ContentBuildResult r)
    {
        // r.ProfileName, r.Target, r.OutputPath, r.Files（已排序）, r.Archives, r.Manifest, r.BuiltNames, r.Milliseconds
    }
}
```

在窗口里挑选实现（它用 `TypeCache` 找到它们）并在那里排序；顺序随配置档保存，也就是运行顺序。找不到的类型或抛出
异常的步骤会 **让构建失败**，并保留输出以供检查。其他任何阶段抛出异常也以同样的方式报告：结果里带着原因，窗口、
日志和命令行都会显示它。步骤在打包之后运行，所以 `r.Archives` 是最终状态。

### 菜单项与脚本调用

| 在哪里 | 做什么 |
|---|---|
| *Window > ContentLink > Build Window* | 编辑配置档、构建一个或全部、查看信息和上次结果 |
| *… > Build Content Directory* | 构建活动配置档 |
| *… > Update Content Catalog* | 不构建，只重写活动配置档的目录表（链接从琥珀色变为无色） |
| `ContentBuildRunner.Build(profile)` / `BuildAll()` | 从代码做同样的事；返回 `ContentBuildResult` |

### 命令行

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget StandaloneOSX -logFile <log> \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.compression LZ4
```

`tools/build-content.sh` 封装了这个调用（先检查工程锁，透传退出码，打印日志里的 `[ContentLink …]` 行）：

```
tools/build-content.sh -p samples/LinkSample -contentlink.profile Main
tools/build-content.sh -p <project> -t Win64 -contentlink.profile all      # -t 透传给 -buildTarget (Win64, OSXUniversal, Linux64, Android, iOS, …)
```

| 键（`-contentlink.<key> <value>`） | 值 |
|---|---|
| `profile` | 配置档名，或 `all`（不区分大小写；全部配置档，在第一次失败处停止）。省略 = 活动配置档 |
| `source`, `output` | 路径 |
| `archive` | `None` · `Engine` · `Packed` |
| `maxArchiveMB` | ≥ 1 的整数 |
| `compression` | `Uncompressed` · `LZ4` · `LZMA` |
| `cleanBuildCache`, `disableWriteTypeTree`, `failBuildWhenErrorsLogged` | `true` · `false` |
| `postProcessors` | 逗号分隔的类型名 |

覆盖参数 **只在内存中** 改变所指定的配置档 —— 命令行绝不会写设置文件（`profile all` 不接受覆盖参数）。构建失败、
未知的键或不存在的配置档以退出码 **1** 结束。工程不能在另一个编辑器实例中打开：批处理模式需要工程锁。

**只更新目录表。** `-executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` 接受同样的参数，但只重写
配置档的目录表 —— 不构建、不碰输出文件夹 —— 它是 *Update Content Catalog* 的命令行孪生
（`tools/build-content.sh -c -contentlink.profile Main`）。当 CI 只需要目录表资源保持最新（播放器构建之前的链接校验），
或目录由另一条管线构建时使用它。每次构建本来就会重写目录表，所以构建那边没有额外需要打开的开关。`profile all`
逐个更新每个配置档，在第一次失败处停止；`source` 覆盖参数决定目录表相对于哪个根目录命名。退出码同上。

## 加载内容

### 作用域

| 作用域 | 如何创建 | 何时结束 | 父级 |
|---|---|---|---|
| Root | 自动（`ContentScope.Root`） | `Application.quitting` | —— |
| Scene | `ContentScope.ForScene(scene)` | `SceneManager.sceneUnloaded` | Root |
| GameObject | `gameObject.Scope()` / `component.Scope()` | `OnDestroy` | 它所在的场景 |
| Explicit | `parent.CreateChild("Stage")` | 你的 `Dispose()`，否则随父级 | 任意 |

**GameObject 作用域** —— 最常见的情况。资源随对象一起消失：

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        var scope = this.Scope();                                   // 这个 GameObject 的作用域（gameObject.Scope() 也行）
        image.sprite = await scope.LoadAsync<Sprite>("Icons/sword[sword]");
    }                                                               // 在 OnDestroy 里释放 —— 别的什么都不用写
}
```

**Scene 作用域** —— 应该恰好和场景活得一样久的资源：

```csharp
var scope  = ContentScope.ForScene(gameObject.scene);               // 每个场景一个，第一次使用时创建
var skybox = await scope.LoadAsync<Material>("Materials/Sky");      // 场景卸载时释放
```

**Explicit 作用域** —— 关卡、界面、弹窗：由你决定它何时结束，最迟由父级结束它：

```csharp
var stage = ContentScope.Root.CreateChild("Stage3");                // 或 this.Scope().CreateChild("Popup")
await stage.LoadAsync<AudioClip>("Wav/descent");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... 关卡进行中 ...
stage.Dispose();                                                    // 它持有的一切都归还
```

**Root 作用域** —— 与应用同寿：

```csharp
var atlas = await ContentScope.Root.LoadAsync<Texture2D>("UI/Atlas");   // 在 Application.quitting 时释放
```

**每个作用域都有：**

```csharp
var mat   = scope.Load<Material>("Materials/Steel");                // 同步（阻塞等待引擎加载）
bool held = scope.Contains("Icons/sword");                          // true：再次加载它是免费的
scope.Release("Icons/sword");                                       // 可选的提前归还
var token = scope.DisposedToken;                                    // 作用域结束时取消 —— 传给你自己的 await
```

- **去重。** 每个作用域最多持有同一个键一次。反复加载同一个键（哪怕每帧一次）也不会让它增长。
- **泄漏信号。** 被场景卸载或退出回收的显式作用域会记录一条带路径的警告（`Root/Field/ForgottenPopup`）。
  `ContentService.CaptureStackTrace = true` 会附上它在哪里创建。随着已 Dispose 的父级或已销毁的所有者 GameObject
  结束属于正常生命周期，不会警告。
- **过度持有警告。** 作用域持有的资源数超过 `ContentScope.DefaultWarnThreshold`（或 `scope.WarnThreshold`）时会警告 ——
  作用域的失败模式是持有太多，而不是泄漏。
- **从未激活的对象** 不会收到 `OnDestroy`；它们的作用域在场景卸载时被回收。
- **被移动的对象**（`MoveGameObjectToScene`、`DontDestroyOnLoad`）保留其资源：原场景卸载时，它们的作用域被移交给
  它们现在所在的场景。
- **跨作用域。** `source.ShareTo(target, key)` 给 `target` 一个它自己的引用；`target.Adopt(asset)` 接收一个你已经持有的
  资源。没有这两者之一，绝不要把裸资源交给另一个系统。
- 作用域只释放资源；它们从不销毁 GameObject。

### 序列化链接

```csharp
public ContentLinkSprite icon;                               // 还有 ContentLinkGameObject、ContentLinkTexture2D
public class ContentLinkAudio : ContentLink<AudioClip> { }   // 其他类型自行派生；绘制器同样适用

image.sprite = await icon.LoadAsync(this);                   // 必须有所有者（null 抛出 ArgumentNullException）—— Component/GameObject 表示它的 GameObject 作用域，ContentScope 表示该作用域
image.sprite = await icon.LoadAsync(stage);                  // 例如放进一个显式作用域
var clip = music.Load(this);                                 // 同步

// 装备切换：提前归还旧的（可选）
icon.Release();
icon.SetName(table[itemId].IconName);
image.sprite = await icon.LoadAsync(this);

icon.IsValid; icon.Asset; icon.IsLoading; icon.Scope; icon.RuntimeKeyIsValid();
```

### 实例

```csharp
var go = await scope.InstantiateAsync("Prefabs/Cat", parent);
```

预制体引用属于 **实例**，而不属于 `scope`（后者只在自己先结束时取消请求）。销毁实例就会归还它。用
`Object.Instantiate(instance)` 做出的克隆体在 `Awake` 里继承引用，所以即使原件先被销毁，克隆体也保留着它的贴图。
不需要生成器类：一个引用就是表里的一个槽位，所以每个实例一个也很便宜。

### 场景

源根目录下的场景成为场景条目（`Scenes/Arena`），连同其依赖一起构建进目录。场景不做引用计数：它的生命周期就是
场景实例本身。ContentLink 解析名称，跟踪每一个已加载的内容场景（无论谁加载的），使得有场景打开时目录不能被注销，
并允许作用域拥有一个场景。

```csharp
// 拥有：作用域结束时卸载（仅 additive）
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
scope.ReleaseScene(arena);                                 // 可选的提前卸载

// 序列化，必须有所有者 —— 规则与 ContentLink<T> 相同
public ContentSceneLink level;
var scene = await level.LoadAsync(this);
await level.UnloadAsync();                                 // 可选；任何时候调用都安全

// 非拥有，任意模式（Single 会关闭其他所有场景）。用 SceneManager.UnloadSceneAsync 卸载。
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

- Single 模式的场景不能被拥有（它们连所有者一起关掉）。Single 模式的加载也会销毁调用方：从 MonoBehaviour 里 await
  时请传入 `destroyCancellationToken`。
- 拥有场景的作用域结束时，如果该场景是最后一个已加载的场景，场景会留下并记录一条警告；应用退出时什么都不卸载。
- 取消只停止等待；additive 场景会在引擎加载完成后再被卸载。
- 作用域只拥有自己的加载产生的那个实例：你自己通过 `SceneManager` 打开的场景仍然是你的，哪怕某个作用域加载了同一个
  场景；失败的作用域加载也绝不会认领它。
- `ContentService.LoadedSceneCount` / `SnapshotScenes(list)` 显示哪些是打开的。

### 裸句柄

面向自己管理生命周期的代码（对象池、流式系统）：

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");   // 或 Acquire<T>（同步）
h.Asset; h.IsValid; h.Status; h.Key;
var h2 = h.Share();          // 第二个引用
h.Dispose();                 // 归还它 —— 或者交给一个作用域：
scope.Attach(h2);            // 现在由作用域拥有

if (ContentService.TryGetLoadableObjectId("Icons/sword", out var id))   // 引擎 id，用于基于 id 的加载
    h = await ContentService.AcquireAsync<Texture2D>(id);
```

`ContentService.Contains(name)` / `ContainsScene(name)` 告诉你某个已注册的目录是否携带该名称。

## 何时调用 Release

| 情形 | 该做什么 |
|---|---|
| 组件、弹窗、关卡、场景内容 | 什么都不做。加载到对应的作用域里。 |
| 资源很多的关卡 / 界面 | 一个显式作用域，关卡结束时 `Dispose()`。 |
| 复用单元格的滚动列表 | 每个单元格一个子作用域；单元格重新绑定时 Dispose 它。 |
| 装备 / 皮肤切换 | 加载新的之前 `Release(key)` 或 `ContentLink.Release()`。 |
| 把资源交给另一个系统 | `ShareTo(target, key)` 或 `target.Adopt(asset)`。绝不跨作用域传递裸资源。 |

## 释放是如何发生的

一个已加载的资源分三步回到引擎：**作用域察觉自己的结束** → **它放开持有的每一个引用** → **计数归零的资源在帧末
一次性归还。**

### 1. 作用域如何知道自己已经结束

| 作用域 | 信号 | 内部发生什么 |
|---|---|---|
| **GameObject** | 该对象的 `OnDestroy` | 第一次调用 `gameObject.Scope()` 会添加一个隐藏的 `ContentScopeOwner` 组件；它的 `OnDestroy` 把作用域 Dispose 掉。从未激活的对象不会收到 `OnDestroy`，所以它的作用域在场景卸载时由 **场景作用域** 作为子级回收。 |
| **Scene** | `SceneManager.sceneUnloaded` | `ContentScope.ForScene(scene)` 以场景句柄为键创建作用域。`sceneUnloaded` 时该作用域被 Dispose。其对象 **已移动到另一个场景** 的子 GameObject 作用域（`MoveGameObjectToScene` · `DontDestroyOnLoad` —— 所有者的 `scene` 已不再是这一个）不会被释放，而是被重新挂到它们现在所在的场景之下。 |
| **Root** | `Application.quitting` | `ContentService.Shutdown()` 把 Root Dispose 掉，整棵树级联。你自己对 Root 调用 `Dispose()` 只会记录一条错误。 |
| **Explicit** | 你的 `Dispose()` | 没有它，父级的结束会级联进来 —— 而当父级是场景或 Root 时，会记录一条带路径和创建栈的 **泄漏警告**。通过显式父级或已销毁的所有者 GameObject 结束属于正常生命周期，不会警告。 |

克隆体（`Object.Instantiate`）继承 `ContentScopeOwner` 序列化的键列表，并在 `Awake` 里取得 **它们自己的引用**
（原件仍持有时共享，原件已消失时重新加载）。以非激活状态创建的克隆体，其 `Awake` 被推迟，在第一次激活时取得引用。

### 2. 作用域结束时做什么 —— 按顺序

1. `IsDisposed = true`，`DisposedToken` 被取消 —— 该作用域上仍在进行的加载被取消。
2. 子作用域按逆序结束（适用上面的移交规则）。
3. 每个按键持有的引用（`LoadAsync`/`Load`）和每个 `Attach` 的句柄被释放 → 各资源的计数下降。
4. 作用域拥有的场景被卸载（应用退出时除外 —— 引擎会自己拆掉它们）。
5. 被场景或 Root 回收的显式作用域记录那条警告。

### 3. 计数归零之后

每个资源有一个槽位（来自任何作用域的同一名称是同一个槽位），槽位携带计数。计数归零时槽位 **不会当场释放**：
它连同帧号一起进入队列。真正的释放由 `ContentService.Collect()` 完成，包把它插入播放器循环中 `PostLateUpdate` 的
末尾 —— **每帧一次**，在你的所有代码都运行之后。

对队列中的每个槽位，`Collect()` 做以下之一：

- 期间有人再次获取了它，计数不再为零 → 它只是离开队列（**同一帧内的释放再获取绝不会重新加载**）；
- 它仍在加载中 → 放着不动，直到加载完成；
- 自归零起还不到 `ReleaseDelayFrames` 帧 → 下一帧再看；
- 否则 → 调用引擎的 `Loadable.Release()` 并清空槽位。槽位的代号前进，所以迟到完成的加载碰不到这个空槽位。

设置 `ContentService.AutoCollect = false` 后播放器循环钩子什么都不做，由你自己调用 `Collect()`。

### 目录与关闭

`ContentService.UnregisterDirectory(dir)` 在该目录的资源仍被持有或该目录的某个场景仍打开时拒绝执行，因为引擎要求
先释放所有 `Loadable`、卸载所有场景；`force: true` 会使剩余的资源引用失效（打开的场景永远不会），此后曾持有它们的
作用域只看到活着的引用 —— `Count`、`DumpTree` 和 `Release(key)` 会悄悄丢弃被杀掉的引用。打包目录在注销时卸载其归档。
`ContentService.Shutdown()`（也在 `Application.quitting` 时运行）Dispose 作用域树，报告剩余的裸句柄，并注销一切。

## 编辑器模拟

在编辑器里，由工程来充当内容目录。在 Play Mode 中以某个 **配置档的输出文件夹** 为路径调用 `RegisterDirectory`
（或 `RegisterPackedDirectoryAsync`）时，不会向引擎注册任何东西：名称来自配置档源根目录此刻在磁盘上的样子，按配置档
的类别过滤，每个资源从工程加载。修改一张贴图再按 Play —— 你看到修改。删除一个文件 —— 加载失败。添加一个文件 ——
它能加载。克隆仓库直接按 Play —— 不需要构建。

```
Window > ContentLink > Editor Simulation     开关（默认开启，按工程保存）
```

相当于 Addressables 的 *Use Asset Database* 播放模式：一种创作时的便利，不是构建的替代品。关掉它来检查构建是否
表现一致 —— 此时编辑器会像播放器一样真正注册已构建的目录。

- **什么都不写入。** 索引（名称 → 路径）在内存里，在注册目录或进入 Play 时建立，退出 Play 时丢弃。
- **只有配置档的输出文件夹以这种方式提供。** 其他任何路径（下载的副本、临时的构建）都会真正注册；在这类目录的
  根之下，还没有任何目录表携带的名称仍然从工程解析（原始的模拟），两处都有的名称由真正的目录表回答。
- **注册仍然是必须的。** 忘掉的 `RegisterDirectory` 在编辑器里和在播放器里一样失败；同一文件夹注册两次会被拒绝，
  源根目录不存在的配置档会抛出异常。
- **开关在注册目录时读取。** Play 中途关掉它，已由工程提供的目录会一直工作到下一次 Play；只有真实目录之下的名称
  停止解析。退出 Play 时的摘要和重置无论开关此刻怎样都会运行。
- **被释放的资源不会卸载。** 引用计数不变，但编辑器会让对象活着，所以创作期间的内存读数比构建高。请在开关关闭时
  确认卸载行为。持有由工程提供的资源永远不会阻止注销目录。
- **Play 期间的修改就地可见**（重新导入会刷新已加载的对象）；被删除的资源，其对象会在仍持有它的任何句柄之下被销毁，
  对它的新加载会以错误失败。
- **归档不会被演练。** `Packed` 和 `Engine` 的输出与其他输出一样由工程提供；挂载和归档的行为需要关闭开关。
- **模拟的场景** 通过 `EditorSceneManager.LoadSceneAsyncInPlayMode` 打开；请求它的作用域仍会卸载它，但它们不属于
  任何目录。
- **重复的名称会被拒绝**，就像构建器拒绝它们那样，所以不存在只在编辑器里能用的名称。

每一次这样的注册都会记录一行，说明由哪个配置档和根目录提供。离开 Play Mode 时记录一行，列出模拟解析过的名称 ——
加载过的，或只用 `RuntimeKeyIsValid` / `Contains` 检查过的 —— 之中配置档的目录表尚未携带的那些：发布前需要构建的
链接；`ContentService.SnapshotSimulated(list)` 在播放时报告每一个由工程提供的名称。
`ContentService.TryGetLoadableObjectId(name, out id)` 也回答由工程提供的名称。

## 诊断

- *Window > ContentLink > Dump Scope Tree*（Play Mode），或 `ContentScope.DumpTree()`：

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```
  子级按作用域持有它们的顺序列出，这不是创建顺序：移除一个作用域会把最后一个同级挪进它的槽位，所以无论树多大，
  移除的开销都相同。

- `ContentService.Snapshot(list)` —— 每一个活着的引用（`CaptureStackTrace` 开启时带堆栈跟踪）
- `ContentService.SnapshotScenes(list)` / `LoadedSceneCount` —— 已打开的内容场景
- `ContentService.SnapshotSimulated(list)` —— 编辑器模拟解析过的名称
- `ContentService.LiveAssetCount` —— 当前持有的资源数
- 构建窗口显示本次编辑器会话的上次构建（配置档、目标、文件数、耗时、错误）；日志里每次构建有一行
  `[ContentLink] built …`

## 从 AssetLink 或 Addressables 迁移

| 以前 | ContentLink |
|---|---|
| `AssetLink.LoadAssetAsync()` + `ReleaseAsset()` | `ContentLink.LoadAsync(owner)`；`Release()` 可选 |
| `AssetLinkSpawner.SpawnAsync()` + `DoAutoRelease` | `scope.InstantiateAsync()` |
| `AddressablesTracker.ReportUnreferencedHandles()`（基于 GC） | 场景卸载 / 退出时的作用域警告（确定性） |
| Auto Regist Folder Pattern | 构建配置档与类别 |
| `AssetRef`（GUID） | `ContentService.TryGetLoadableObjectId(name, out id)` → `AcquireAsync<T>(id)` + `scope.Attach(handle)` |
| *Use Asset Database* 播放模式 | 编辑器模拟 |
| Addressables 的组 / 包 | 配置档 / 类别，`Packed` 归档 |

## 注意事项与限制

- 仅限主线程。取消令牌也来自主线程。
- 引擎加载不能取消。被取消的请求立即归还其引用；加载完成后在下一次 `Collect()` 时释放。
- 引擎抛出异常的加载与其他失败一样报告 —— 无效句柄，并记录日志 —— 同步和异步路径都是如此；不会有东西被一直持有。
- 由工程提供的 Play Mode 内容（编辑器模拟）不受 Unity 引用计数。请在模拟关闭时，针对已构建并注册的内容目录测试
  释放行为。
- 以非激活状态创建的克隆体在第一次激活时取得引用。
- 场景不做引用计数。以 additive 方式加载同一场景两次得到两个实例；各自分别跟踪和卸载。场景没有 `ShareTo` / `Adopt`。
- 内容目录是为一个构建目标构建的，在别的目标上可能加载不了。
- `Packed` 的归档大小是对输入字节的上限，不是对压缩后文件的。哈希前缀 trie 产生的归档数约为普通填充的 1.5 倍；
  徘徊在上限附近的归档可能在不同构建之间分裂与合并；修改 `MaxArchiveMB` 或分割器会重新分割输出。
- 构建需要工程锁：命令行构建之前，请在其他编辑器实例中关闭该工程。

## 仓库结构与测试

| 路径 | 是什么 |
|---|---|
| `com.xpTURN.ContentLink/` | 包（`Runtime`、`Editor`、`Tests~/Verify`、`README`、`CHANGELOG`） |
| `samples/LinkSample/` | 通过 `file:` 使用该包的 Unity 6.6 工程 —— `Assets/Content` 下有贴图、精灵图集、预制体、场景和音频 |
| `tests/LinkTests/` | Unity Test Runner 工程：EditMode（构建器、设置、打包器、窗口模型）和 PlayMode（生命周期、场景、打包注册、分配）套件 |
| `tools/run-all-tests.sh` | 运行全部：dotnet 测试台，然后 EditMode，然后 PlayMode（`--player` 额外运行独立播放器） |
| `tools/build-content.sh` | 命令行内容构建 |
| `docs/IMP/` | 设计与实现记录（韩语） |
| `.github/` | CI（`workflows/check.yml`）：每次 push 和 pull request 运行 dotnet 测试台、`.meta` 伴生文件检查和版本清单检查；Unity 套件需要有许可的编辑器，只在本地运行 |

`Tests~/Verify` 针对已文档化的 Unity 6.6 API 桩和一个强制执行 Loadable 引用规则的伪引擎来编译包，并运行生命周期、
构建和打包场景（用 .NET 8 SDK `dotnet run`）。Unity 套件针对测试自己构建的内容目录运行，在编辑器和独立播放器中都有。
已在 Unity 6000.6.0f1 上验证。

```
tools/run-all-tests.sh                 # 测试台 + EditMode + PlayMode
tools/run-all-tests.sh --dotnet-only   # 仅测试台（几秒钟）
tools/run-all-tests.sh --player        # 另外在 StandaloneOSX 播放器中运行 PlayMode
```

## 许可证

Apache License 2.0
