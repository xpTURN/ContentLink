# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | [한국어](README.ko-KR.md) | **日本語** | [简体中文](README.zh-CN.md)

ContentLink は、Unity 6.6 に組み込まれた **コンテンツディレクトリ** から、スプライト・プレハブ・シーン・オーディオといった
ゲームコンテンツを **名前で** ロードし、あらゆるロードシステムが利用者に押し付けてきたひとつの仕事 — ロードしたアセットを
いつ解放してよいかを決めること — を肩代わりします。

**問題。** `Resources`、AssetBundle、Addressables のいずれも、ロードごとに解放を手作業で、すべてのスクリプトで対にしなければ
なりません。ひとつ忘れればアプリが終了するまでメモリは増え続け、早すぎればテクスチャが消えます。犯人を探す道具は、事が
起きた後にプロファイラーで当たりを付けるだけです。

**ContentLink がすること。** すべてのロードは **スコープ** に属します — 要求した GameObject、そのシーン、アプリ、あるいは
ステージやポップアップのために自分で作ったもの。スコープが終わると（オブジェクトの破棄・シーンのアンロード・アプリの終了・
自分で Dispose）、それが保持していたものは全部解放されます。ロードすれば、それで終わりです。忘れた解放はピークメモリを少し
上げるだけで、正しさを損ないません。同じ考え方がシリアライズ参照（インスペクター描画付きの `ContentLinkSprite icon;`）、
インスタンス化したプレハブ、コンテンツシーンにも及びます。

**ほかに入っているもの。** コンテンツディレクトリそのものを作るビルドパイプライン — `ProjectSettings` に保存されるプロファイル、
ビルドウィンドウ、ダウンロードパイプライン向けの決定論的なサイズ分割アーカイブ、後処理フック、コマンドライン — と、Play Mode が
プロジェクトから直接ロードするので作業中は何もビルドしなくてよいエディターモード。依存は Unity 6000.6 だけです。

> **ロードして、解放のことは忘れてください。** アセットは所有者 — GameObject、シーン、アプリ — が生きている間だけ生き、
> `Release()` はつねに最適化であって、義務ではありません。

---

## 目次

1. [得られるもの](#得られるもの)
2. [要件とインストール](#要件とインストール)
3. [五つの概念](#五つの概念)
4. [クイックスタート](#クイックスタート)
5. [名前](#名前)
6. [コンテンツのビルド](#コンテンツのビルド)
7. [コンテンツのロード](#コンテンツのロード)
8. [Release はいつ呼ぶか](#release-はいつ呼ぶか)
9. [解放はどう起きるか](#解放はどう起きるか)
10. [エディターシミュレーション](#エディターシミュレーション)
11. [診断](#診断)
12. [AssetLink · Addressables からの移行](#assetlink--addressables-からの移行)
13. [注意と制限](#注意と制限)
14. [リポジトリ構成とテスト](#リポジトリ構成とテスト)
15. [ライセンス](#ライセンス)

---

## 得られるもの

| 領域 | 機能 |
|---|---|
| ロード | `scope.LoadAsync<T>("Icons/sword")` — アセットはスコープのもの。解放するものがない |
| スコープ | 自動で終わる Root / Scene / GameObject / Explicit スコープ · スコープごとの重複排除 · パス付きのリーク警告 |
| シリアライズ参照 | `ContentLink<T>` / `ContentSceneLink`（名前）と `ContentRef<T>` / `ContentSceneRef`（GUID — ファイルを移動・改名しても保たれる）フィールド — インスペクター描画が埋めて検証する |
| インスタンス | `scope.InstantiateAsync("Prefabs/Cat")` — プレハブ参照はインスタンスとその複製が持つ |
| シーン | スコープやリンクが所有するコンテンツシーン · どのモードでも非所有ロード · 開いているシーンの追跡 |
| 決定論的な解放 | エンジンへの解放はフレームごとに一箇所（`Collect()`）で — コードの途中では起きない |
| ビルドプロファイル | ソースルート · 出力 · ディレクトリごとに複数のカテゴリ（カタログ） · アーカイブモード · 圧縮 · エンジンオプション — `ProjectSettings` に保存 |
| ビルドウィンドウ | *Window > ContentLink > Build Window*: Undo できるプロファイル編集 · 検証メッセージ · ひとつまたは全部をビルド |
| パックアーカイブ | ビルドを約 `MaxArchiveMB` ずつのアーカイブに決定論的に分割 · ランタイムがひとつのディレクトリにマウントし直す |
| 後処理 | `IContentBuildPostProcess` の各ステップがビルド後に、並べた順に走る |
| コマンドライン | `-executeMethod` で `ContentBuildCLI.Build` · 上書き · `profile all` · 終了コード · `tools/build-content.sh` |
| エディターシミュレーション | Play Mode でプロファイルのディレクトリをプロジェクトが肩代わりする: 追加・編集・削除したアセットがそのまま見え、ビルドは不要 — 何も書かない |
| 診断 | スコープツリーのダンプ · 参照スナップショット · 最後のビルドの要約 |

## 要件とインストール

- Unity **6000.6** 以降（`Unity.Loading` コンテンツディレクトリ API）。
- ほかに依存なし。

**git から** — *Window > Package Manager > + > Add package from git URL*:

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

**ローカルチェックアウトから** — `Packages/manifest.json`:

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

このリポジトリの `samples/LinkSample` が、まさにそのように配線された Unity プロジェクトです。

## 五つの概念

| 概念 | 何か |
|---|---|
| **コンテンツディレクトリ** | エンジンがアセットから作るフォルダ（`BuildPipeline.BuildContentDirectory`）。ランタイムで登録し、そこからロードします。ひとつのビルドターゲット向けに作られます。 |
| **カタログ** | ビルドがディレクトリに入れる `ContentCatalog` アセット: 名前と、それが指すエンジン id の一覧。ひとつのディレクトリが複数のカタログ（*カテゴリ* ごとにひとつ）を持てます。 |
| **名前** | ロードに使うもの: ソースルートからの相対パスから拡張子を除いたもの — `Icons/sword`。サブアセットは `Icons/sword[sword]`、シーンは `Scenes/Arena`。 |
| **スコープ** | ロードしたすべてのものの所有者。スコープはツリー（Root → Scene → GameObject → Explicit）をなし、自動で終わります。 |
| **リンク** | 名前を保持するシリアライズフィールド（`ContentLinkSprite icon;`）。インスペクターで設定し、所有者を渡してロードします。 |

## クイックスタート

**1. ソースルートの下にアセットを置きます** — 既定は `Assets/Content`:

```
Assets/Content/Icons/sword.png        →  "Icons/sword"            (Texture2D)
                                         "Icons/sword[sword]"     (その Sprite サブアセット)
Assets/Content/Prefabs/Cat.prefab     →  "Prefabs/Cat"
Assets/Content/Scenes/Arena.unity     →  "Scenes/Arena"
```

**2. ビルドします** — *Window > ContentLink > Build Window*。初回はウィンドウに `Main` プロファイルが表示されます:
ソースルート `Assets/Content`、ルート全体を覆うカテゴリひとつ、出力 `Assets/StreamingAssets/MainContent`、アーカイブ `None`
（フラットファイル）。**Build 'Main'** を押してください。（*Window > ContentLink > Build Content Directory* はウィンドウを
開かずにアクティブなプロファイルをビルドします。）

`StreamingAssets` はプレイヤーの中に同梱されるので、下のコードはそのフォルダを見つけます。ダウンロードパイプラインなら
`Archive` を `Packed` に、出力を `Assets/` の外にして、アーカイブを自分で配布してください — [コンテンツのビルド](#コンテンツのビルド) 参照。

**3. 起動時にディレクトリを一度登録します** — 何かをロードする前に:

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    async void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");

        // Archive = None（フラットファイル · 移行された Main プロファイル）または Engine（content0.archive ひとつ）:
        ContentService.RegisterDirectory(path);

        // Archive = Packed（新しいプロファイルの既定）: アーカイブ数個 + archives.json
        // await ContentService.RegisterPackedDirectoryAsync(path);
    }
}
```

**4. スコープにロードします。** 解放するものはありません。スプライトは GameObject とともに消えます。

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

**5. あるいはシリアライズリンクを使い**、インスペクターでアセットを選びます:

```csharp
public ContentLinkSprite icon;                       // Assets/Content/Icons/sword.png をドロップすると → "Icons/sword" + スプライト "sword"
async void Start() => image.sprite = await icon.LoadAsync(this);   // this = この GameObject のスコープ（手順 4 と同じ）。別のスコープを渡してもよい
```

エディターでは手順 2 を丸ごと飛ばせます。[エディターシミュレーション](#エディターシミュレーション)（既定でオン）が有効なら、
プロファイルの出力フォルダを登録した時点で Play Mode はそのディレクトリを **プロジェクトから** 提供します — 追加・編集・削除した
アセットがそのまま見え、フォルダが存在しなくても構いません。ビルドは出荷するときに。

## 名前

- 名前は **プロファイルのソースルートからの相対** アセットパスから拡張子を除いたものです: `Icons/sword`。
  カテゴリは名前を変えません — `Icons/sword` はどのカタログにあっても `Icons/sword` です。
- **サブアセット** は `<メイン>[<サブ>]`: `sword.png` の中のスプライト `sword` は `Icons/sword[sword]`、スプライトシート
  `sheet.png` は `Icons/sheet[run_01]`、`Icons/sheet[run_02]`、…
- **リンク** はスプライト名を分けて持ちます: `("Icons/sheet", "run_01")` は `Icons/sheet[run_01]` をロードします。スプライト名
  なしで完全な名前を書いても、同じキーとしてロードされます。
- **シーン** もアセットと同じように名付けられます: `Scenes/Arena`。アセットとシーンはひとつの名前空間を共有します。
- 名前は **プロファイル内で一意** です。同じ名前になる二つのアセット（カテゴリの重なり、二つのフォルダの同名）は、両方の
  パスをコンソールに出してビルドを失敗させます。
- インスペクター描画は名前の状態を色で示します: 空なら無色、まだどのカタログも知らない名前は **琥珀色**（ビルドするか
  *Update Content Catalog* を実行）、フィールドが保持できないものに解決される名前は **赤**。リンクの名前行は
  スプライトを `Icons/sheet[run_01]` と表示し、そこに入力した名前はカタログにあれば二つのフィールドに分けて保存されます。

## コンテンツのビルド

### プロファイル

**プロファイル** はコンテンツディレクトリひとつを記述します。`ProjectSettings/ContentLinkBuild.asset`（`ContentBuildSettings`）に
保存され、ビルドウィンドウで編集します。ひとつのプロファイルが **アクティブ** です: メニュー項目と旧来の静的 API はそれに作用します。

| フィールド | 既定値（新しいプロファイル） | 意味 |
|---|---|---|
| `Name` | — | 一意。アクティブプロファイルとコマンドラインはこの名前で選ぶ |
| `SourceRoot` | `Assets/Content` | 名前の基準になるフォルダ |
| `OutputPath` | `Build/Content/<Name>` | ディレクトリの書き出し先。既定は `Assets/` の外 — 下記参照 |
| `Catalogs` | ルート全体のカテゴリひとつ | カテゴリ = ルートのサブフォルダ + それが埋めるカタログアセット |
| `Archive` | `Packed` | `None`（フラット） · `Engine`（エンジンの単一アーカイブ） · `Packed`（サイズ分割アーカイブ） |
| `MaxArchiveMB` | `20` | `Packed`: アーカイブを閉じるサイズ — ファイルひとつ分の前後 |
| `Compression` | `LZ4` | `Uncompressed` · `LZ4` · `LZMA` — アーカイブにのみ適用。フラットビルドはつねに非圧縮 |
| `CleanBuildCache` | オフ | エンジンオプション: ビルドキャッシュを無視 |
| `DisableWriteTypeTree` | オフ | より小さい出力 — **まったく同じエンジンバージョンでのみ** ロードできる（`SerializeUnityVersion` は自動で付く） |
| `FailBuildWhenErrorsLogged` | オン | ビルド中に記録されたエラーはビルドを失敗させる |
| `Target` | `NoTarget` | `NoTarget` = アクティブなビルドターゲットなら何でも。それ以外は別のターゲットでのビルドを拒否 |
| `PostProcessors` | なし | `IContentBuildPostProcess` 実装の型名、実行順 |

**カテゴリ。** サブフォルダ（`Icons`、`Prefabs`、`Levels/Forest`、ルート全体なら `""`）を追加すると、ビルドはそのフォルダの
アセットを専用のカタログに入れます。プロファイルのすべてのカタログは **ひとつの** ディレクトリに入るので、ランタイムはひとつの
名前空間を見ます。重なるカテゴリ、自分のルートの下に置かれたカタログ、重複する名前は、何かを書く前に拒否されます。
パス文字（`/ \ : * ? " < > |` — 名前はアーカイブ名や既定パスに入ります）を含むプロファイル名、末尾に区切り文字が付いた
ソースルート、ソースルートの中にある、あるいはルートを含む出力フォルダも同様です。新しいカテゴリのカタログアセットは初回ビルド時に
`Assets/ContentCatalogs/<プロファイル>.<カテゴリ>.asset` に作られ（フォルダがなければフォルダも）、既存のカタログをフィールドに
指定することもできます。

**出力フォルダ。** フォルダは何かを書く前に検査されます: 空のフォルダか、以前のコンテンツビルド（`BuildManifestHash.txt` が
ある）だけを受け入れ、ほかのものが入ったフォルダには手を付けずビルドを失敗させます。空にするのはエンジンが走る直前なので、それより
前の段階 — 検証、カタログ — で失敗したビルドは、以前の出力をそのまま残します。
`Assets/` の中の出力（例えば `StreamingAssets`）は Unity がインポートし、ビルド後にリフレッシュされます。`Assets/` の外の出力は
そうならず、別途配布するコンテンツにはそちらが向いています。

**ビルドターゲット。** コンテンツディレクトリはエディターの **アクティブなビルドターゲット** 向けに作られ、別のターゲットでは
ロードできないことがあります。ウィンドウ、ログ、最後のビルドの要約は、つねにどのターゲット向けのビルドかを示します。

**静的ビルダーからの移行。** スクリプトで `ContentDirectoryBuilder.SourceRoot` / `CatalogPath` / `OutputPath` を設定していた
プロジェクトはそのまま動きます: 設定のないプロジェクトはその既定値から作った `Main` プロファイル（`Assets/Content` →
`Assets/StreamingAssets/MainContent`、フラット、非圧縮）を受け取り、静的プロパティはアクティブプロファイルのビューとして残ります。
セッターで値を入れるとエディターセッションの間だけプロファイルを上書きし、ウィンドウがそのことを知らせます。

### アーカイブモードと登録

| `Archive` | 書き出されるもの | 登録 |
|---|---|---|
| `None` | エンジンのフラットファイル（`*.cf`、`*.resS`、マニフェスト） | `ContentService.RegisterDirectory(path)` |
| `Engine` | `content0.archive` ひとつ（エンジンの `UseArchive`） | `ContentService.RegisterDirectory(path)` |
| `Packed` | `<プロファイル>.manifest-<ハッシュ>.archive`、`<プロファイル>.<ビット列>-<ハッシュ>.archive`、… + `archives.json` | `await ContentService.RegisterPackedDirectoryAsync(path)` |

`Packed` は配布用です: 単一アーカイブはダウンロード単位として悪く、エンジンにはサイズオプションがないので、パッケージがフラット
ビルドを自分でパックします。既定の規則は **ハッシュ接頭辞トライ** です: マニフェストとそのハッシュファイルは小さな専用アーカイブに、
上限より大きいファイルは単独のアーカイブに、残りはキーの MD5 のビットでバケットに分け、上限に収まらないバケットだけを次のビットで
分割します。キーは、シリアライズファイルならマニフェスト ID（エンジンがアセットの同一性から導き、内容が変わっても保たれる）、
`.resS`/`.resource` ならファイル名です。そのため編集したアセットは自分のアーカイブの中で変わり、追加・削除はアーカイブひとつ
（バケットが分割・結合される場合は隣のひとつも）だけに触れ、ほかのアーカイブは名前もバイトもそのままです — パッチパイプラインが
望む性質です。（`.resS` は名前がキーなので、テクスチャを編集するとピクセルデータはあるアーカイブから別のアーカイブへ移ります。）
アーカイブ名はビット経路 + ファイル一覧のハッシュ（`Main.0110-a3f9c2e1.archive`）なので、同じ名前が異なる内容を意味することは
ありません。**同じ入力はつねにバイト単位まで同じにパックされます。** アーカイブ数は単純詰めより約 1.5 倍になります（リーフは上限の
0.6 倍前後）— 上限近くにしたければ `MaxArchiveMB` を上げてください。上限は *入力* バイト基準です — LZ4 ならディスク上のアーカイブは
その半分ほどになります。`archives.json` はアーカイブごとにサイズ・入力サイズ・CRC・中のファイル一覧を記録します: パッチャーはまず
これを取得し、持っていない名前をダウンロードし、消えた名前を削除します。CRC はエンジンがアーカイブの内容に対して報告する値であり、
ディスク上のファイルの CRC32 ではないので（実測）、ダウンロードの検証はサイズか自前のハッシュで行ってください。

**分割規則のカスタマイズ。** Editor アセンブリで `IContentArchivePartitioner` を実装し、プロファイルの `Partitioner` で選びます
（コマンドラインでは `-contentlink.partitioner <型名>`、`trie`、`sequential`）。実装はファイルとサイズ、マニフェストファイル、
パース済みマニフェスト、ビルドされたロードブル（カタログとディスク上のファイル付き）を受け取り、アーカイブ一覧（名前 + ファイル）を
返します。組み込みの `SequentialFillPartitioner` は素朴なソート詰めで、局所性と引き換えに少なく満杯のアーカイブを得るためのものです。
パッカーはどの答えも検査し（すべてのファイルがちょうど一度 · 空のアーカイブなし · 平易で一意な名前）、ファイル順を変えてもう一度
呼び、答えが違えばビルドを失敗させます。組み込み実装のように名前に内容ハッシュを入れてください。そうしないとキャッシュされた
ダウンロードが古くなります。実装を切り替えたり `MaxArchiveMB` を変えたりすると再分割されます — 一度だけの大きなダウンロードです。

ランタイムの `RegisterPackedDirectoryAsync` はすべてのアーカイブをひとつの名前空間にマウントし、マウントパスをフラット
ディレクトリのように登録します。`UnregisterDirectory` がアンマウントし直します。フォルダに `archives.json` がない、索引が
アーカイブをひとつも挙げていない、あるいはいずれかのアーカイブのディスク上のサイズが索引と異なる場合は、エラーとともに `-1` を
返し、何もマウントしません。アーカイブがマウントできない場合は、どれかを示す例外を投げます。

ダウンロードで端末に届いたコンテンツは、登録する前に検査する価値があります。`archives.json` はアーカイブごとの `Bytes` と `Crc` を
記録しています: `RegisterPackedDirectoryAsync` はディスク上のサイズが索引と異なるアーカイブを、何かをマウントする前に拒否しますが、
同じ長さの破損は `Crc` の比較でしか捕まらず、それは呼び出し側の仕事です — エンジンは破損したコンテンツファイルで失敗する代わりに
ブロックするので、先に検証してください。

### 後処理

```csharp
using xpTURN.ContentLink.Editor;

public sealed class UploadStep : IContentBuildPostProcess
{
    public void OnBuilt(ContentBuildResult r)
    {
        // r.ProfileName, r.Target, r.OutputPath, r.Files（ソート済み）, r.Archives, r.Manifest, r.BuiltNames, r.Milliseconds
    }
}
```

実装はウィンドウで選び（`TypeCache` で見つけます）、そこで順序を決めます。順序はプロファイルに保存され、それが実行順です。
見つからない型や例外を投げるステップは **ビルドを失敗させ**、確認できるよう出力は残します。ビルドのほかの段階が投げても同じ
ように報告されます: 結果が理由を持ち、ウィンドウ・ログ・コマンドラインのすべてがそれを示します。ステップはパッキングの後に走るので、
`r.Archives` は最終状態です。

### メニュー項目とスクリプティング

| どこで | 何を |
|---|---|
| *Window > ContentLink > Build Window* | プロファイルの編集 · ひとつ/全部のビルド · メッセージと最後の結果 |
| *… > Build Content Directory* | アクティブなプロファイルをビルド |
| *… > Update Content Catalog* | ビルドせずにアクティブなプロファイルのカタログだけ書き直す（リンクが琥珀色から無色に） |
| `ContentBuildRunner.Build(profile)` / `BuildAll()` | コードから同じこと · `ContentBuildResult` を返す |

### コマンドライン

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget StandaloneOSX -logFile <log> \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.compression LZ4
```

`tools/build-content.sh` がこの呼び出しを包みます（先にプロジェクトロックを検査 · 終了コードを伝搬 · ログの `[ContentLink …]` 行を
出力）:

```
tools/build-content.sh -p samples/LinkSample -contentlink.profile Main
tools/build-content.sh -p <project> -t Win64 -contentlink.profile all      # -t は -buildTarget に渡る (Win64, OSXUniversal, Linux64, Android, iOS, …)
```

| キー（`-contentlink.<key> <value>`） | 値 |
|---|---|
| `profile` | プロファイル名、または `all`（大文字小文字を問わない · 全部 · 最初の失敗で止まる）。省略 = アクティブなプロファイル |
| `source`, `output` | パス |
| `archive` | `None` · `Engine` · `Packed` |
| `maxArchiveMB` | 1 以上の整数 |
| `compression` | `Uncompressed` · `LZ4` · `LZMA` |
| `cleanBuildCache`, `disableWriteTypeTree`, `failBuildWhenErrorsLogged` | `true` · `false` |
| `postProcessors` | カンマ区切りの型名 |

上書きは指定したプロファイルを **メモリ上でだけ** 変えます — コマンドラインが設定ファイルを書くことはありません（`profile all` は
上書きを受け付けません）。ビルド失敗、未知のキー、存在しないプロファイルは終了コード **1** です。プロジェクトが別のエディター
インスタンスで開かれていてはいけません: バッチモードはプロジェクトロックを必要とします。

**カタログのみ。** `-executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` は同じ引数を取りますが、
プロファイルのカタログを書き直すだけです — ビルドも出力フォルダもありません — *Update Content Catalog* のコマンドライン版です
（`tools/build-content.sh -c -contentlink.profile Main`）。CI がカタログアセットだけ最新にすればよいとき（プレイヤービルド前の
リンク検証）や、ディレクトリを別のパイプラインがビルドするときに使います。ビルドはすでにカタログを毎回書き直すので、ビルド側で
追加で有効にするものはありません。`profile all` はプロファイルごとに更新し、最初の失敗で止まります。`source` の上書きはカタログの
基準ルートになります。終了コードは上記と同じです。

## コンテンツのロード

### スコープ

| スコープ | 作り方 | 終わるとき | 親 |
|---|---|---|---|
| Root | 自動（`ContentScope.Root`） | `Application.quitting` | — |
| Scene | `ContentScope.ForScene(scene)` | `SceneManager.sceneUnloaded` | Root |
| GameObject | `gameObject.Scope()` / `component.Scope()` | `OnDestroy` | そのシーン |
| Explicit | `parent.CreateChild("Stage")` | 自分の `Dispose()`、なければ親が終わるとき | 任意 |

**GameObject スコープ** — いちばんふつうの場合。アセットはオブジェクトとともに消えます:

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        var scope = this.Scope();                                   // この GameObject のスコープ（gameObject.Scope() でも同じ）
        image.sprite = await scope.LoadAsync<Sprite>("Icons/sword[sword]");
    }                                                               // OnDestroy で解放 — ほかに書くことはない
}
```

**Scene スコープ** — シーンが生きている間だけ生きるべきアセット:

```csharp
var scope  = ContentScope.ForScene(gameObject.scene);               // シーンごとにひとつ · 最初に使うとき作られる
var skybox = await scope.LoadAsync<Material>("Materials/Sky");      // シーンがアンロードされたら解放
```

**Explicit スコープ** — ステージ、画面、ポップアップ: 終わるときは自分で決め、遅くとも親が終わるときに終わります:

```csharp
var stage = ContentScope.Root.CreateChild("Stage3");                // または this.Scope().CreateChild("Popup")
await stage.LoadAsync<AudioClip>("Wav/descent");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... ステージが進む ...
stage.Dispose();                                                    // 保持していたものを全部返す
```

**Root スコープ** — アプリが生きている間:

```csharp
var atlas = await ContentScope.Root.LoadAsync<Texture2D>("UI/Atlas");   // Application.quitting で解放
```

**すべてのスコープで:**

```csharp
var mat   = scope.Load<Material>("Materials/Steel");                // 同期（エンジンのロードを待つ）
bool held = scope.Contains("Icons/sword");                          // true: もう一度ロードしてもコストはない
scope.Release("Icons/sword");                                       // 任意: 早めに返す
var token = scope.DisposedToken;                                    // スコープが終わるとキャンセル — 自分の await に渡す
```

- **重複排除。** ひとつのキーはスコープごとに最大一度だけ保持されます。同じキーを繰り返し（毎フレームでも）ロードしても増えません。
- **リークの合図。** シーンのアンロードや終了が回収した明示的スコープは、パス（`Root/Field/ForgottenPopup`）付きで警告を
  残します。`ContentService.CaptureStackTrace = true` なら作られた場所も付きます。親が Dispose されたり所有 GameObject が
  破棄されたりして終わるのは正常な寿命で、警告しません。
- **過剰保持の警告。** `ContentScope.DefaultWarnThreshold`（または `scope.WarnThreshold`）を超える数のアセットを保持すると
  警告します — スコープの失敗の形はリークではなく、持ちすぎです。
- **一度もアクティブになっていないオブジェクト** は `OnDestroy` を受けません。そのスコープはシーンがアンロードされるとき回収されます。
- **移動したオブジェクト**（`MoveGameObjectToScene`、`DontDestroyOnLoad`）はアセットを保ちます: 元のシーンがアンロードされると、
  スコープは今いるシーンへ引き渡されます。
- **スコープ間。** `source.ShareTo(target, key)` は `target` に自分の参照を与え、`target.Adopt(asset)` はすでに保持している
  アセットを受け取ります。この二つなしに生のアセットを別のシステムへ渡さないでください。
- スコープはアセットを解放するだけで、GameObject を破棄することはありません。

### シリアライズリンク

```csharp
public ContentLinkSprite icon;                               // ContentLinkGameObject, ContentLinkTexture2D もある
public class ContentLinkAudio : ContentLink<AudioClip> { }   // ほかの型は派生する — 描画はこれらも描く

image.sprite = await icon.LoadAsync(this);                   // 所有者は必須（null は ArgumentNullException）— Component/GameObject ならその GameObject スコープ、ContentScope ならそのスコープ
image.sprite = await icon.LoadAsync(stage);                  // 例: 明示的スコープに入れる
var clip = music.Load(this);                                 // 同期

// 装備の交換: 古いものを早めに返す（任意）
icon.Release();
icon.SetName(table[itemId].IconName);
image.sprite = await icon.LoadAsync(this);

icon.SetName("Icons/sheet", "run_01");                       // スプライト: メイン名とスプライト名を分けて
other.SetName(icon.Name, icon.SubObjectName);                // リンクのコピー: Name はメイン名だけ

icon.IsValid; icon.Asset; icon.IsLoading; icon.Scope; icon.Name; icon.SubObjectName; icon.RuntimeKeyIsValid();
```

### GUID によるシリアライズ参照

```csharp
public ContentRef<Sprite> icon;                          // ContentRef<GameObject>, ContentRef<Enemy>（プレハブ上のコンポーネント）…
public ContentSceneRef level;

image.sprite = await this.Scope().LoadAsync(icon);       // 名前と同じくスコープが所有する
this.Scope().Release(icon);                              // 早めに返す（任意）
var enemy = await stage.InstantiateAsync(enemyRef);      // GameObject を返す
var scene = await this.Scope().LoadSceneAsync(level);

icon.IsSet; icon.RuntimeKeyIsValid(); ContentService.TryGetName(icon, out var name);
```

- 参照はパスではなくアセットの **GUID**（とスプライト名）を持ちます: ファイルを移動・改名しても、カタログを更新すれば
  そのまま動きます。フィールドは Addressables の `AssetReference` と同じです（`m_AssetGUID`, `m_SubObjectName`）。
- ビルドがカタログエントリごとに GUID を記録し、ランタイムは参照をその **カタログ名** に解決して名前でロードします。
  そのため同じアセットの参照ロードと名前ロードは **スロットひとつ · スコープキーひとつ** です: `Release(icon)` は
  `LoadAsync<Sprite>("Icons/sword")` も返し、その逆も同じです。
- 参照はロードではなくキーだけを持ちます — コピーしても問題なく、ロードはスコープを通します。フィールドごとに個別に
  解放したいなら、名前を一度取得してリンクを使います: `ContentService.TryGetName(icon, out var name)` →
  `new ContentLink<Sprite>(name)`。
- サブアセットは **スプライト、名前で** です（Addressables と同じ）。スライス名を変えると参照は切れます。
- `RuntimeKeyIsValid()` は `ContentLink` と同じく「登録済みカタログ（またはエディターシミュレーション）がロードできる」
  の意味です — Addressables の「GUID が入っている」は `IsSet` です。
- **インスペクターでは。** アセットを選ぶと GUID が保存され、2 行目に解決される名前が読み取り専用で表示されます。
  赤: その GUID のアセットがない、スプライト名が消えた（スライスの改名）、フィールドの型と違う。黄: まだカタログにない、
  どのコンテンツルートの外にもある（ルートの下へ移しても GUID は保たれる）、スプライトアトラス。ビルドに載せられない
  選択はリンクと同じく拒否し、フィールドにすでにある値は拒否せず表示だけします。

### インスタンス

```csharp
var go = await scope.InstantiateAsync("Prefabs/Cat", parent);
```

プレハブ参照は `scope` ではなく **インスタンス** のものです（`scope` は自分が先に終わったとき要求をキャンセルするだけ）。
インスタンスを破棄すると返ります。`Object.Instantiate(instance)` で作った複製は `Awake` で参照を引き継ぐので、元が先に
破棄されてもテクスチャを保ちます。スポナークラスは要りません: 参照はテーブルのスロットひとつなので、インスタンスごとにひとつでも
安上がりです。

### シーン

ソースルートの下のシーンはシーンエントリ（`Scenes/Arena`）になり、依存物とともにディレクトリにビルドされます。シーンは参照
カウントされません: 寿命はシーンインスタンスそのものです。ContentLink は名前を解決し、（誰がロードしたかを問わず）開いている
コンテンツシーンをすべて追跡してシーンが開いている間はディレクトリを登録解除できないようにし、スコープにシーンを所有させます。

```csharp
// 所有: スコープが終わるとアンロード（additive のみ）
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
scope.ReleaseScene(arena);                                 // 任意: 早めにアンロード

// シリアライズ · 所有者必須 — ContentLink<T> と同じ規則
public ContentSceneLink level;
var scene = await level.LoadAsync(this);
await level.UnloadAsync();                                 // 任意 · いつ呼んでも安全

// 非所有 · どのモードでも（Single はほかのシーンをすべて閉じる）。SceneManager.UnloadSceneAsync でアンロード。
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

- Single モードのシーンは所有できません（所有者も閉じてしまうので）。Single ロードは呼び出し側も破棄します: MonoBehaviour から
  await するときは `destroyCancellationToken` を渡してください。
- 所有スコープが終わるときそのシーンが最後に残ったシーンなら、シーンは残り、警告が記録されます。アプリ終了時には何もアンロード
  しません。
- キャンセルは待機を止めます。additive シーンはエンジンのロード完了後にアンロードし直されます。
- スコープは自分のロードが作ったインスタンスだけを所有します: `SceneManager` で自分が開いたシーンは、スコープが同じシーンを
  ロードしてもあなたのもののままで、失敗したスコープのロードがそれを取ることはありません。
- `ContentService.LoadedSceneCount` / `SnapshotScenes(list)` が開いているものを示します。

### 生ハンドル

寿命を自分で管理するコード（プール、ストリーミングシステム）向け:

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");   // 同期は Acquire<T>
h.Asset; h.IsValid; h.Status; h.Key;
var h2 = h.Share();          // 二つ目の参照
h.Dispose();                 // 返す — あるいはスコープに渡す:
scope.Attach(h2);            // これでスコープが所有

if (ContentService.TryGetLoadableObjectId("Icons/sword", out var id))   // id ベースのロード用のエンジン id
    h = await ContentService.AcquireAsync<Texture2D>(id);
```

`ContentService.Contains(name)` / `ContainsScene(name)` は、登録済みディレクトリがその名前を持つかを教えます。同じ呼び出しは `ContentRef<T>` / `ContentSceneRef` も受け取り、`Acquire` / `AcquireAsync` も `ContentRef<T>` を受け取ります。

## Release はいつ呼ぶか

| 状況 | すること |
|---|---|
| コンポーネント · ポップアップ · ステージ · シーンコンテンツ | 何もしない。合うスコープにロードする。 |
| アセットの多いステージ/画面 | 明示的スコープひとつ、ステージが終わったら `Dispose()`。 |
| セルを再利用するスクロールリスト | セルごとに子スコープ、セルを再バインドするとき Dispose。 |
| 装備/スキンの交換 | 新しいものをロードする前に `Release(key)` または `ContentLink.Release()`。 |
| 別のシステムにアセットを渡す | `ShareTo(target, key)` または `target.Adopt(asset)`。スコープ間で生のアセットを渡さないこと。 |

## 解放はどう起きるか

ロードしたアセットがエンジンに戻るまでは三段階です: **スコープが自分の終わりに気づき** → **保持していた参照をすべて手放し** →
**カウントが 0 になったアセットをフレームの終わりに一度で返します。**

### 1. スコープはどうやって自分の終わりを知るか

| スコープ | 合図 | 内側で起きること |
|---|---|---|
| **GameObject** | そのオブジェクトの `OnDestroy` | 最初の `gameObject.Scope()` 呼び出しで隠しコンポーネント `ContentScopeOwner` が付きます。その `OnDestroy` がスコープを Dispose します。一度もアクティブになっていないオブジェクトは `OnDestroy` を受けないので、そのスコープはシーンのアンロード時に **シーンスコープ** が子として回収します。 |
| **Scene** | `SceneManager.sceneUnloaded` | `ContentScope.ForScene(scene)` がシーンハンドルをキーにスコープを作ります。`sceneUnloaded` でそのスコープを Dispose します。子の GameObject スコープのうち **別のシーンへ移動した** オブジェクトのもの（`MoveGameObjectToScene` · `DontDestroyOnLoad` — 所有オブジェクトの `scene` がもうこのシーンでない）は解放せず、今いるシーンの下に付け替えます。 |
| **Root** | `Application.quitting` | `ContentService.Shutdown()` が Root を Dispose し、ツリー全体がカスケードします。Root を自分で `Dispose()` してもエラーログが残るだけです。 |
| **Explicit** | 自分の `Dispose()` | 呼ばなければ親の終わりがカスケードして終わります — その親がシーンか Root なら、パスと作成時のスタック付きの **リーク警告** が記録されます。明示的な親や破棄された所有 GameObject を通じて終わるのは正常な寿命で、警告しません。 |

複製（`Object.Instantiate`）は `ContentScopeOwner` がシリアライズしたキー一覧を引き継ぎ、`Awake` で **自分の参照** を取ります
（元がまだ保持していれば共有、すでに消えていれば再ロード）。非アクティブで作られた複製は `Awake` が遅れ、最初にアクティブになるとき
取ります。

### 2. スコープが終わるときにすること — 順に

1. `IsDisposed = true`、`DisposedToken` がキャンセル — そのスコープで進行中だったロードはキャンセルされます。
2. 子スコープを逆順に終わらせます（上の引き渡し規則付き）。
3. 名前で保持した参照（`LoadAsync`/`Load`）と `Attach` したハンドルをすべて手放します → 各アセットのカウントが下がります。
4. スコープが所有するシーンをアンロードします（アプリ終了時は除く — エンジンが自分で片付けます）。
5. シーンか Root が回収した明示的スコープは警告を記録します。

### 3. カウントが 0 になった後

アセットごとにスロットがひとつあり（同じ名前はどのスコープからでも同じスロット）、スロットがカウントを持ちます。0 になっても
スロットは **その場では解放されず**、フレーム番号とともに待ち行列に入ります。実際の解放は `ContentService.Collect()` が行い、
パッケージがプレイヤーループの `PostLateUpdate` の末尾に挿し込むので **フレームごとに一度**、あなたのコードがすべて走った後です。

待ち行列の各スロットについて `Collect()` は次のいずれかをします:

- その間に誰かが再取得してカウントが 0 でなくなった → 行列から外すだけ（**同一フレーム内の解放 → 再取得は再ロードしません**）;
- まだロード中 → ロードが終わるまで触れない;
- 0 になってから `ReleaseDelayFrames` フレーム経っていない → 次のフレームでもう一度見る;
- それ以外 → エンジンの `Loadable.Release()` を呼び、スロットを空にする。スロットの世代番号が進むので、遅れて完了したロードが
  空のスロットに触れることはありません。

`ContentService.AutoCollect = false` ならプレイヤーループのフックは何もせず、`Collect()` を自分で呼びます。

### ディレクトリと終了

`ContentService.UnregisterDirectory(dir)` は、そのディレクトリのアセットがまだ保持されているか、そのディレクトリのシーンが
開いている間は拒否します — エンジンがすべての `Loadable` の解放とすべてのシーンのアンロードを先に要求するからです。`force: true` は
残ったアセット参照を無効化し（開いているシーンは決して対象外）、それらを保持していたスコープはその後、生きている参照だけを見ます —
`Count`、`DumpTree`、`Release(key)` は死んだ参照を黙って捨てます。パックディレクトリは登録解除時にアーカイブをアンマウントします。
`ContentService.Shutdown()`（`Application.quitting` でも実行）はスコープツリーを Dispose し、残った生ハンドルを報告し、すべてを
登録解除します。

## エディターシミュレーション

エディターではプロジェクトがコンテンツディレクトリの代わりを務めます。Play Mode で `RegisterDirectory`（または
`RegisterPackedDirectoryAsync`）に渡したパスがどれかの **プロファイルの出力フォルダ** なら、エンジンには何も登録されず、名前は
プロファイルのソースルートをいまディスクにあるとおりに走査して（カテゴリで絞って）答え、アセットはプロジェクトからロードされます。
テクスチャを編集して Play を押せば編集が見え、ファイルを消せばロードは失敗し、追加すればロードされます。リポジトリをクローンして
すぐ Play を押しても構いません — ビルドは要りません。

```
Window > ContentLink > Editor Simulation     トグル（既定でオン · プロジェクトごと）
```

Addressables の *Use Asset Database* プレイモードに相当します: 制作上の便宜であって、ビルドの代替ではありません。ビルドが
同じように振る舞うか確かめるにはオフにしてください — するとエディターもプレイヤーとまったく同じように、ビルド済みディレクトリを
本当に登録します。

- **何も書きません。** 索引（名前 → パス）はメモリ上にあり、ディレクトリの登録時か Play の開始時に作られ、Play 終了時に捨てられます。
- **プロファイルの出力フォルダだけ** をこのように肩代わりします。それ以外のパス（ダウンロードした複製 · 即席のビルド）は本当に
  登録され、そのようなディレクトリのルートの下では、まだどのカタログも知らない名前だけをプロジェクトから解決します（元の
  シミュレーション）。両方にある名前は本物のカタログが答えます。
- **登録は依然として必要です。** 忘れた `RegisterDirectory` はプレイヤーと同じくエディターでも失敗し、同じフォルダの二重登録は
  拒否され、ソースルートが存在しないプロファイルは例外を投げます。
- **トグルはディレクトリの登録時に読みます。** Play 中にオフにしても、すでにプロジェクトが肩代わりしていたディレクトリは次の Play
  までそのまま動き、本物のディレクトリの下の新しい名前だけが止まります。Play 終了時の要約とリセットは、その瞬間のトグルの状態に
  かかわらず走ります。
- **解放されたアセットはアンロードされません。** 参照カウントはそのままですが、エディターがオブジェクトを生かしておくので、制作中の
  メモリの読みはビルドより高くなります。アンロードの挙動はトグルをオフにして確かめてください。プロジェクトが肩代わりしたアセットを
  保持していても、ディレクトリの登録解除は妨げられません。
- **Play 中の編集はその場で見えます**（再インポートがロード済みオブジェクトを更新します）。削除したアセットは、それを保持している
  ハンドルの下でオブジェクトが破棄され、新しいロードはエラーとともに失敗します。
- **アーカイブは動きません。** `Packed` と `Engine` の出力もほかと同じくプロジェクトが肩代わりするので、マウントとアーカイブの挙動は
  トグルをオフにして確かめます。
- **シミュレーションされたシーン** は `EditorSceneManager.LoadSceneAsyncInPlayMode` で開きます。要求したスコープが依然として
  アンロードしますが、どのディレクトリにも属しません。
- **重複する名前は拒否されます** — ビルダーが拒否するのと同じように。エディターでだけ動く名前はありません。

そのように肩代わりした登録ごとに、どのプロファイルとルートが答えるかの一行が記録されます。Play Mode を出るとき、シミュレーションが
解決した名前 — ロードしたもの、または `RuntimeKeyIsValid` / `Contains` で確認しただけのもの — のうち、そのプロファイルの
カタログがまだ知らないものを一行で残します — 出荷前にビルドが必要なリンクです。`ContentService.SnapshotSimulated(list)` はプレイ中に
プロジェクトが肩代わりした名前をすべて報告します。`ContentService.TryGetLoadableObjectId(name, out id)` もプロジェクトが
肩代わりする名前に答えます。

## 診断

- *Window > ContentLink > Dump Scope Tree*（Play Mode）、または `ContentScope.DumpTree()`:

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```
  子はスコープが保持している順に並び、それは作成順ではありません: スコープを取り除くと最後の兄弟がその位置に入るので、ツリーの
  大きさによらず取り除くコストは同じです。

- `ContentService.Snapshot(list)` — 生きているすべての参照（`CaptureStackTrace` がオンならスタックトレース付き）
- `ContentService.SnapshotScenes(list)` / `LoadedSceneCount` — 開いているコンテンツシーン
- `ContentService.SnapshotSimulated(list)` — エディターシミュレーションが解決した名前
- `ContentService.LiveAssetCount` — 現在保持しているアセット数
- ビルドウィンドウはエディターセッションの最後のビルド（プロファイル · ターゲット · ファイル数 · 時間 · エラー）を表示し、ログには
  ビルドごとに `[ContentLink] built …` の一行が残ります

## AssetLink · Addressables からの移行

| 以前 | ContentLink |
|---|---|
| `AssetLink.LoadAssetAsync()` + `ReleaseAsset()` | `ContentLink.LoadAsync(owner)` · `Release()` は任意 |
| `AssetLinkSpawner.SpawnAsync()` + `DoAutoRelease` | `scope.InstantiateAsync()` |
| `AddressablesTracker.ReportUnreferencedHandles()`（GC ベース） | シーンアンロード/終了時のスコープ警告（決定論的） |
| Auto Regist Folder Pattern | ビルドプロファイルとカテゴリ |
| `AssetRef`（GUID） | `ContentRef<T>` / `ContentSceneRef` |
| `AssetReference` / `AssetReferenceSprite` フィールド | `ContentRef<T>` フィールド — 型を変えるだけでデータが残る（下記） |
| `ref.LoadAssetAsync()` + `ref.ReleaseAsset()` | `this.Scope().LoadAsync(ref)` · `this.Scope().Release(ref)` は任意 |
| `ref.RuntimeKeyIsValid()`（「値があるか」） | `ref.IsSet` |
| *Use Asset Database* プレイモード | エディターシミュレーション |
| Addressables のグループ / バンドル | プロファイル / カテゴリ · `Packed` アーカイブ |

**`AssetReference` フィールドから。** フィールドの型を `ContentRef<T>` に変えると、シリアライズされた GUID とスプライト名が
残ります。アセットはコンテンツルートの下に置く必要があります（移しても GUID は保たれる）。Addressables は三つの形で記録し、
そのまま解決されるのはひとつです:

| Addressables で選んだもの | 保存された値 | ContentLink では |
|---|---|---|
| スプライト | テクスチャの GUID + スプライト名 | 解決される |
| `AssetReferenceSprite` に落としたテクスチャ | テクスチャの GUID、スプライト名なし | スプライトではなくテクスチャ: フィールドは赤 — スプライトを選び直す |
| Addressable スプライトアトラスが持つスプライト | アトラスの GUID + スプライト名 | 非対応（アトラス経由のスプライトはカタログにない）: 黄 — テクスチャからスプライトを選ぶ |

## 注意と制限

- メインスレッド専用。キャンセルトークンもメインスレッドから。
- エンジンのロードはキャンセルできません。キャンセルされた要求は参照をただちに返し、ロードは完了後に次の `Collect()` で解放されます。
- エンジンが例外を投げたロードは、同期・非同期どちらの経路でもほかの失敗と同じように報告されます — 無効なハンドルとログ — 何も
  保持されたままにはなりません。
- プロジェクトが肩代わりした Play Mode のコンテンツ（エディターシミュレーション）は Unity が参照カウントしません。解放の挙動は
  シミュレーションをオフにし、ビルドして登録したコンテンツディレクトリで確かめてください。
- 非アクティブで作られた複製は、最初にアクティブになるときに参照を取ります。
- シーンは参照カウントされません。同じシーンを additive で二度ロードするとインスタンスが二つでき、それぞれ追跡・アンロードされます。
  シーンには `ShareTo` / `Adopt` がありません。
- コンテンツディレクトリはひとつのビルドターゲット向けで、別のターゲットではロードできないことがあります。
- `Packed` のアーカイブサイズは圧縮後のファイルではなく入力バイトの上限です。ハッシュ接頭辞トライは単純詰めより約 1.5 倍の
  アーカイブを作り、上限近くを行き来するアーカイブはビルドごとに分割・結合されることがあり、`MaxArchiveMB` や分割実装を変えると
  出力は再分割されます。
- ビルドにはプロジェクトロックが必要です: コマンドラインビルドの前に、ほかのエディターインスタンスでプロジェクトを閉じてください。
- 参照と名前はアセットのスコープキーを共有します: どちらで解放しても両方が外れます。二つのフィールドを別々に解放するなら
  `ContentLink` か子スコープを使ってください。
- リンクの `Name` はメイン名で、スプライトリンクのスプライト名は `SubObjectName` です。`SetName(name)` はスプライト名を
  空にするので、リンクは `SetName(a.Name, a.SubObjectName)` でコピーしてください — それを失った `ContentLink<Object>` は
  エラーなしでテクスチャをロードします。
- サブアセットはスプライトだけを名前で参照します。ほかのサブアセット（モデルファイル内のメッシュ）とスプライトアトラスを
  経由するスプライトは参照できません。
- 同じファイルが二つの名前でビルドされた場合 — ベースディレクトリと後のパッチの間で移動 — 参照は最後に登録された
  ディレクトリの名前に解決され、古い名前のリンクは別のコピーをロードします: メモリにコピーが二つあります。

## リポジトリ構成とテスト

| パス | 何か |
|---|---|
| `com.xpTURN.ContentLink/` | パッケージ（`Runtime`、`Editor`、`Tests~/Verify`、`README`、`CHANGELOG`） |
| `samples/LinkSample/` | パッケージを `file:` で使う Unity 6.6 プロジェクト — `Assets/Content` の下にテクスチャ · スプライトアトラス · プレハブ · シーン · オーディオ |
| `tests/LinkTests/` | Unity Test Runner プロジェクト: EditMode（ビルダー · 設定 · パッカー · ウィンドウモデル）と PlayMode（寿命 · シーン · パック登録 · アロケーション）のスイート |
| `tools/run-all-tests.sh` | 全部を実行: dotnet ハーネス → EditMode → PlayMode（`--player` でスタンドアロンプレイヤーの実行を追加） |
| `tools/build-content.sh` | コマンドラインのコンテンツビルド |
| `docs/IMP/` | 設計・実装の記録（韓国語） |
| `.github/` | CI（`workflows/check.yml`）: push と pull request ごとに dotnet ハーネス · `.meta` サイドカー検査 · バージョンマニフェスト検査。Unity のスイートはライセンス済みエディターが必要なのでローカルに留める |

`Tests~/Verify` は、文書化された Unity 6.6 API のスタブと Loadable の参照規則を強制する偽エンジンに対してパッケージをコンパイルし、
寿命・ビルド・パッキングのシナリオを実行します（.NET 8 SDK で `dotnet run`）。Unity のスイートはテスト自身がビルドしたコンテンツ
ディレクトリに対して、エディターとスタンドアロンプレイヤーで走ります。Unity 6000.6.0f1 で検証済みです。

```
tools/run-all-tests.sh                 # ハーネス + EditMode + PlayMode
tools/run-all-tests.sh --dotnet-only   # ハーネスのみ（数秒）
tools/run-all-tests.sh --player        # StandaloneOSX プレイヤーでの PlayMode も
```

## ライセンス

Apache License 2.0
