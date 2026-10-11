# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | [한국어](README.ko-KR.md) | **日本語** | [简体中文](README.zh-CN.md)

ContentLink は、Unity 6.6 のための完結したコンテンツワークフローで、エンジンの **コンテンツディレクトリ** の上に作られて
います。コンテンツディレクトリとは、プレイヤーが実行時に登録してロードする、ビルド済みアセットのフォルダーです。ContentLink
はアセットをプロジェクトフォルダーから画面まで運びます。アセットを **名前で** 指し、ビルドし、プレイヤーに同梱するか CDN
から配信し、スクリプトやインスペクターのフィールドからロードします。ロードした側がいなくなると、アセットはひとりでに解放
されます。

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        // Assets/Content/Icons/sword.png — 名前でロードし、この GameObject が破棄されると解放される
        image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
    }
}
```

> **名前で指してください、エディターから CDN まで — そして解放のことは忘れてください。** アセットは所有者 — GameObject、
> シーン、アプリ — が生きている間だけ生き、`Release()` はつねに最適化であって、義務ではありません。

---

## 目次

1. [得られるもの](#得られるもの)
2. [インストール](#インストール)
3. [全体像](#全体像)
4. [チュートリアル: 最初のコンテンツディレクトリ](#チュートリアル-最初のコンテンツディレクトリ)
5. [カタログの設定](#カタログの設定)
6. [ビルド](#ビルド)
7. [ロード](#ロード)
8. [解放](#解放)
9. [CDN からコンテンツをダウンロードする](#cdn-からコンテンツをダウンロードする)
10. [ローカライズされたコンテンツ](#ローカライズされたコンテンツ)
11. [エディターシミュレーション](#エディターシミュレーション)
12. [診断](#診断)
13. [Addressables・Resources からの移行](#addressablesresources-からの移行)
14. [知っておくこと](#知っておくこと)
15. [ライセンス](#ライセンス)

---

## 得られるもの

| 段階 | ContentLink が与えるもの |
|---|---|
| **名前** | フォルダー（`Assets/Content`）の下のすべてのアセットが、パスから名前を得ます: `Icons/sword`、`Prefabs/Cat`、`Scenes/Arena` |
| **選ぶ** | 名前（`ContentLinkSprite icon;`）や GUID（`ContentRef<Sprite>`）を保持するインスペクターのフィールド — ドラッグ＆ドロップと状態の色 |
| **作業** | エディターでは Play Mode がプロジェクトから直接ロードします — 作業中にビルドするものはありません |
| **ビルド** | プロファイル・カタログ・圧縮・サイズ分割アーカイブを扱うビルドウィンドウと、CI 向けのコマンドライン |
| **出荷** | プレイヤーの中（`StreamingAssets`）に、または CDN からダウンロード — 変わったアーカイブだけ、再開と MD5 検証つき |
| **ロード** | スプライト・プレハブ・オーディオ・あらゆるアセット型、プレハブのインスタンスとシーン — 非同期でも同期でも、名前で |
| **解放** | 自動: すべてのロードは **スコープ**（GameObject・シーン・アプリ・自分で作ったもの）に属し、スコープが終わるとともに去ります |
| **調べる** | スコープツリーのダンプ、生きている参照のスナップショット、終え忘れたスコープを名指しする警告 |

依存は Unity 6000.6 だけです。

## インストール

*Window > Package Manager > + > Add package from git URL*:

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

または、ローカルのチェックアウトなら `Packages/manifest.json` に追加します:

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

このリポジトリの `samples/LinkSample` は、下のチュートリアルとまったく同じように設定された小さな Unity プロジェクトです:
`Assets/Content` の下のテクスチャ・スプライトアトラス・プレハブ・シーン・オーディオと、それを登録してロードするブートストラップ
スクリプト。

## 全体像

```
 エディターで                                     実行時に (プレイヤー、または Play Mode)
 ─────────────────────────────────────────────   ─────────────────────────────────────────────────────
 Assets/Content/Icons/sword.png                  1. 起動時にフォルダーを一度登録
        │                                            ContentService.RegisterDirectory(".../MainContent")
        │  プロファイル "Main"                    2. 名前でスコープにロード
        ▼                                            this.Scope().LoadAsync<Sprite>("Icons/sword[sword]")
 カタログ  "Icons/sword" → エンジン id            3. スコープが終わる → アセットが解放される
        │
        │  ビルド
        ▼
 StreamingAssets/MainContent/  (コンテンツディレクトリ: ビルド済みアセット + カタログ)
```

| 用語 | 何か |
|---|---|
| **ソースルート** | アセットをビルドするフォルダー。既定は `Assets/Content`。 |
| **名前** | ロードに使うもの: ソースルートの下のアセットのパスから拡張子を除いたもの — `Icons/sword`。 |
| **カタログ** | すべての名前とその裏のエンジン id を並べた `ContentCatalog` アセット。ウィンドウが書き、手では編集しません。ビルドがコンテンツディレクトリに入れます。 |
| **プロファイル** | コンテンツディレクトリひとつのビルド設定: ソースルート、出力フォルダー、カタログ、アーカイブ形式。 |
| **コンテンツディレクトリ** | ビルドの出力フォルダー。実行時に登録し、名前でロードします。 |
| **スコープ** | ロードしたものの所有者。スコープが終わると、それがロードしたものはすべて解放されます。 |
| **リンク・参照** | 名前（リンク）やアセットの GUID（参照）を保持するシリアライズされたフィールド。インスペクターで設定します。 |

## チュートリアル: 最初のコンテンツディレクトリ

プロファイルひとつ、カタログひとつ、ビルド一回をたどります: プレイヤーに同梱して出荷するコンテンツです。

### ステップ 1 — アセットをソースルートの下に置く

`Assets/Content` フォルダーを作り、コンテンツを入れます。サブフォルダーは自由に分けてかまいません:

```
Assets/Content/Icons/sword.png        (Texture Type: Sprite)
Assets/Content/Prefabs/Cat.prefab
Assets/Content/Audio/click.wav
Assets/Content/Scenes/Arena.unity
```

ファイルごとに名前が付きます — `Assets/Content` の下のパスから拡張子を除いたものです:

| ファイル | 名前 | ロードされるもの |
|---|---|---|
| `Icons/sword.png` | `Icons/sword` | `Texture2D` |
| — そのスプライト `sword` | `Icons/sword[sword]` | `Sprite` |
| `Prefabs/Cat.prefab` | `Prefabs/Cat` | プレハブ（`GameObject`） |
| `Audio/click.wav` | `Audio/click` | `AudioClip` |
| `Scenes/Arena.unity` | `Scenes/Arena` | シーン — `LoadSceneAsync` でロード |

これらのファイルが使うアセット — マテリアルのテクスチャ、プレハブのメッシュ — は自動的に一緒にビルドされます。名前でロード
するものだけがルートの下にあればかまいません。

### ステップ 2 — ビルドウィンドウを開く

*Window > ContentLink > Build Window*。初めて開くと、ウィンドウにはプロファイル `Main` がひとつあります:

| フィールド | 値 | 意味 |
|---|---|---|
| Name | `Main` | プロファイルの名前 |
| Name style | `PathWithoutExtension` | 拡張子なしの名前（`Icons/sword`）— [名前のスタイル](#名前のスタイル) 参照 |
| Source root | `Assets/Content` | アセットをビルドするフォルダー |
| Output path | `Assets/StreamingAssets/MainContent` | ビルドを書く場所 — `StreamingAssets` はプレイヤーに同梱されます |
| Categories | 一行: *(whole root)* | ルート全体がカタログひとつに — `Assets/ContentCatalog.asset` に作られる |
| Archive | `None` | フラットなファイル — [アーカイブ形式](#アーカイブ形式) 参照 |
| Compression | `Uncompressed` | アーカイブにだけ効く |
| Target | `NoTarget` | いまアクティブなプラットフォーム向けにビルド |

設定は `ProjectSettings/ContentLinkBuild.asset` に保存されます。このファイルをコミットしてください。

### ステップ 3 — カタログを作る

ウィンドウ下部の **Update catalogs** を押します。`Assets/Content` を走査して `Assets/ContentCatalog.asset` を書きます。
Project ウィンドウでそのアセットを選ぶと、インスペクターにすべての名前が並びます。

- カタログアセットをコミットしてください。インスペクターのフィールドがこれを読み、名前が有効かどうかを示します。
- 手では編集しません。**Update catalogs** とビルドが毎回書き直します。
- ソースルートの **外** に置いてください — ルートの下のカタログは拒否されます。

ビルドのたびに **Update catalogs** を押す必要はありません: ビルドがカタログを自分で更新します。アセットを追加・移動した
あと、インスペクターに新しい名前をすぐ認識させたいときに押してください。

### ステップ 4 — ディレクトリを登録してロードする

アプリの起動時、何かをロードする前に、コンテンツディレクトリを一度登録します。最初のシーンの `Boot` コンポーネントで
十分です:

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
        ContentService.RegisterDirectory(path);   // Archive = None または Engine
        // Archive = Packed:  await ContentService.RegisterPackedDirectoryAsync(path);  （async メソッドの中で）
    }
}
```

それから名前でロードします。`this.Scope()` はこの GameObject のスコープです: GameObject が破棄されるとスプライトは解放
されます。

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

あるいはインスペクターでアセットを選ばせます — フィールドに `Assets/Content/Icons/sword.png` をドロップしてください:

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;
    public ContentLinkSprite icon;   // インスペクターが "Icons/sword" + スプライト "sword" を保存する

    async void Start() => image.sprite = await icon.LoadAsync(this);
}
```

Play を押します。**ビルドなしで** 動きます: エディターでは、プロファイルの出力フォルダーを登録すると、そのプロファイルを
プロジェクトが肩代わりします（[エディターシミュレーション](#エディターシミュレーション)）。アセットを追加・編集・削除して
もう一度 Play を押せば、そのまま見えます。

### ステップ 5 — ビルドする

**Build 'Main'** を押します。ウィンドウの *Last build* に結果が出て、`Assets/StreamingAssets/MainContent` にコンテンツ
ディレクトリ — エンジンのファイルとカタログ — ができます。

それからいつもどおりプレイヤーをビルドします（*File > Build Profiles*）。`StreamingAssets` がプレイヤーに入り、`Boot` が
そのフォルダーを見つけます。

- コンテンツが変わったなら **プレイヤーをビルドする前に毎回コンテンツをビルド** してください。プレイヤーは最後の
  コンテンツビルドが書いたものを載せます。
- **コンテンツディレクトリはひとつのプラットフォーム向けにビルドされます** — エディターでアクティブなプラットフォームです。
  プラットフォームを切り替えたら、コンテンツをビルドし直してください。

### ステップ 6 — エディターでビルドを確かめる（任意）

*Window > ContentLink > Editor Simulation* をオフにして Play を押すと、エディターがプレイヤーと同じようにビルド済みの
フォルダーをロードします。オンに戻せば、ビルドなしで作業する状態に戻ります。

これでひと回りです: アセットをルートの下に置き、名前でロードし、出荷前にビルドします。

## カタログの設定

### 名前

- 名前はソースルートの下のアセットのパスから拡張子を除いたものです: `Assets/Content/Icons/sword.png` → `Icons/sword`。
- **スプライト** は `<ファイル>[<スプライト>]`: `Icons/sword[sword]`。スプライトシート `sheet.png` は `Icons/sheet[run_01]`、
  `Icons/sheet[run_02]`、… を与えます。
- ルートの下の **スプライトアトラス**（Sprite Atlas V2）は、それがパックするスプライトごとに、アトラス経由の名前をもうひとつ
  付けます: `Atlas/UI[icon]`。スプライト自身の名前とアトラスの名前は同じスプライトをロードします。
- **シーン** はアセットと同じように名前が付き（`Scenes/Arena`）、`LoadSceneAsync` でロードします。
- 名前は **プロファイルの中で一意** です。同じ名前になる二つのアセット — 同じフォルダーの `sword.png` と `sword.prefab` —
  は、両方のファイルを示してビルドが失敗します。
- 名前は大文字と小文字を区別し、ファイルの綴りをそのまま使います。ハングル・かな・アクセント付きの名前も、どのマシンでも
  同じように動きます。

### 名前のスタイル

プロファイルの **Name style** が、名前に拡張子を残すかを決めます:

| Name style | `Icons/sword.png` | 並んだ `sword.png` と `sword.prefab` |
|---|---|---|
| `PathWithoutExtension`（既定） | `Icons/sword` | 拒否 — どちらも `Icons/sword` |
| `PathWithExtension` | `Icons/sword.png` | 許可 — `Icons/sword.png` と `Icons/sword.prefab` |

**早めに決めてください。** 変えると、次のカタログ更新でルートのすべての名前が変わります: 古い名前を持つリンク・コード・
データはロードできなくなります。`ContentRef` フィールドは GUID を持つので影響を受けません。プロファイルとカタログが食い
違っている間、ウィンドウが注意を表示します。

### カテゴリ: ひとつのディレクトリに複数のカタログ

既定ではルート全体がカタログひとつに入ります。大きなプロジェクトは、ルートをサブフォルダーで分け、フォルダーごとに専用の
カタログファイルを持たせられます — ファイルが小さくなり、別のフォルダーで作業する人どうしが同じカタログに触れません。

ビルドウィンドウの **Categories** で:

1. *(whole root)* の行を **−** で消します。カタログひとつで足りるなら残します。
2. **Add category** にルートのサブフォルダー — `Characters` — を書いて **Add** を押します。`UI` と `Levels` も同じように。
3. **Catalog** 欄は空のままにします: カタログは最初のビルドかカタログ更新のときに
   `Assets/ContentCatalogs/<プロファイル>.<カテゴリ>.asset` に作られます（ウィンドウにパスが出ます）。あるいは既存の
   `ContentCatalog` を選びます（*Assets > Create > xpTURN > ContentLink > Content Catalog*）。

```
プロファイル "Main"            ソースルート Assets/Content
  カテゴリ  Characters    →    Assets/ContentCatalogs/Main.Characters.asset
  カテゴリ  UI            →    Assets/ContentCatalogs/Main.UI.asset
  カテゴリ  Levels        →    Assets/ContentCatalogs/Main.Levels.asset
```

- **カテゴリは名前を変えません。** `Assets/Content/UI/logo.png` はどのカタログに入っても `UI/logo` です。
- プロファイルのすべてのカタログは **ひとつの** コンテンツディレクトリに入ります: 一度登録すれば、すべての名前をロード
  できます。
- カテゴリは重なってはいけません: *(whole root)* の行とサブフォルダーの行は一緒に使えず、`UI` と `UI/Icons` も一緒には
  使えません。
- どのカテゴリにも入らないルートの下のアセットは **ビルドされません** — ビルドがその数とともに警告を出します。

### 複数のプロファイル: 別々に登録するコンテンツディレクトリ

プロファイルごとにコンテンツディレクトリをひとつビルドし、それぞれ別に登録します。コンテンツを別々に出荷・ダウンロードする
ときに複数使います — 本編と DLC パック、あるいは [ロケール](#ローカライズされたコンテンツ)。

| プロファイル | ソースルート | 出力パス | Archive |
|---|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` | `Packed` |
| `Dlc1` | `Assets/Dlc1` | `Build/Content/Dlc1` | `Packed` |

追加するには **New profile** に名前を書いて **Add** を押します。新しいプロファイルは、出力パス `Build/Content/<名前>`、LZ4
圧縮の `Packed` アーカイブ、`Assets/ContentCatalogs/<名前>.asset` のカタログひとつで始まります。**Set active** は
*Window > ContentLink* のメニュー項目がビルドするプロファイルを選びます。

- ソースルートは互いの中に入れず、**並べて** 置いてください。
- あるルートの下のアセットが別のルートの下のアセットを参照すると、その **コピー** が自分のディレクトリに入ります。共有する
  アセットはルートをまたいで参照せず、名前でロードしてください。

## ビルド

### ビルドウィンドウ

| フィールド | 何を入れるか |
|---|---|
| **Content version** | 任意。すべての Packed ビルドに刻まれるラベル — ダウンロード時に本編とそのロケールを対にするのに使います |
| **Name** | プロファイルの名前。`Packed` プロファイルは英字・数字・`.`・`-`・`_` だけ（アーカイブの名前になります） |
| **Name style** | [名前のスタイル](#名前のスタイル) 参照 |
| **Source root** | ビルドするフォルダー（**…** で選ぶ） |
| **Output path** | ビルドを書く場所（**…** で選ぶ）— [出力の置き場所](#出力の置き場所) 参照 |
| **Target** | `NoTarget` はアクティブなプラットフォーム向けにビルド。別の値なら、ほかのプラットフォームではビルドを拒否 |
| **Categories** | サブフォルダーとそのカタログ — [カテゴリ](#カテゴリ-ひとつのディレクトリに複数のカタログ) 参照 |
| **Archive** | `None`・`Engine`・`Packed` — [アーカイブ形式](#アーカイブ形式) 参照 |
| **Max archive MB** | `Packed` のみ: アーカイブひとつに入るコンテンツのおおよその量（既定 20） |
| **Compression** | `Uncompressed`・`LZ4`・`LZMA` — アーカイブのみ。`LZ4` が無難な既定 |
| **Clean build cache** | エンジンのビルドキャッシュを無視してすべてビルドし直す |
| **Disable type trees** | 出力は小さくなるが、**まったく同じ Unity バージョンでしか** ロードできない |
| **Fail when errors are logged** | 既定でオン: ビルド中にエラーがログされるとビルドが失敗 |
| **Partitioner** | `Packed` のみ: ファイルをアーカイブにまとめる方法（既定のままにしてください） |
| **Post-processors** | ビルドのあとに走るステップ — [ビルドのあと](#ビルドのあと) 参照 |

下部: **Update catalogs**（カタログだけ書く）、**Build '\<プロファイル\>'**、**Build all**。問題は何かをビルドする前にそれらの
ボタンの上に並び、カテゴリの問題はその行の下に出ます。

### アーカイブ形式

| Archive | 出力の見た目 | 登録 | 使いどころ |
|---|---|---|---|
| `None` | エンジンのフラットなファイル | `ContentService.RegisterDirectory(path)` | コンテンツをプレイヤーに同梱し、いちばん単純にしたいとき |
| `Engine` | `content0.archive` ひとつ | `ContentService.RegisterDirectory(path)` | プレイヤーの中に圧縮ファイルひとつで |
| `Packed` | *Max archive MB* ほどのアーカイブ数個 + `archives.json`・`archives.hash` | `await ContentService.RegisterPackedDirectoryAsync(path)` | コンテンツをダウンロードするとき、または圧縮しつつパッチ用に分けたいとき |

`Packed` ビルドは **決定論的** です: 同じ入力はバイト単位で同じアーカイブにまとまり、小さな変更が触れるのはアーカイブの
いくつかだけ — 残りは名前とバイトをそのまま保つので、パッチは小さくなります。*Max archive MB* は圧縮前のコンテンツ量を数え
ます。LZ4 ならディスク上のファイルはその半分ほどになります。

### 出力の置き場所

| 出力パス | どうなるか |
|---|---|
| `Assets/StreamingAssets/` の下 | フォルダーがプレイヤーに同梱されます。`Application.streamingAssetsPath/<フォルダー>` を登録 |
| `Assets/` の外（例: `Build/Content/Main`） | インポートされず、プレイヤーにも入りません — サーバーに上げる（[CDN](#cdn-からコンテンツをダウンロードする)）か自分でコピー |

出力フォルダーは空か、以前のコンテンツビルドでなければなりません: ほかのものが入ったフォルダーには手を付けず、ビルドが
失敗します。エンジンが走る前に失敗したビルドは、以前の出力をそのまま残します。

### 設定の例

**すべてアプリの中に** — いちばん単純:

| フィールド | 値 |
|---|---|
| Output path | `Assets/StreamingAssets/MainContent` |
| Archive | `None`（ファイルを小さくしたいなら `Packed` + `LZ4`） |

```csharp
var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
ContentService.RegisterDirectory(path);                      // Archive = None
// await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

**CDN からダウンロード** — アプリをビルドし直さずに更新:

| フィールド | 値 |
|---|---|
| Output path | `Build/Content/Main` |
| Archive | `Packed`、Compression `LZ4`、Max archive MB `20` |

ビルドのたびに出力フォルダーをアップロードし、起動時にダウンロードします — [CDN からコンテンツをダウンロードする](#cdn-からコンテンツをダウンロードする) 参照。

**本編とダウンロードするパック** — [複数のプロファイル](#複数のプロファイル-別々に登録するコンテンツディレクトリ) のように、それぞれ
プロファイルひとつずつ。

### よくあるビルドエラー

| メッセージ | 直し方 |
|---|---|
| `source root '…' is not a folder` | フォルダーを作るか、*Source root* を直してください |
| `duplicate name '…': … / …` | 二つのファイルの一方の名前を変えるか、プロファイルを `PathWithExtension` に |
| `'…' is a ContentCatalog under the source root` | カタログアセットをルートの外へ移してください |
| `categories '…' and '…' overlap` | *(whole root)* の行か、内側のサブフォルダーの行を消してください |
| `output folder '…' is neither empty nor a previous content build` | *Output path* を空のフォルダーにするか、フォルダーを空にしてください |
| `… asset(s) under '…' belong to no category`（警告） | そのフォルダーのカテゴリを追加してください — それらのアセットはビルドされていません |
| `'…' is a V1 sprite atlas under the source root` | アトラスを Sprite Atlas V2 に移してください — [知っておくこと](#知っておくこと) 参照 |
| `profile is for … but the active build target is …` | プラットフォームを切り替えるか、*Target* を `NoTarget` に |

### コマンドラインでビルドする

CI ではバッチモードでプロファイルをビルドします:

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget Android -logFile build.log \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main
```

| オプション（`-contentlink.<キー> <値>`） | 値 |
|---|---|
| `profile` | プロファイル名、またはすべてのプロファイルなら `all`。省略するとアクティブなプロファイル |
| `output`、`source` | この実行のパスを上書き |
| `archive` | `None`・`Engine`・`Packed` |
| `compression` | `Uncompressed`・`LZ4`・`LZMA` |
| `maxArchiveMB` | 1 以上の整数 |
| `contentVersion` | この実行の Packed ビルドに刻むコンテンツバージョン |

上書きはその実行にだけ効き、設定ファイルは変わりません。ビルドの失敗、知らないオプションやプロファイルでは終了コード
**1** で終わります。ほかのエディターでプロジェクトを先に閉じてください: バッチモードにはプロジェクトのロックが必要です。

`xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` は同じオプションを受け取り、カタログだけを書きます — CI が
プレイヤービルドの前に最新のカタログだけを必要とするときに使います。このリポジトリでは `tools/build-content.sh` が両方の
呼び出しを包みます。

エディタースクリプトからは `ContentBuildRunner.Build(profile)` と `ContentBuildRunner.BuildAll()` が同じことをし、
`ContentBuildResult` を返します。

### ビルドのあと

ポストプロセッサーは、それを追加したプロファイルのビルドのたびに、ビルドのあとで走ります — 出力をアップロードしたり
コピーしたりする場所です。Editor アセンブリに置いてください:

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

それからビルドウィンドウの **Post-processors** に追加します。複数あれば、そこに並んだ順に走ります。例外を投げたステップは
ビルドを失敗させ、出力をそのまま残します。

## ロード

### まず登録

すべてのコンテンツディレクトリを、起動時に一度、**最初のロードの前に** 登録します:

```csharp
int dir = ContentService.RegisterDirectory(path);                      // Archive = None または Engine
// int dir = await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

どちらもディレクトリ id を返します（フォルダーが使えなければ `-1` とエラーのログひとつ）。同じフォルダーを二度登録すると
拒否されます。`ContentService.UnregisterDirectory(dir)` で外します — そのディレクトリのアセットをまだ保持していれば拒否
されます。

### スコープ

すべてのロードは **スコープ** に入ります。スコープが終わると、それがロードしたものは解放されます。

| スコープ | 得かた | 終わるとき |
|---|---|---|
| GameObject | `this.Scope()` / `gameObject.Scope()` | GameObject が破棄されたとき |
| Scene | `ContentScope.ForScene(scene)` | シーンがアンロードされたとき |
| Explicit | `parent.CreateChild("Stage3")` | `Dispose()` を呼んだとき — または親が終わったとき |
| Root | `ContentScope.Root` | アプリが終了したとき |

```csharp
// GameObject — 日常的なケース
var sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");

// Scene — シーンが生きている間
var sky = await ContentScope.ForScene(gameObject.scene).LoadAsync<Material>("Materials/Sky");

// Explicit — ステージ・画面・ポップアップ: いつ終えるかは自分で決める
var stage = ContentScope.Root.CreateChild("Stage3");
await stage.LoadAsync<AudioClip>("Audio/boss");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... ステージが進む ...
stage.Dispose();   // ステージがロードしたものがすべて戻る

// Root — アプリ全体の間
var font = await ContentScope.Root.LoadAsync<Font>("Fonts/Main");
```

どのスコープでも:

```csharp
var mat  = scope.Load<Material>("Materials/Steel");   // 同期
bool has = scope.Contains("Icons/sword");             // ここにロード済み: もう一度ロードしてもタダ
scope.Release("Icons/sword");                         // 任意: 早めに返す
```

- ひとつのスコープに同じ名前を二度ロードしても、保持はひとつです — 毎フレームロードしてもかまいません。
- 複数のスコープが同じ名前をロードできます: アセットは一度だけロードされ、最後のスコープが手放したときに解放されます。
- 別のスコープにアセットを渡すには `source.ShareTo(target, name)` か `target.Adopt(asset)` を使ってください。

### インスペクターのフィールド: リンク

**リンク** は名前を保持します。インスペクターで、ソースルートの下のアセットをドロップしてください:

```csharp
public ContentLinkSprite icon;              // ContentLinkGameObject、ContentLinkTexture2D もある
public ContentLink<AudioClip> music;        // どんなアセット型でも

image.sprite = await icon.LoadAsync(this);  // この GameObject のスコープへ
var clip = music.Load(this);                // 同期

// アイテムの交換: 古いものを返し、新しいものをロード
icon.Release();
icon.SetName("Icons/shield", "shield");
image.sprite = await icon.LoadAsync(this);
```

フィールドの色が状態を示します: 通常 — 問題なし、**琥珀色** — まだカタログにない（**Update catalogs** を押してください）、
**赤** — その名前がフィールドに入らないものをロードする。

### インスペクターのフィールド: GUID による参照

**参照** は名前の代わりにアセットの GUID を保持します: ファイルを移動・改名しても動き続けます。

```csharp
public ContentRef<Sprite> icon;
public ContentSceneRef level;

image.sprite = await this.Scope().LoadAsync(icon);
var scene = await this.Scope().LoadSceneAsync(level);
```

同じアセットの参照と名前は同じオブジェクトをロードします: `Release(icon)` は `LoadAsync<Sprite>("Icons/sword")` が
ロードしたものも解放します。

### プレハブのインスタンス

```csharp
var cat = await this.Scope().InstantiateAsync("Prefabs/Cat", parent);
```

プレハブはインスタンスが生きている間ロードされたままで、インスタンスを破棄すると解放されます。`Object.Instantiate(cat)` で
作った複製もプレハブを保持するので、スポナークラスは要りません。

### シーン

```csharp
// スコープが所有: スコープが終わるとアンロード (additive)
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
this.Scope().ReleaseScene(arena);   // 任意: 早めにアンロード

// インスペクターのフィールドから
public ContentSceneLink level;
var scene = await level.LoadAsync(this);

// 所有者なし、どのモードでも — SceneManager.UnloadSceneAsync で自分でアンロード
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

### Resources フォルダー

`res:` で始まる名前は、プレイヤーの `Resources` フォルダーからロードします — 同じスコープとフィールドを通るので、
プロジェクトを `Resources.Load` から呼び出しひとつずつ移せます:

```csharp
var boot  = this.Scope().Load<TextAsset>("res:Config/boot");                 // Resources/Config/boot.txt
var logo  = await this.Scope().LoadAsync<Sprite>("res:UI/logo[logo]");       // スプライト: [その名前]
var enemy = await this.Scope().InstantiateAsync("res:Prefabs/Enemy");
```

プレハブとそれが使うアセットは、`ContentService.SweepResources()` でしかメモリから出ていきません — ロード画面のように
引っかかりがかまわない場所で呼んでください。

### 生のハンドル

寿命を自分で管理するコード（プール・ストリーミング）のために:

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");
var tex = h.Asset;
h.Dispose();   // 返す — またはスコープに渡す: scope.Attach(h)
```

## 解放

ほとんどのコードは `Release` を呼びません:

| 状況 | すること |
|---|---|
| コンポーネント・ポップアップ・ステージ・シーンのコンテンツ | なし — 合うスコープにロード |
| アセットの多いステージや画面 | Explicit スコープひとつ、ステージが終わったら `Dispose()` |
| セルを再利用するスクロールリスト | セルごとに子スコープひとつ、セルを再利用するときに Dispose |
| 装備・スキンの交換 | 新しいものをロードする前に `Release(name)` か `link.Release()` |
| 別のシステムにアセットを渡す | `ShareTo(target, name)` か `target.Adopt(asset)` |

**仕組み。** スコープが終わると — GameObject の破棄、シーンのアンロード、`Dispose()` の呼び出し、アプリの終了 — それが
ロードしたものをすべて手放します。誰も保持していないアセットは **フレームごとに一度、フレームの終わりに** 解放されます。
あなたのコードがすべて走ったあとで、コードの途中では起きません。ひとつのフレームの中で手放してまたロードしても、何も
ロードし直されません。

**終え忘れたスコープ。** シーンのアンロードやアプリの終了まで開いたままの Explicit スコープは代わりに終えられ、そのパスと
ともに警告として残ります（`Root/Field/ForgottenPopup`）。どこで作ったかを見るには `ContentService.CaptureStackTrace = true`
にしてください。

## CDN からコンテンツをダウンロードする

`Packed` プロファイルの出力は、キャッシュフォルダーにダウンロードしてそこから登録できます。

**1. ビルドしてアップロードする。** 出力パスが `Assets/` の外にある `Packed` プロファイルを使います。ビルドのたびに出力
フォルダー全体 — アーカイブ、`archives.json`、`archives.hash` — を、プラットフォームとアプリのバージョンごとにひとつの URL へ
アップロードします。アーカイブの名前には中身のハッシュが入っているので永久にキャッシュしてかまいません。`archives.json` と
`archives.hash` は CDN でキャッシュ時間を短くしてください。

**2. 起動時に確認・ダウンロード・登録:**

```csharp
var options = new ContentRemoteOptions
{
    BuiltInDirectory = Path.Combine(Application.streamingAssetsPath, "MainContent"),   // 任意: アプリが同梱するコピー
    RetryCount = 3,
    Timeout = 30,
};
var remote = await ContentRemote.CheckAsync("Main", $"https://cdn.example.com/content/{platform}/Main", options);

if (remote.BytesToDownload > 0)
    ShowDownloadSize(remote.BytesToDownload);

if (await remote.DownloadAsync())   // 進捗は remote.Status をポーリング
{
    await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
    remote.CleanCache();            // 新しいビルドがもう使わないアーカイブを削除
}
else
    Debug.LogError(remote.Error);
```

- キャッシュにないアーカイブだけをダウンロードします。中断したダウンロードは再開し、MD5 が合わないファイルは決して使われ
  ません。
- オフラインなら、キャッシュが最後にダウンロードしたもの、次に `BuiltInDirectory` に戻ります。どちらを使ったかは
  `remote.Source`、その理由は `remote.Error` が教えてくれます。
- アプリがすでに同梱しているアーカイブ（`BuiltInDirectory`）は、ダウンロードせずにコピーします。
- あるプロファイルのキャッシュフォルダー **か** 同梱フォルダーのどちらかだけを登録してください。両方は不可です: エンジンが
  同じビルドの二つ目のディレクトリを拒否します。
- フォルダーを登録したままダウンロードしても安全です。次にそのフォルダーを登録したときに反映されます。
- エディターでシミュレーションがオンなら、確認はリクエストなしにプロジェクトから答えます。実際のダウンロードを試すには
  シミュレーションをオフにしてください。

| オプション | 既定 | 意味 |
|---|---|---|
| `CacheRoot` | `persistentDataPath/ContentLink` | キャッシュの置き場所: `<CacheRoot>/<profile>` |
| `BuiltInDirectory` | なし | アプリが同梱する Packed ビルド |
| `Timeout` | `0`（なし） | 新しいバイトが来ないままこの秒数が過ぎると、リクエストをやり直す |
| `RetryCount` | `0` | リクエストごとのリトライ回数 |
| `VerifyCachedFiles` | `false` | すでにキャッシュされたファイルの MD5 も検査 |
| `WebRequestOverride`、`TransformUrl` | なし | すべてのリクエスト（ヘッダー）や URL（署名）を調整 |

## ローカライズされたコンテンツ

ロケールは、言語が変えるファイルだけを同じパスに入れた二つ目のプロファイルです。本編のあとに登録すると、両方が持つ名前は
ロケールのファイルが答え、ほかの名前は本編のままです。

| プロファイル | ソースルート | 出力パス |
|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` |
| `Main.ko` | `Assets/Content.ko` | `Assets/StreamingAssets/MainContent.ko` |

`Assets/Content.ko/Icons/flag.png` が `Assets/Content/Icons/flag.png` の代わりになります — どちらも `Icons/flag` です。
ロケールのルートは本編のルートの **隣に** 置き、二つのプロファイルの名前のスタイルをそろえてください。

```csharp
static string Dir(string name) => Path.Combine(Application.streamingAssetsPath, name);

ContentService.RegisterDirectory(Dir("MainContent"));      // 本編を先に
ContentService.RegisterDirectory(Dir("MainContent.ko"));   // 次にロケール、最初のロードの前に

var flag = await this.Scope().LoadAsync<Sprite>("Icons/flag");   // 韓国語の旗
```

**実行中に言語を切り替える。** ロケールが変えるものはひとつのスコープにロードし、切り替えるときにまとめて手放せるように
します:

```csharp
ContentScope localized = ContentScope.Root.CreateChild("Localized");
int locale = -1;

bool SwitchLocale(string code)   // "ko"、"ja"、… — 本編だけなら null
{
    localized.Dispose();                                    // 古いロケールのアセットを手放す
    if (locale >= 0 && !ContentService.UnregisterDirectory(locale))
        return false;                                       // まだどこかが保持している — エラーが何かを教える
    locale = code == null ? -1 : ContentService.RegisterDirectory(Dir("MainContent." + code));
    localized = ContentScope.Root.CreateChild("Localized");
    return true;                                            // ロードし直す: 名前はもう新しい言語で答える
}
```

Unity の Localization パッケージを使うなら、文字列の翻訳はそちらに任せ、`LocalizationSettings.SelectedLocaleChanged` から
`SwitchLocale` を呼んでください。

## エディターシミュレーション

*Window > ContentLink > Editor Simulation*（既定でオン）。Play Mode でプロファイルの出力フォルダーを登録すると、その
プロファイルを **プロジェクトが肩代わり** します: 追加・編集・削除したアセットがそのまま見え、ビルドするものはありません。
フォルダーが存在しなくてもかまいません。

- プレイヤーと同じく、登録は必要です。
- ディスクには何も書きません。
- エディターでは解放したアセットがメモリに残ります — メモリと解放の動きは、シミュレーションをオフにして確かめてください。
- アーカイブは使いません。実際のビルド・アーカイブ・ダウンロードを試すには、シミュレーションをオフにしてください。
- Play Mode が終わると、ロードしたのにまだどのカタログにもない名前をコンソールが知らせます: 出荷前にビルドしてください。

## 診断

- *Window > ContentLink > Dump Scope Tree*（Play Mode）、または `ContentScope.DumpTree()`:

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```

- `ContentService.Snapshot(list)` — いま保持しているすべての参照: その名前、取ったフレーム、そして取った場所
  （`ContentService.CaptureStackTrace = true` のとき）
- `ContentService.LiveAssetCount` — 保持しているアセットの数
- `ContentScope.DefaultWarnThreshold` — ひとつのスコープがこれより多いアセットを保持したら警告
- ビルドウィンドウが最後のビルドを表示し、コンソールにはビルドごとに `[ContentLink] built …` が一行残ります

## Addressables・Resources からの移行

| Addressables | ContentLink |
|---|---|
| グループとバンドル | プロファイルとカテゴリ、`Packed` アーカイブ |
| `Addressables.LoadAssetAsync` + `Release` | `scope.LoadAsync` — 解放は自動 |
| `AssetReference` / `AssetReferenceSprite` フィールド | `ContentRef<T>` — フィールドの型を変えるだけで、保存された GUID が引き継がれる |
| `ref.LoadAssetAsync()` + `ref.ReleaseAsset()` | `this.Scope().LoadAsync(ref)` |
| `InstantiateAsync` + `ReleaseInstance` | `scope.InstantiateAsync` — インスタンスを破棄 |
| *Use Asset Database* プレイモード | エディターシミュレーション |
| `CheckForCatalogUpdates` / `UpdateCatalogs` | `ContentRemote.CheckAsync` |
| `GetDownloadSizeAsync` / `DownloadDependenciesAsync` | `remote.BytesToDownload` / `remote.DownloadAsync()` |
| `ClearDependencyCacheAsync` | `ContentRemote.ClearCache(profile)` / `remote.CleanCache()` |

`AssetReference` から引き継いだ `ContentRef<T>` は、アセットがコンテンツルートの下になければなりません（そこへ移しても
GUID は保たれます）。

| Resources | ContentLink |
|---|---|
| `Resources.Load<TextAsset>("Config/boot")` | `scope.Load<TextAsset>("res:Config/boot")` |
| `Resources.Load<Sprite>("UI/logo")` | `scope.Load<Sprite>("res:UI/logo[logo]")` |
| `Resources.UnloadAsset(asset)` | なし — スコープが解放 |
| `Resources.UnloadUnusedAssets()` | `ContentService.SweepResources()` |

## 知っておくこと

- **メインスレッド専用。** ロード・登録・キャンセルはメインスレッドで。
- **ビルドひとつにプラットフォームひとつ。** あるプラットフォーム向けにビルドしたコンテンツディレクトリは、別の
  プラットフォームではロードできないことがあります。
- **スプライトアトラス** には *Project Settings > Editor > Sprite Packer > Mode* が Sprite Atlas V2 であることが必要です。
  旧（V1）の `.spriteatlas` ファイルはコンテンツルートの下で拒否されます — V2 に移してください。
- サブアセットとしてロードできるのは **スプライトだけ** です。モデルファイルの中のメッシュは単独ではロードできません。
- **シーンは共有されません。** シーンを二度ロードするとインスタンスが二つでき、それぞれ別にアンロードされます。
- **非アクティブな複製。** 非アクティブで作った複製は、初めて起きるまで何も保持しません。元が先に破棄されうるなら、
  `Instantiate` の直後に `clone.Scope()` を呼んでください。
- **参照と名前は同じ枠を使います。** 一方を解放すると、もう一方も解放されます。二つのフィールドを別々に解放したいなら、
  リンクか子スコープを使ってください。
- **コンテンツルートと `Resources` フォルダーは離しておいてください。** 両方に入ったファイルは、二つの名前で二度ビルド
  されます。
- **コンテンツシーンのロード中に Play Mode を止めないでください** — 6000.6 のエディターは、ContentLink の有無に関係なく
  その場合にクラッシュすることがあります。
- **エディターでは `Resources.UnloadAsset` は何もアンロードしません。** `res:` アセットの解放はプレイヤーで確かめてください。

## ライセンス

Apache License 2.0
