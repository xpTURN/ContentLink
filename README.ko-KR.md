# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | **한국어** | [日本語](README.ja-JP.md) | [简体中文](README.zh-CN.md)

ContentLink 는 Unity 6.6 에 내장된 **콘텐츠 디렉터리**에서 스프라이트 · 프리팹 · 씬 · 오디오 같은 게임 콘텐츠를
**이름으로** 로드하고, 모든 로딩 시스템이 사용자에게 떠넘기는 한 가지 일 — 로드한 에셋을 언제 놓아도 되는가 —
를 대신 맡습니다.

**문제.** `Resources`, AssetBundle, Addressables 모두 로드마다 해제를 손으로, 모든 스크립트에서 짝지어야 합니다.
하나를 빠뜨리면 앱이 끝날 때까지 메모리가 늘고, 너무 일찍 놓으면 텍스처가 사라집니다. 범인을 찾는 도구는 일이
벌어진 뒤에 프로파일러로 짐작할 뿐입니다.

**ContentLink 가 하는 것.** 모든 로드는 **스코프**에 속합니다 — 요청한 GameObject, 그 씬, 앱, 또는 스테이지나
팝업용으로 직접 만든 것. 스코프가 끝나면(오브젝트 파괴 · 씬 언로드 · 앱 종료 · 직접 Dispose) 들고 있던 것이 전부
해제됩니다. 로드하면 끝입니다: 잊은 해제는 최대 메모리를 조금 높일 뿐, 올바름을 깨뜨리지 않습니다. 같은 원리가
직렬화 참조(`ContentLinkSprite icon;` + 인스펙터 드로어), 인스턴스화한 프리팹, 콘텐츠 씬에도 적용됩니다.

**그 밖에 들어 있는 것.** 콘텐츠 디렉터리 자체를 만드는 빌드 파이프라인 — `ProjectSettings` 에 저장되는 프로파일,
빌드 창, 다운로드 파이프라인을 위한 결정론적 크기 분할 아카이브, 후처리 훅, 명령줄 — 그리고 Play Mode 가 프로젝트에서
바로 로드해 작업 중엔 아무것도 굽지 않아도 되는 에디터 모드. 의존성은 Unity 6000.6 뿐입니다.

> **로드하고, 해제는 잊으세요.** 에셋은 소유자 — GameObject, 씬, 앱 — 가 사는 만큼만 살고, `Release()` 는
> 언제나 최적화일 뿐 의무가 아닙니다.

---

## 목차

1. [무엇을 얻는가](#무엇을-얻는가)
2. [요구사항과 설치](#요구사항과-설치)
3. [다섯 가지 개념](#다섯-가지-개념)
4. [빠른 시작](#빠른-시작)
5. [이름](#이름)
6. [콘텐츠 빌드](#콘텐츠-빌드)
7. [콘텐츠 로딩](#콘텐츠-로딩)
8. [Release 는 언제 부르나](#release-는-언제-부르나)
9. [해제는 어떻게 일어나나](#해제는-어떻게-일어나나)
10. [에디터 시뮬레이션](#에디터-시뮬레이션)
11. [진단](#진단)
12. [AssetLink · Addressables 에서 옮겨오기](#assetlink--addressables-에서-옮겨오기)
13. [주의와 제한](#주의와-제한)
14. [저장소 구조와 테스트](#저장소-구조와-테스트)
15. [라이선스](#라이선스)

---

## 무엇을 얻는가

| 영역 | 기능 |
|---|---|
| 로딩 | `scope.LoadAsync<T>("Icons/sword")` — 에셋은 스코프의 것, 해제할 것이 없다 |
| 스코프 | 스스로 끝나는 Root / Scene / GameObject / Explicit 스코프 · 스코프당 중복 제거 · 경로가 찍히는 누수 경고 |
| 직렬화 참조 | `ContentLink<T>` · `ContentSceneLink`(이름) 와 `ContentRef<T>` · `ContentSceneRef`(GUID — 파일을 옮기거나 이름을 바꿔도 유지) 필드 — 인스펙터 드로어가 채우고 검증 |
| 인스턴스 | `scope.InstantiateAsync("Prefabs/Cat")` — 프리팹 참조는 인스턴스와 그 복제본이 가진다 |
| 씬 | 스코프나 링크가 소유하는 콘텐츠 씬 · 어떤 모드로든 비소유 로드 · 열린 씬 추적 |
| 결정적 해제 | 엔진 해제는 프레임당 한 지점(`Collect()`)에서만 — 코드 중간에서 일어나지 않는다 |
| 빌드 프로파일 | 소스 루트 · 출력 · 디렉터리당 여러 카테고리(카탈로그) · 아카이브 모드 · 압축 · 엔진 옵션 — `ProjectSettings` 에 저장 |
| 빌드 창 | *Window > ContentLink > Build Window*: Undo 되는 프로파일 편집 · 검증 메시지 · 하나 또는 전부 빌드 |
| 패킹 아카이브 | 빌드를 약 `MaxArchiveMB` 크기의 아카이브들로 결정론적으로 분할 · 런타임이 다시 한 디렉터리로 마운트 |
| 후처리 | `IContentBuildPostProcess` 단계가 빌드 뒤 적힌 순서대로 돈다 |
| 명령줄 | `-executeMethod` 로 `ContentBuildCLI.Build` · 덮어쓰기 · `profile all` · 종료 코드 · `tools/build-content.sh` |
| 에디터 시뮬레이션 | Play Mode 에서 프로파일의 디렉터리를 프로젝트가 대신한다: 추가·수정·삭제가 그대로 보이고 빌드가 필요 없다 — 아무것도 쓰지 않는다 |
| 진단 | 스코프 트리 덤프 · 참조 스냅샷 · 마지막 빌드 요약 |

## 요구사항과 설치

- Unity **6000.6** 이상(`Unity.Loading` 콘텐츠 디렉터리 API).
- 다른 의존성 없음.

**git 에서** — *Window > Package Manager > + > Add package from git URL*:

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

**로컬 체크아웃에서** — `Packages/manifest.json`:

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

이 저장소의 `samples/LinkSample` 이 정확히 그렇게 연결된 Unity 프로젝트입니다.

## 다섯 가지 개념

| 개념 | 무엇인가 |
|---|---|
| **콘텐츠 디렉터리** | 엔진이 에셋에서 만들어 내는 폴더(`BuildPipeline.BuildContentDirectory`). 런타임에 등록하고 거기서 로드합니다. 하나의 빌드 타깃을 위해 만들어집니다. |
| **카탈로그** | 빌드가 디렉터리에 넣는 `ContentCatalog` 에셋: 이름과 그것이 가리키는 엔진 id 의 목록. 한 디렉터리가 여러 카탈로그(*카테고리*마다 하나)를 가질 수 있습니다. |
| **이름** | 로드할 때 쓰는 것: 소스 루트 기준 에셋 경로에서 확장자를 뺀 것 — `Icons/sword`. 서브에셋은 `Icons/sword[sword]`, 씬은 `Scenes/Arena`. |
| **스코프** | 로드한 모든 것의 소유자. 스코프는 트리(Root → Scene → GameObject → Explicit)를 이루고 스스로 끝납니다. |
| **링크** | 이름을 담는 직렬화 필드(`ContentLinkSprite icon;`). 인스펙터에서 정하고, 소유자를 넘겨 로드합니다. |

## 빠른 시작

**1. 소스 루트 아래에 에셋을 둡니다** — 기본은 `Assets/Content`:

```
Assets/Content/Icons/sword.png        →  "Icons/sword"            (Texture2D)
                                         "Icons/sword[sword]"     (그 안의 Sprite 서브에셋)
Assets/Content/Prefabs/Cat.prefab     →  "Prefabs/Cat"
Assets/Content/Scenes/Arena.unity     →  "Scenes/Arena"
```

**2. 빌드합니다** — *Window > ContentLink > Build Window*. 처음 열면 `Main` 프로파일이 보입니다:
소스 루트 `Assets/Content`, 루트 전체를 담는 카테고리 하나, 출력 `Assets/StreamingAssets/MainContent`,
아카이브 `None`(평면 파일). **Build 'Main'** 을 누르세요. (*Window > ContentLink > Build Content
Directory* 는 창을 열지 않고 활성 프로파일을 굽습니다.)

`StreamingAssets` 는 플레이어 안에 실리므로 아래 코드가 그 폴더를 찾습니다. 다운로드 파이프라인이라면
`Archive` 를 `Packed` 로, 출력은 `Assets/` 밖으로 두고 아카이브를 직접 배포하세요 — [콘텐츠 빌드](#콘텐츠-빌드) 참고.

**3. 시작할 때 디렉터리를 한 번 등록합니다** — 무엇이든 로드하기 전에:

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    async void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");

        // Archive = None(평면 파일 · 이주된 Main 프로파일) 또는 Engine(content0.archive 하나):
        ContentService.RegisterDirectory(path);

        // Archive = Packed(새 프로파일의 기본): 아카이브 몇 개 + archives.json
        // await ContentService.RegisterPackedDirectoryAsync(path);
    }
}
```

**4. 스코프에 로드합니다.** 해제할 것이 없습니다: 스프라이트는 GameObject 와 함께 사라집니다.

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

**5. 또는 직렬화 링크를 쓰고** 인스펙터에서 에셋을 고릅니다:

```csharp
public ContentLinkSprite icon;                       // Assets/Content/Icons/sword.png 를 떨어뜨리면 → "Icons/sword" + 스프라이트 "sword"
async void Start() => image.sprite = await icon.LoadAsync(this);   // this = 이 GameObject 의 스코프(4단계와 같다). 다른 스코프를 넘겨도 된다
```

에디터에서는 2단계를 통째로 건너뛸 수 있습니다: [에디터 시뮬레이션](#에디터-시뮬레이션)(기본 켜짐)이 켜져 있으면
프로파일의 출력 폴더를 등록하는 순간 Play Mode 는 그 디렉터리를 **프로젝트에서** 냅니다 — 추가·수정·삭제한 에셋이
그대로 보이고, 폴더가 아예 없어도 됩니다. 빌드는 출하할 때.

## 이름

- 이름은 **프로파일의 소스 루트 기준** 에셋 경로에서 확장자를 뺀 것입니다: `Icons/sword`.
  카테고리는 이름을 바꾸지 않습니다 — `Icons/sword` 는 어느 카탈로그에 들어 있든 `Icons/sword` 입니다.
- **서브에셋**은 `<메인>[<서브>]`: `sword.png` 안의 스프라이트 `sword` 는 `Icons/sword[sword]`, 스프라이트 시트
  `sheet.png` 는 `Icons/sheet[run_01]`, `Icons/sheet[run_02]`, …
- **링크**는 스프라이트 이름을 따로 둡니다: `("Icons/sheet", "run_01")` 은 `Icons/sheet[run_01]` 을 로드합니다. 스프라이트
  이름 없이 전체 이름을 적어도 같은 키로 로드됩니다.
- **씬**도 에셋처럼 이름 붙습니다: `Scenes/Arena`. 에셋과 씬은 이름 공간을 공유합니다.
- 이름은 **프로파일 안에서 고유**합니다. 같은 이름을 받게 될 두 에셋(카테고리가 겹치거나, 두 폴더에 같은 이름)은
  콘솔에 두 경로를 찍으며 빌드를 실패시킵니다.
- 인스펙터 드로어는 이름의 상태를 색으로 보여 줍니다: 빈 링크는 무채색, 아직 어느 카탈로그도 모르는 이름은
  **호박색**(빌드하거나 *Update Content Catalog* 실행), 필드가 담을 수 없는 것으로 풀리는 이름은 **빨강**. 링크의 이름 줄은
  스프라이트를 `Icons/sheet[run_01]` 로 보여 주고, 거기에 친 이름은 카탈로그에 있으면 두 필드로 나뉘어 저장됩니다.

## 콘텐츠 빌드

### 프로파일

**프로파일**은 콘텐츠 디렉터리 하나를 기술합니다. `ProjectSettings/ContentLinkBuild.asset`(`ContentBuildSettings`)에
저장되고 빌드 창에서 편집합니다. 프로파일 하나가 **활성**입니다: 메뉴 항목과 옛 정적 API 는 그것에 작용합니다.

| 필드 | 기본값(새 프로파일) | 뜻 |
|---|---|---|
| `Name` | — | 고유. 활성 프로파일과 명령줄이 이 이름으로 고른다 |
| `SourceRoot` | `Assets/Content` | 이름의 기준이 되는 폴더 |
| `OutputPath` | `Build/Content/<Name>` | 디렉터리가 쓰이는 곳. 기본은 `Assets/` 밖 — 아래 참고 |
| `Catalogs` | 루트 전체 카테고리 하나 | 카테고리 = 루트의 하위 폴더 + 그것이 채우는 카탈로그 에셋 |
| `Archive` | `Packed` | `None`(평면) · `Engine`(엔진의 단일 아카이브) · `Packed`(크기 분할 아카이브) |
| `MaxArchiveMB` | `20` | `Packed`: 아카이브를 닫는 크기 — 파일 하나 전후 |
| `Compression` | `LZ4` | `Uncompressed` · `LZ4` · `LZMA` — 아카이브에만 적용. 평면 빌드는 항상 비압축 |
| `CleanBuildCache` | 꺼짐 | 엔진 옵션: 빌드 캐시 무시 |
| `DisableWriteTypeTree` | 꺼짐 | 더 작은 출력 — **정확히 같은 엔진 버전에서만** 로드된다(`SerializeUnityVersion` 은 자동으로 붙는다) |
| `FailBuildWhenErrorsLogged` | 켜짐 | 빌드 중 찍힌 오류 로그는 빌드 실패 |
| `Target` | `NoTarget` | `NoTarget` = 활성 빌드 타깃이면 무엇이든. 다른 값이면 다른 타깃에서의 빌드를 거부 |
| `PostProcessors` | 없음 | `IContentBuildPostProcess` 구현체의 형식 이름, 실행 순서대로 |

**카테고리.** 하위 폴더(`Icons`, `Prefabs`, `Levels/Forest`, 루트 전체는 `""`)를 추가하면 빌드가 그 폴더의
에셋을 별도 카탈로그에 넣습니다. 한 프로파일의 모든 카탈로그는 **하나의** 디렉터리로 들어가므로 런타임은 하나의
이름 공간을 봅니다. 겹치는 카테고리, 자기 루트 아래 놓인 카탈로그, 중복 이름은 무엇을 쓰기 전에 거부됩니다.
경로 문자(`/ \ : * ? " < > |`)가 든 프로파일 이름(이름은 아카이브 이름과 기본 경로에 들어갑니다), 끝에 구분자가 붙은
소스 루트, 소스 루트 안에 있거나 루트를 품는 출력 폴더도 마찬가지입니다. 새 카테고리의 카탈로그 에셋은 첫 빌드 때
`Assets/ContentCatalogs/<프로파일>.<카테고리>.asset` 에 만들어지며(폴더가 없으면 폴더도), 기존 카탈로그를 필드에
지정할 수도 있습니다.

**출력 폴더.** 폴더는 무엇을 쓰기 전에 검사합니다: 비어 있거나 이전 콘텐츠 빌드(`BuildManifestHash.txt` 가 있는)
폴더만 받아들이고, 다른 것이 든 폴더는 손대지 않고 빌드가 실패합니다. 비우는 것은 엔진이 돌기 직전이라, 그보다 앞
단계(검증 · 카탈로그)에서 실패한 빌드는 이전 산출물을 그대로 남깁니다. `Assets/` 안의 출력(예: `StreamingAssets`)은 Unity 가
임포트하고 빌드 뒤 자동 리프레시됩니다. `Assets/` 밖의 출력은 그렇지 않으며, 따로 배포할 콘텐츠라면 그쪽이 맞습니다.

**빌드 타깃.** 콘텐츠 디렉터리는 에디터의 **활성 빌드 타깃**용으로 만들어지고 다른 타깃에서는 로드되지 않을 수
있습니다. 창, 로그, 마지막 빌드 요약이 항상 어느 타깃용인지 말합니다.

**정적 빌더에서 옮겨오기.** 스크립트로 `ContentDirectoryBuilder.SourceRoot` / `CatalogPath` / `OutputPath` 를 세팅하던
프로젝트는 그대로 동작합니다: 설정이 없는 프로젝트는 그 기본값으로 만든 `Main` 프로파일(`Assets/Content` →
`Assets/StreamingAssets/MainContent`, 평면, 비압축)을 받고, 정적 속성은 활성 프로파일의 뷰로 남습니다. 세터로 값을
넣으면 에디터 세션 동안만 프로파일을 덮어쓰며, 창이 그 사실을 알려 줍니다.

### 아카이브 모드와 등록

| `Archive` | 쓰이는 것 | 등록 |
|---|---|---|
| `None` | 엔진의 평면 파일(`*.cf`, `*.resS`, 매니페스트) | `ContentService.RegisterDirectory(path)` |
| `Engine` | `content0.archive` 하나(엔진의 `UseArchive`) | `ContentService.RegisterDirectory(path)` |
| `Packed` | `<프로파일>.manifest-<해시>.archive`, `<프로파일>.<비트열>-<해시>.archive`, … + `archives.json` | `await ContentService.RegisterPackedDirectoryAsync(path)` |

`Packed` 는 배포용입니다: 단일 아카이브는 다운로드 단위로 나쁘고 엔진에는 크기 옵션이 없어서, 패키지가 평면 빌드를
직접 패킹합니다. 기본 규칙은 **해시 접두 트리**입니다: 매니페스트와 해시 파일은 작은 전용 아카이브에, 상한보다 큰
파일은 혼자 한 아카이브에, 나머지는 키의 MD5 비트로 버킷을 나누고 상한에 안 들어가는 버킷만 다음 비트로 쪼갭니다.
키는 직렬화 파일이면 매니페스트 ID(엔진이 에셋의 정체성에서 만들고 내용이 바뀌어도 유지), `.resS`/`.resource` 면 파일
이름입니다. 그래서 에셋을 고치면 그 아카이브 안에서 바뀌고, 늘거나 빠지면 아카이브 하나(버킷이 쪼개지거나 합쳐지면
이웃 하나 더)만 바뀌며, 나머지 아카이브는 이름도 바이트도 그대로입니다 — 패치 파이프라인이 원하는 성질입니다.
(`.resS` 는 이름이 키라 텍스처를 고치면 픽셀 데이터는 한 아카이브에서 다른 아카이브로 옮겨 갑니다.) 아카이브 이름은 비트
경로 + 파일 목록 해시(`Main.0110-a3f9c2e1.archive`)라 같은 이름이 다른 내용을 뜻하는 일이 없습니다. **같은 입력은
언제나 바이트까지 같게 패킹**됩니다. 아카이브 수는 단순 채우기보다 약 1.5배가 됩니다(리프는 상한의 0.6배 근처) —
상한 근처를 원하면 `MaxArchiveMB` 를 올리세요. 상한은 *입력* 바이트 기준입니다 — LZ4 면 디스크의 아카이브는 그
절반 정도로 나옵니다. `archives.json` 은 아카이브마다 크기 · 입력 크기 · CRC · 안의 파일 목록을 적습니다: 패처는
이것을 먼저 받고, 없는 이름을 내려받고, 사라진 이름을 지웁니다. CRC 는 엔진이 아카이브 내용에 대해 돌려주는 값이지
디스크 파일의 CRC32 가 아니므로(실측), 내려받은 파일의 검증은 크기나 자체 해시로 하세요.

**분류 규칙 커스터마이징.** Editor 어셈블리에서 `IContentArchivePartitioner` 를 구현하고 프로파일의 `Partitioner`
에 고르세요(명령줄은 `-contentlink.partitioner <타입 이름>`, `trie`, `sequential`). 구현체는 파일과 크기 · 매니페스트
파일 · 파싱된 매니페스트 · 빌드된 로더블(카탈로그와 디스크 파일 포함)을 받아 아카이브 목록(이름 + 파일)을 돌려줍니다.
내장 `SequentialFillPartitioner` 는 예전의 정렬 채우기로, 국소성 대신 더 적고 꽉 찬 아카이브를 원할 때 씁니다.
패커는 어떤 답이든 검사하고(모든 파일이 정확히 한 번 · 빈 아카이브 없음 · 평범하고 유일한 이름) 파일 순서를 바꿔
한 번 더 불러 답이 다르면 빌드를 실패시킵니다. 내장 구현처럼 이름에 내용 해시를 넣어야 캐시된 다운로드가 낡지
않습니다. 구현체를 바꾸거나 `MaxArchiveMB` 를 바꾸면 다시 분할됩니다 — 한 번의 큰 다운로드입니다.

런타임의 `RegisterPackedDirectoryAsync` 는 모든 아카이브를 한 네임스페이스에 마운트하고 마운트 경로를 평면
디렉터리처럼 등록합니다. `UnregisterDirectory` 가 다시 언마운트합니다. 폴더에 `archives.json` 이 없거나, 색인이
아카이브를 하나도 적지 않았거나, 어느 아카이브의 디스크 크기가 색인과 다르면 오류 로그와 함께 `-1` 을 돌려주고
아무것도 마운트하지 않습니다. 아카이브 하나가 마운트되지 않으면 어느 것인지 말하는 예외를 던집니다.

다운로드로 기기에 온 콘텐츠는 등록하기 전에 검증할 가치가 있습니다. `archives.json` 은 아카이브마다 `Bytes` 와
`Crc` 를 적습니다: `RegisterPackedDirectoryAsync` 는 디스크 크기가 색인과 다른 아카이브를 마운트 전에 거부하지만,
길이가 같은 손상은 `Crc` 를 비교해야만 잡히고 그것은 호출자의 몫입니다 — 엔진은 손상된 콘텐츠 파일에서 실패하는 대신
블록하므로, 먼저 검증하세요.

### 후처리

```csharp
using xpTURN.ContentLink.Editor;

public sealed class UploadStep : IContentBuildPostProcess
{
    public void OnBuilt(ContentBuildResult r)
    {
        // r.ProfileName, r.Target, r.OutputPath, r.Files(정렬됨), r.Archives, r.Manifest, r.BuiltNames, r.Milliseconds
    }
}
```

구현체는 창에서 고르고(`TypeCache` 로 찾아 줍니다) 순서를 정합니다. 순서는 프로파일에 저장되고 그것이 실행
순서입니다. 찾을 수 없는 형식이나 예외를 던지는 단계는 **빌드를 실패**시키되, 살펴볼 수 있게 출력은 남깁니다.
빌드의 다른 단계가 던져도 같은 방식으로 보고됩니다: 결과가 사유를 담고, 창과 로그와 명령줄이 모두 그것을 보여 줍니다.
후처리는 패킹 다음에 돌므로 `r.Archives` 는 최종 상태입니다.

### 메뉴와 스크립팅

| 어디서 | 무엇 |
|---|---|
| *Window > ContentLink > Build Window* | 프로파일 편집 · 하나/전부 빌드 · 메시지와 마지막 결과 |
| *… > Build Content Directory* | 활성 프로파일 빌드 |
| *… > Update Content Catalog* | 빌드 없이 활성 프로파일의 카탈로그만 다시 쓴다(링크가 호박색에서 무채색으로) |
| `ContentBuildRunner.Build(profile)` / `BuildAll()` | 코드에서 같은 일 · `ContentBuildResult` 반환 |

### 명령줄

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget StandaloneOSX -logFile <log> \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.compression LZ4
```

`tools/build-content.sh` 가 이 호출을 감쌉니다(프로젝트 락을 먼저 검사 · 종료 코드 전달 · 로그의 `[ContentLink …]`
줄 출력):

```
tools/build-content.sh -p samples/LinkSample -contentlink.profile Main
tools/build-content.sh -p <project> -t Win64 -contentlink.profile all      # -t 는 -buildTarget 으로 (Win64, OSXUniversal, Linux64, Android, iOS, …)
```

| 키(`-contentlink.<key> <value>`) | 값 |
|---|---|
| `profile` | 프로파일 이름, 또는 `all`(대소문자 무관 · 전부 · 첫 실패에서 멈춤). 생략 = 활성 프로파일 |
| `source`, `output` | 경로 |
| `archive` | `None` · `Engine` · `Packed` |
| `maxArchiveMB` | 1 이상의 정수 |
| `compression` | `Uncompressed` · `LZ4` · `LZMA` |
| `cleanBuildCache`, `disableWriteTypeTree`, `failBuildWhenErrorsLogged` | `true` · `false` |
| `postProcessors` | 쉼표로 나눈 형식 이름 |

덮어쓰기는 지정한 프로파일을 **메모리에서만** 바꿉니다 — 명령줄은 설정 파일을 절대 쓰지 않습니다(`profile all` 은
덮어쓰기를 받지 않습니다). 빌드 실패, 모르는 키, 없는 프로파일은 종료 코드 **1**. 프로젝트가 다른 에디터
인스턴스에 열려 있으면 안 됩니다: 배치 모드는 프로젝트 락이 필요합니다.

**카탈로그만.** `-executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` 는 같은 인자를 받되
프로파일의 카탈로그만 다시 씁니다 — 빌드도, 출력 폴더도 없습니다 — *Update Content Catalog* 메뉴의 명령줄
짝입니다(`tools/build-content.sh -c -contentlink.profile Main`). CI 가 카탈로그 에셋만 최신으로 맞추면 될 때(플레이어
빌드 전 링크 검증), 또는 디렉터리는 다른 파이프라인이 구울 때 씁니다. 빌드는 이미 카탈로그를 매번 다시 쓰므로
빌드 쪽에 따로 켤 것은 없습니다. `profile all` 은 프로파일마다 갱신하고 첫 실패에서 멈춥니다. `source` 덮어쓰기는
카탈로그의 기준 루트가 됩니다. 종료 코드는 위와 같습니다.

## 콘텐츠 로딩

### 스코프

| 스코프 | 만드는 법 | 끝나는 때 | 부모 |
|---|---|---|---|
| Root | 자동(`ContentScope.Root`) | `Application.quitting` | — |
| Scene | `ContentScope.ForScene(scene)` | `SceneManager.sceneUnloaded` | Root |
| GameObject | `gameObject.Scope()` / `component.Scope()` | `OnDestroy` | 그 씬 |
| Explicit | `parent.CreateChild("Stage")` | 직접 `Dispose()`, 아니면 부모가 끝날 때 | 아무거나 |

**GameObject 스코프** — 가장 흔한 경우. 에셋은 오브젝트와 함께 사라집니다:

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        var scope = this.Scope();                                   // 이 GameObject 의 스코프(gameObject.Scope() 도 같다)
        image.sprite = await scope.LoadAsync<Sprite>("Icons/sword[sword]");
    }                                                               // OnDestroy 에서 해제 — 더 쓸 것이 없다
}
```

**Scene 스코프** — 씬이 사는 동안만 살아야 하는 에셋:

```csharp
var scope  = ContentScope.ForScene(gameObject.scene);               // 씬마다 하나 · 처음 쓸 때 만들어진다
var skybox = await scope.LoadAsync<Material>("Materials/Sky");      // 씬이 언로드되면 해제
```

**Explicit 스코프** — 스테이지, 화면, 팝업: 끝나는 때를 직접 정하고, 늦어도 부모가 끝낼 때 끝납니다:

```csharp
var stage = ContentScope.Root.CreateChild("Stage3");                // 또는 this.Scope().CreateChild("Popup")
await stage.LoadAsync<AudioClip>("Wav/descent");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... 스테이지 진행 ...
stage.Dispose();                                                    // 들고 있던 것 전부 반환
```

**Root 스코프** — 앱이 사는 동안:

```csharp
var atlas = await ContentScope.Root.LoadAsync<Texture2D>("UI/Atlas");   // Application.quitting 에서 해제
```

**모든 스코프에서:**

```csharp
var mat   = scope.Load<Material>("Materials/Steel");                // 동기(엔진 로드를 기다린다)
bool held = scope.Contains("Icons/sword");                          // true: 다시 로드해도 비용이 없다
scope.Release("Icons/sword");                                       // 선택: 일찍 돌려주기
var token = scope.DisposedToken;                                    // 스코프가 끝나면 취소 — 직접 기다리는 곳에 넘긴다
```

- **중복 제거.** 한 키는 스코프당 최대 한 번 보유됩니다. 같은 키를 반복해서(매 프레임이라도) 로드해도 늘지 않습니다.
- **누수 신호.** 씬 언로드나 종료가 회수한 명시적 스코프는 경로(`Root/Field/ForgottenPopup`)와 함께 경고를
  남깁니다. `ContentService.CaptureStackTrace = true` 면 만든 곳도 붙습니다. 부모가 Dispose 되거나 소유
  GameObject 가 파괴되어 끝나는 것은 정상 수명이라 경고하지 않습니다.
- **과보유 경고.** `ContentScope.DefaultWarnThreshold`(또는 `scope.WarnThreshold`)보다 많이 들면 경고합니다 —
  스코프의 실패 양상은 누수가 아니라 너무 많이 들고 있는 것입니다.
- **한 번도 활성화되지 않은 오브젝트**는 `OnDestroy` 를 받지 못합니다. 그 스코프는 씬이 언로드될 때 회수됩니다.
- **옮겨진 오브젝트**(`MoveGameObjectToScene`, `DontDestroyOnLoad`)는 에셋을 유지합니다: 원래 씬이 언로드되면
  스코프가 지금 사는 씬으로 넘어갑니다.
- **스코프 사이.** `source.ShareTo(target, key)` 는 `target` 에 자기 참조를 주고, `target.Adopt(asset)` 은 이미
  들고 있는 에셋을 받습니다. 이 둘 없이 날것의 에셋을 다른 시스템에 넘기지 마세요.
- 스코프는 에셋을 해제할 뿐, GameObject 를 파괴하지 않습니다.

### 직렬화 링크

```csharp
public ContentLinkSprite icon;                               // ContentLinkGameObject, ContentLinkTexture2D 도 있다
public class ContentLinkAudio : ContentLink<AudioClip> { }   // 다른 타입은 파생 — 드로어는 이것도 그린다

image.sprite = await icon.LoadAsync(this);                   // 소유자 필수(null 이면 ArgumentNullException) — Component/GameObject 면 그 GameObject 스코프, ContentScope 면 그 스코프
image.sprite = await icon.LoadAsync(stage);                  // 예: 명시적 스코프에 넣기
var clip = music.Load(this);                                 // 동기

// 장비 교체: 옛것을 일찍 돌려주기(선택)
icon.Release();
icon.SetName(table[itemId].IconName);
image.sprite = await icon.LoadAsync(this);

icon.SetName("Icons/sheet", "run_01");                       // 스프라이트: 메인 이름과 스프라이트 이름을 따로
other.SetName(icon.Name, icon.SubObjectName);                // 링크 복사: Name 은 메인 이름뿐

icon.IsValid; icon.Asset; icon.IsLoading; icon.Scope; icon.Name; icon.SubObjectName; icon.RuntimeKeyIsValid();
```

### GUID 직렬화 참조

```csharp
public ContentRef<Sprite> icon;                          // ContentRef<GameObject>, ContentRef<Enemy>(프리팹의 컴포넌트) …
public ContentSceneRef level;

image.sprite = await this.Scope().LoadAsync(icon);       // 이름과 똑같이 스코프가 소유
this.Scope().Release(icon);                              // 일찍 돌려주기(선택)
var enemy = await stage.InstantiateAsync(enemyRef);      // GameObject 를 돌려준다
var scene = await this.Scope().LoadSceneAsync(level);

icon.IsSet; icon.RuntimeKeyIsValid(); ContentService.TryGetName(icon, out var name);
```

- 참조는 경로가 아니라 에셋의 **GUID**(와 스프라이트 이름)를 담습니다: 파일을 옮기거나 이름을 바꿔도 카탈로그를
  갱신하면 그대로 동작합니다. 필드는 Addressables `AssetReference` 와 같습니다(`m_AssetGUID`, `m_SubObjectName`).
- 빌드가 카탈로그 엔트리마다 GUID 를 기록하고, 런타임은 참조를 그 **카탈로그 이름**으로 풀어 이름으로 로드합니다.
  그래서 같은 에셋의 참조 로드와 이름 로드는 **슬롯 하나 · 스코프 키 하나**입니다: `Release(icon)` 은
  `LoadAsync<Sprite>("Icons/sword")` 도 돌려주고, 반대도 마찬가지입니다.
- 참조는 로드가 아니라 키만 담습니다 — 복사해도 문제없고, 로드는 스코프를 거칩니다. 필드마다 따로 해제해야 하면
  이름을 한 번 얻어 링크를 쓰세요: `ContentService.TryGetName(icon, out var name)` → `new ContentLink<Sprite>(name)`.
- 서브에셋은 **스프라이트, 이름으로**입니다(Addressables 와 같음). 슬라이스 이름을 바꾸면 참조가 끊깁니다.
- `RuntimeKeyIsValid()` 는 `ContentLink` 와 같이 "등록된 카탈로그(또는 에디터 시뮬레이션)가 로드할 수 있다"는
  뜻입니다 — Addressables 의 "GUID 가 들어 있다"는 `IsSet` 입니다.
- **인스펙터에서.** 에셋을 고르면 GUID 가 저장되고, 둘째 줄에 풀리는 이름이 읽기 전용으로 보입니다. 빨강: 그 GUID 의
  에셋이 없음, 스프라이트 이름이 사라짐(슬라이스 이름 변경), 필드 타입과 다름. 노랑: 아직 카탈로그에 없음, 모든 콘텐츠
  루트 밖(루트 아래로 옮겨도 GUID 는 유지), 스프라이트 아틀라스. 빌드에 담을 수 없는 선택은 링크처럼 거부하고,
  필드에 이미 들어 있는 값은 거부하지 않고 표시만 합니다.

### 인스턴스

```csharp
var go = await scope.InstantiateAsync("Prefabs/Cat", parent);
```

프리팹 참조는 `scope` 가 아니라 **인스턴스**의 것입니다(`scope` 는 자기가 먼저 끝나면 요청을 취소할 뿐). 인스턴스를
파괴하면 돌아갑니다. `Object.Instantiate(instance)` 로 만든 복제본은 `Awake` 에서 참조를 물려받아, 원본이 먼저
파괴돼도 텍스처를 유지합니다. 스포너 클래스가 필요 없습니다: 참조는 테이블 슬롯 하나라 인스턴스당 하나가 싸게 듭니다.

### 씬

소스 루트 아래의 씬은 씬 엔트리(`Scenes/Arena`)가 되어 의존성과 함께 디렉터리에 구워집니다. 씬은 참조 계수되지
않습니다: 수명은 씬 인스턴스 자체입니다. ContentLink 는 이름을 풀고, (누가 로드했든) 열린 콘텐츠 씬을 전부 추적해
씬이 열린 동안 디렉터리가 해제되지 않게 하며, 스코프가 씬을 소유하게 합니다.

```csharp
// 소유: 스코프가 끝나면 언로드(additive 만)
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
scope.ReleaseScene(arena);                                 // 선택: 일찍 언로드

// 직렬화 · 소유자 필수 — ContentLink<T> 와 같은 규칙
public ContentSceneLink level;
var scene = await level.LoadAsync(this);
await level.UnloadAsync();                                 // 선택 · 언제 불러도 안전

// 비소유 · 어떤 모드든(Single 은 다른 씬을 전부 닫는다). SceneManager.UnloadSceneAsync 로 언로드.
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

- Single 모드 씬은 소유할 수 없습니다(소유자도 닫히므로). Single 로드는 호출자도 파괴합니다: MonoBehaviour 에서
  기다릴 때는 `destroyCancellationToken` 을 넘기세요.
- 소유 스코프가 끝날 때 그 씬이 마지막 남은 씬이면 씬은 남고 경고가 찍힙니다. 앱 종료 때는 아무것도 언로드하지 않습니다.
- 취소는 기다림을 멈춥니다. additive 씬은 엔진 로드가 끝난 뒤 다시 언로드됩니다.
- 스코프는 자기 로드가 만든 인스턴스만 소유합니다: `SceneManager` 로 직접 연 씬은 스코프가 같은 씬을 로드해도 그대로
  여러분의 것이고, 실패한 스코프 로드는 그것을 청구하지 않습니다.
- `ContentService.LoadedSceneCount` / `SnapshotScenes(list)` 가 열린 씬을 보여 줍니다.

### 원시 핸들

수명을 직접 관리하는 코드(풀, 스트리밍 시스템)용:

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");   // 동기는 Acquire<T>
h.Asset; h.IsValid; h.Status; h.Key;
var h2 = h.Share();          // 두 번째 참조
h.Dispose();                 // 반환 — 또는 스코프에 넘긴다:
scope.Attach(h2);            // 이제 스코프가 소유

if (ContentService.TryGetLoadableObjectId("Icons/sword", out var id))   // id 기반 로드용 엔진 id
    h = await ContentService.AcquireAsync<Texture2D>(id);
```

`ContentService.Contains(name)` / `ContainsScene(name)` 은 등록된 디렉터리가 그 이름을 갖고 있는지 알려 줍니다. 같은 호출이 `ContentRef<T>` / `ContentSceneRef` 도 받고, `Acquire` / `AcquireAsync` 도 `ContentRef<T>` 를 받습니다.

## Release 는 언제 부르나

| 상황 | 할 일 |
|---|---|
| 컴포넌트 · 팝업 · 스테이지 · 씬 콘텐츠 | 없음. 맞는 스코프에 로드한다. |
| 에셋이 많은 스테이지/화면 | 명시적 스코프 하나, 스테이지가 끝나면 `Dispose()`. |
| 셀을 재활용하는 스크롤 리스트 | 셀마다 자식 스코프, 셀을 다시 바인딩할 때 Dispose. |
| 장비/스킨 교체 | 새것을 로드하기 전에 `Release(key)` 또는 `ContentLink.Release()`. |
| 다른 시스템에 에셋 넘기기 | `ShareTo(target, key)` 또는 `target.Adopt(asset)`. 스코프 사이에 날것의 에셋을 넘기지 말 것. |

## 해제는 어떻게 일어나나

로드한 에셋이 엔진으로 돌아가기까지는 세 단계입니다: **스코프가 자기 끝을 알아채고** → **들고 있던 참조를 전부 놓고**
→ **참조가 0 이 된 에셋을 프레임 끝에 한 번에 돌려줍니다.**

### 1. 스코프는 자기 끝을 어떻게 아는가

| 스코프 | 끝을 알리는 신호 | 안쪽에서 일어나는 일 |
|---|---|---|
| **GameObject** | 그 오브젝트의 `OnDestroy` | `gameObject.Scope()` 를 처음 부를 때 숨은 컴포넌트 `ContentScopeOwner` 가 붙습니다. 그 컴포넌트의 `OnDestroy` 가 스코프를 Dispose 합니다. 한 번도 활성화되지 않은 오브젝트는 `OnDestroy` 를 받지 못하므로, 그 스코프는 **부모인 씬 스코프**가 씬 언로드 때 자식으로서 회수합니다. |
| **Scene** | `SceneManager.sceneUnloaded` | `ContentScope.ForScene(scene)` 은 씬 핸들을 키로 스코프를 만들어 사전에 둡니다. `sceneUnloaded` 가 오면 그 핸들의 스코프를 Dispose 합니다. 자식 GameObject 스코프 가운데 **다른 씬으로 옮겨진 오브젝트**(`MoveGameObjectToScene` · `DontDestroyOnLoad` — 소유 오브젝트의 `scene` 이 이 씬이 아닌 것)는 해제하지 않고 지금 사는 씬의 스코프 밑으로 옮깁니다. |
| **Root** | `Application.quitting` | `ContentService.Shutdown()` 이 Root 를 Dispose 해 트리 전체가 캐스케이드됩니다. Root 를 직접 `Dispose()` 하면 오류 로그만 남깁니다. |
| **Explicit** | 직접 `Dispose()` | 부르지 않으면 부모가 끝날 때 캐스케이드로 끝납니다 — 그때 부모가 씬이나 Root 면 **누수 경고**(경로 + 만든 곳의 스택)를 남깁니다. 부모가 명시적 스코프거나 소유 GameObject 가 파괴된 경우는 정상 수명이라 경고하지 않습니다. |

복제본(`Object.Instantiate`)은 `ContentScopeOwner` 가 직렬화해 둔 키 목록을 물려받아 `Awake` 에서 **자기 참조**를 잡습니다
(원본이 아직 들고 있으면 공유, 이미 사라졌으면 다시 로드). 비활성으로 만든 복제본은 `Awake` 가 미뤄져 처음 활성화될
때 잡습니다.

### 2. 스코프가 끝날 때 하는 일 — 순서대로

1. `IsDisposed = true`, `DisposedToken` 취소 — 그 스코프로 진행 중이던 로드 요청이 취소됩니다.
2. 자식 스코프를 역순으로 끝냅니다(위의 이관 규칙 적용).
3. 이름으로 든 참조(`LoadAsync`/`Load`)와 `Attach` 한 핸들을 전부 놓습니다 → 각 에셋의 참조 카운트가 내려갑니다.
4. 스코프가 소유한 씬을 언로드합니다(앱 종료 때는 제외 — 엔진이 알아서 내립니다).
5. 명시적 스코프를 씬/Root 가 회수한 경우 경고를 찍습니다.

### 3. 참조가 0 이 된 뒤

에셋마다 슬롯이 하나이고(같은 이름은 어느 스코프에서 불러도 같은 슬롯), 슬롯은 참조 카운트를 가집니다. 참조가 0 이
되면 슬롯은 **바로 해제되지 않고** 대기 목록에 들어가며 그 프레임 번호를 기억합니다. 실제 해제는
`ContentService.Collect()` 가 합니다 — 패키지가 플레이어 루프의 `PostLateUpdate` 끝에 끼워 넣어 **프레임당 한 번**,
여러분의 코드가 다 돈 뒤에 돕니다.

`Collect()` 는 대기 중인 슬롯마다:

- 그 사이 누군가 다시 얻어 참조가 0 이 아니면 → 목록에서 뺄 뿐, 아무것도 안 합니다(**같은 프레임의 해제 → 재획득은
  다시 로드하지 않습니다**).
- 아직 로딩 중이면 → 로드가 끝날 때까지 건드리지 않습니다.
- 참조 0 이 된 지 `ReleaseDelayFrames` 프레임이 안 됐으면 → 다음 프레임에 다시 봅니다.
- 그 밖에는 → 엔진의 `Loadable.Release()` 를 부르고 슬롯을 비웁니다. 슬롯의 세대 번호가 올라가므로, 뒤늦게 끝난
  로드 완료가 빈 슬롯을 건드리지 못합니다.

`ContentService.AutoCollect = false` 면 플레이어 루프 훅이 아무것도 하지 않고 `Collect()` 를 직접 불러야 합니다.

### 디렉터리와 종료

`ContentService.UnregisterDirectory(dir)` 는 그 디렉터리의 에셋이 아직 보유 중이거나 그 디렉터리의 씬이 열려 있으면
거부합니다 — 엔진이 모든 `Loadable` 의 해제와 모든 씬의 언로드를 먼저 요구하기 때문입니다. `force: true` 는 남은
에셋 참조를 무효화하며(열린 씬은 절대 아님), 그것을 들고 있던 스코프는 그 뒤 살아 있는 참조만 봅니다 — `Count`,
`DumpTree`, `Release(key)` 는 죽은 참조를 조용히 버립니다. 패킹 디렉터리는 해제될 때 아카이브를 언마운트합니다.
`ContentService.Shutdown()`(`Application.quitting` 에서도 실행)은 스코프 트리를 Dispose 하고, 남은 원시 핸들을
보고하고, 전부 해제합니다.

## 에디터 시뮬레이션

에디터에서는 프로젝트가 콘텐츠 디렉터리를 대신합니다. Play Mode 에서 `RegisterDirectory`(또는
`RegisterPackedDirectoryAsync`)에 넘긴 경로가 어느 **프로파일의 출력 폴더**이면 엔진에는 아무것도 등록되지 않고,
이름은 프로파일의 소스 루트를 지금 디스크에 있는 대로 훑어(카테고리로 걸러) 답하며, 에셋은 프로젝트에서 로드됩니다.
텍스처를 고치고 Play 를 누르면 고친 것이 보이고, 파일을 지우면 로드가 실패하고, 추가하면 로드됩니다. 저장소를 받아
바로 Play 를 눌러도 됩니다 — 빌드가 필요 없습니다.

```
Window > ContentLink > Editor Simulation     토글(기본 켜짐 · 프로젝트별)
```

Addressables 의 *Use Asset Database* 플레이 모드에 해당합니다: 저작 편의이지 빌드의 대체가 아닙니다. 빌드가 같게
동작할지 확인하려면 끄세요 — 그러면 에디터도 플레이어와 똑같이 구워진 디렉터리를 실제로 등록합니다.

- **아무것도 쓰지 않습니다.** 색인(이름 → 경로)은 메모리에만 있고, 디렉터리 등록 때나 Play 진입에 만들고 Play 종료에 버립니다.
- **프로파일의 출력 폴더만** 이렇게 대신합니다. 그 밖의 경로(다운로드 사본 · 임의로 구운 것)는 실제로 등록되며, 그런
  디렉터리의 루트 밑에서는 아직 어느 카탈로그도 모르는 이름만 프로젝트에서 찾습니다(원래의 시뮬레이션). 두 곳에 다
  있는 이름은 실제 카탈로그가 답합니다.
- **등록은 여전히 필요합니다.** 빠뜨린 `RegisterDirectory` 는 플레이어에서처럼 에디터에서도 실패하고, 같은 폴더의 중복
  등록은 거부되며, 소스 루트가 없는 프로파일은 예외를 던집니다.
- **토글은 디렉터리를 등록할 때 읽습니다.** Play 중에 끄면 이미 프로젝트가 대신하던 디렉터리는 다음 Play 까지 그대로
  동작하고, 실제 디렉터리 밑의 새 이름만 멈춥니다. Play 종료 때의 요약과 초기화는 그 순간의 토글 상태와 무관하게 돕니다.
- **해제된 에셋은 언로드되지 않습니다.** 참조 계수는 그대로지만 에디터가 오브젝트를 살려 두므로, 저작 중의 메모리
  수치는 빌드보다 높습니다. 언로드 동작은 토글을 끄고 확인하세요. 프로젝트가 대신한 에셋을 들고 있어도 디렉터리 해제는
  막히지 않습니다.
- **Play 중의 수정은 그 자리에서 보입니다**(재임포트가 로드된 객체를 다시 채웁니다). 삭제한 에셋은 들고 있던 핸들
  아래에서 객체가 파괴되고, 새 로드는 오류와 함께 실패합니다.
- **아카이브는 돌지 않습니다.** `Packed` · `Engine` 출력도 프로젝트가 대신하므로, 마운트·아카이브 동작은 토글을 끄고 봅니다.
- **시뮬레이션 씬**은 `EditorSceneManager.LoadSceneAsyncInPlayMode` 로 열립니다. 요청한 스코프가 여전히 언로드하지만,
  어느 디렉터리에도 속하지 않습니다.
- **중복 이름은 거부됩니다** — 빌더가 거부하듯이. 에디터에서만 되는 이름은 없습니다.

그렇게 대신한 등록마다 어느 프로파일과 루트가 답하는지 한 줄이 찍힙니다. Play Mode 를 나가면 시뮬레이션이 푼 이름 —
로드했거나, `RuntimeKeyIsValid` / `Contains` 로 확인만 한 것 — 가운데 그 프로파일의 카탈로그가 아직 모르는 것을 한 줄로
남깁니다 — 출하 전에 빌드가 필요한 링크들입니다.
`ContentService.SnapshotSimulated(list)` 는 플레이 중에 프로젝트가 대신한 이름 전부를 알려 줍니다.
`ContentService.TryGetLoadableObjectId(name, out id)` 도 프로젝트가 대신하는 이름을 답합니다.

## 진단

- *Window > ContentLink > Dump Scope Tree*(Play Mode), 또는 `ContentScope.DumpTree()`:

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```
  자식은 스코프가 들고 있는 순서대로 찍히며 그것은 생성 순서가 아닙니다: 스코프를 지우면 마지막 형제가 그 자리로
  오므로, 트리가 얼마나 크든 제거 비용이 같습니다.

- `ContentService.Snapshot(list)` — 살아 있는 모든 참조(`CaptureStackTrace` 가 켜져 있으면 스택 트레이스 포함)
- `ContentService.SnapshotScenes(list)` / `LoadedSceneCount` — 열린 콘텐츠 씬
- `ContentService.SnapshotSimulated(list)` — 에디터 시뮬레이션이 푼 이름
- `ContentService.LiveAssetCount` — 현재 보유 중인 에셋 수
- 빌드 창은 에디터 세션의 마지막 빌드(프로파일 · 타깃 · 파일 수 · 시간 · 오류)를 보여 주고, 로그에는 빌드마다
  `[ContentLink] built …` 한 줄이 남습니다

## AssetLink · Addressables 에서 옮겨오기

| 이전 | ContentLink |
|---|---|
| `AssetLink.LoadAssetAsync()` + `ReleaseAsset()` | `ContentLink.LoadAsync(owner)` · `Release()` 는 선택 |
| `AssetLinkSpawner.SpawnAsync()` + `DoAutoRelease` | `scope.InstantiateAsync()` |
| `AddressablesTracker.ReportUnreferencedHandles()`(GC 기반) | 씬 언로드/종료 때의 스코프 경고(결정적) |
| Auto Regist Folder Pattern | 빌드 프로파일과 카테고리 |
| `AssetRef`(GUID) | `ContentRef<T>` / `ContentSceneRef` |
| `AssetReference` / `AssetReferenceSprite` 필드 | `ContentRef<T>` 필드 — 타입만 바꾸면 데이터가 남는다(아래) |
| `ref.LoadAssetAsync()` + `ref.ReleaseAsset()` | `this.Scope().LoadAsync(ref)` · `this.Scope().Release(ref)` 는 선택 |
| `ref.RuntimeKeyIsValid()`("값이 있나") | `ref.IsSet` |
| *Use Asset Database* 플레이 모드 | 에디터 시뮬레이션 |
| Addressables 그룹 / 번들 | 프로파일 / 카테고리 · `Packed` 아카이브 |

**`AssetReference` 필드에서.** 필드 타입을 `ContentRef<T>` 로 바꾸면 직렬화된 GUID 와 스프라이트 이름이 남습니다.
에셋은 콘텐츠 루트 아래에 있어야 합니다(옮겨도 GUID 는 유지). Addressables 는 세 가지 모양으로 기록하고, 그대로
풀리는 것은 하나입니다:

| Addressables 에서 고른 것 | 저장된 값 | ContentLink 에서 |
|---|---|---|
| 스프라이트 | 텍스처 GUID + 스프라이트 이름 | 풀림 |
| `AssetReferenceSprite` 에 떨군 텍스처 | 텍스처 GUID, 스프라이트 이름 없음 | 스프라이트가 아니라 텍스처: 필드가 빨강 — 스프라이트를 다시 고르세요 |
| Addressable 스프라이트 아틀라스에 든 스프라이트 | 아틀라스 GUID + 스프라이트 이름 | 지원 안 함(아틀라스 경유 스프라이트는 카탈로그에 없음): 노랑 — 텍스처에서 스프라이트를 고르세요 |

## 주의와 제한

- 메인 스레드 전용. 취소 토큰도 메인 스레드에서.
- 엔진 로드는 취소할 수 없습니다. 취소된 요청은 참조를 즉시 돌려주고, 로드는 끝난 뒤 다음 `Collect()` 에서 해제됩니다.
- 엔진이 예외를 던진 로드는 동기·비동기 경로 모두 다른 실패와 똑같이 보고됩니다 — 무효 핸들과 로그 — 남는 것은 없습니다.
- 프로젝트가 대신한 Play Mode 콘텐츠(에디터 시뮬레이션)는 Unity 가 참조 계수하지 않습니다. 해제 동작은 시뮬레이션을
  끄고, 구워서 등록한 콘텐츠 디렉터리로 확인하세요.
- 비활성 상태로 만든 복제본은 처음 활성화될 때 참조를 가져갑니다.
- 씬은 참조 계수되지 않습니다. 같은 씬을 additive 로 두 번 로드하면 인스턴스 둘이 생기고 각각 추적·언로드됩니다.
  씬에는 `ShareTo` / `Adopt` 가 없습니다.
- 콘텐츠 디렉터리는 한 빌드 타깃용이라 다른 타깃에서는 로드되지 않을 수 있습니다.
- `Packed` 의 아카이브 크기 상한은 압축된 파일이 아니라 입력 바이트 기준입니다. 해시 접두 트리는 단순 채우기보다
  아카이브를 약 1.5배 만들고, 상한 근처에서 오가는 아카이브는 빌드마다 쪼개지고 합쳐질 수 있으며, `MaxArchiveMB`
  나 분류 구현체를 바꾸면 산출물이 다시 분할됩니다.
- 빌드는 프로젝트 락이 필요합니다: 명령줄 빌드 전에 다른 에디터 인스턴스에서 프로젝트를 닫으세요.
- 참조와 이름은 에셋의 스코프 키를 공유합니다: 어느 쪽으로 해제해도 둘 다 풀립니다. 두 필드를 따로 해제해야 하면
  `ContentLink` 나 자식 스코프를 쓰세요.
- 링크의 `Name` 은 메인 이름이고, 스프라이트 링크의 스프라이트 이름은 `SubObjectName` 입니다. `SetName(name)` 은 스프라이트
  이름을 비우므로 링크는 `SetName(a.Name, a.SubObjectName)` 으로 복사하세요 — 그걸 잃은 `ContentLink<Object>` 는 에러 없이
  텍스처를 로드합니다.
- 서브에셋은 스프라이트만, 이름으로 참조합니다. 다른 서브에셋(모델 파일 안의 메시)과 스프라이트 아틀라스를 거친
  스프라이트는 참조할 수 없습니다.
- 같은 파일이 두 이름으로 빌드됐으면 — 베이스 디렉터리와 이후 패치 사이에 옮김 — 참조는 가장 최근에 등록된 디렉터리의
  이름으로 풀리고 옛 이름을 가진 링크는 다른 사본을 로드합니다: 메모리에 사본이 둘입니다.

## 저장소 구조와 테스트

| 경로 | 무엇 |
|---|---|
| `com.xpTURN.ContentLink/` | 패키지(`Runtime`, `Editor`, `Tests~/Verify`, `README`, `CHANGELOG`) |
| `samples/LinkSample/` | 패키지를 `file:` 로 쓰는 Unity 6.6 프로젝트 — `Assets/Content` 아래 텍스처 · 스프라이트 아틀라스 · 프리팹 · 씬 · 오디오 |
| `tests/LinkTests/` | Unity Test Runner 프로젝트: EditMode(빌더 · 설정 · 패커 · 창 모델) · PlayMode(수명 · 씬 · 패킹 등록 · 할당) |
| `tools/run-all-tests.sh` | 전부 실행: dotnet 하네스 → EditMode → PlayMode(`--player` 로 스탠드얼론 플레이어 추가) |
| `tools/build-content.sh` | 명령줄 콘텐츠 빌드 |
| `docs/IMP/` | 설계·구현 기록 |
| `.github/` | CI(`workflows/check.yml`): push 와 pull request 마다 dotnet 하네스 · `.meta` 사이드카 검사 · 버전 매니페스트 검사. Unity 스위트는 라이선스가 필요해 로컬에 남긴다 |

`Tests~/Verify` 는 문서화된 Unity 6.6 API 의 스텁과 Loadable 참조 규칙을 강제하는 가짜 엔진에 대해 패키지를
컴파일하고 수명·빌드·패킹 시나리오를 돌립니다(.NET 8 SDK 로 `dotnet run`). Unity 스위트는 테스트가 직접 구운
콘텐츠 디렉터리에 대해 에디터와 스탠드얼론 플레이어에서 돕니다. Unity 6000.6.0f1 로 검증했습니다.

```
tools/run-all-tests.sh                 # 하네스 + EditMode + PlayMode
tools/run-all-tests.sh --dotnet-only   # 하네스만(몇 초)
tools/run-all-tests.sh --player        # StandaloneOSX 플레이어에서의 PlayMode 도
```

## 라이선스

Apache License 2.0
