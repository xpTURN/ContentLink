# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | **한국어** | [日本語](README.ja-JP.md) | [简体中文](README.zh-CN.md)

ContentLink 는 Unity 6.6 을 위한 완결된 콘텐츠 작업 흐름으로, 엔진의 **콘텐츠 디렉터리**를 기반으로 합니다.
콘텐츠 디렉터리는 플레이어가 런타임에 등록하고 로드하는, 빌드된 에셋의 폴더입니다. ContentLink 는 에셋을 프로젝트
폴더에서 화면까지 데려갑니다. 에셋을 **이름으로** 가리키고, 빌드하고, 플레이어에 넣거나 CDN 으로 출하하고, 스크립트나 인스펙터
필드에서 로드합니다. 로드한 쪽이 사라지면 에셋은 저절로 해제됩니다.

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        // Assets/Content/Icons/sword.png — 이름으로 로드하고, 이 GameObject 가 파괴될 때 해제된다
        image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
    }
}
```

> **이름으로 가리키세요, 에디터에서 CDN 까지 — 그리고 해제는 잊으세요.** 에셋은 소유자 — GameObject, 씬, 앱 — 가
> 사는 만큼만 살고, `Release()` 는 언제나 최적화일 뿐 의무가 아닙니다.

---

## 목차

1. [무엇을 얻는가](#무엇을-얻는가)
2. [설치](#설치)
3. [전체 그림](#전체-그림)
4. [튜토리얼: 첫 콘텐츠 디렉터리](#튜토리얼-첫-콘텐츠-디렉터리)
5. [카탈로그 설정](#카탈로그-설정)
6. [빌드](#빌드)
7. [로드](#로드)
8. [해제](#해제)
9. [CDN 에서 콘텐츠 받기](#cdn-에서-콘텐츠-받기)
10. [로컬라이즈 콘텐츠](#로컬라이즈-콘텐츠)
11. [에디터 시뮬레이션](#에디터-시뮬레이션)
12. [진단](#진단)
13. [Addressables · Resources 에서 옮겨 오기](#addressables--resources-에서-옮겨-오기)
14. [알아 둘 점](#알아-둘-점)
15. [라이선스](#라이선스)

---

## 무엇을 얻는가

| 단계 | ContentLink 가 주는 것 |
|---|---|
| **이름** | 폴더(`Assets/Content`) 아래의 모든 에셋이 경로에서 이름을 얻습니다: `Icons/sword`, `Prefabs/Cat`, `Scenes/Arena` |
| **고르기** | 이름(`ContentLinkSprite icon;`)이나 GUID(`ContentRef<Sprite>`)를 담는 인스펙터 필드 — 끌어다 놓기와 상태 색 |
| **작업** | 에디터에서는 Play Mode 가 프로젝트에서 바로 로드합니다 — 작업 중엔 빌드할 것이 없습니다 |
| **빌드** | 프로파일 · 카탈로그 · 압축 · 크기 분할 아카이브를 다루는 빌드 창, CI 용 명령줄 |
| **출하** | 플레이어 안(`StreamingAssets`)에, 또는 CDN 에서 받기 — 바뀐 아카이브만, 이어받기와 MD5 검증 |
| **로드** | 스프라이트 · 프리팹 · 오디오 · 모든 에셋 형식, 프리팹 인스턴스와 씬 — 비동기 또는 동기, 이름으로 |
| **해제** | 자동: 모든 로드는 **스코프**(GameObject · 씬 · 앱 · 직접 만든 것)에 속하고 스코프가 끝나면 함께 갑니다 |
| **살펴보기** | 스코프 트리 덤프, 살아 있는 참조 스냅숏, 끝내기를 잊은 스코프를 이름으로 알리는 경고 |

의존성은 Unity 6000.6 뿐입니다.

## 설치

*Window > Package Manager > + > Add package from git URL*:

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

또는 로컬 체크아웃이라면 `Packages/manifest.json` 에 추가합니다:

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

이 저장소의 `samples/LinkSample` 은 아래 튜토리얼과 똑같이 설정된 작은 Unity 프로젝트입니다: `Assets/Content` 아래의
텍스처 · 스프라이트 아틀라스 · 프리팹 · 씬 · 오디오, 그리고 그것을 등록하고 로드하는 부트스트랩 스크립트.

## 전체 그림

```
 에디터에서                                       런타임에서 (플레이어, 또는 Play Mode)
 ─────────────────────────────────────────────   ─────────────────────────────────────────────────────
 Assets/Content/Icons/sword.png                  1. 시작할 때 폴더를 한 번 등록
        │                                            ContentService.RegisterDirectory(".../MainContent")
        │  프로파일 "Main"                        2. 이름으로 스코프에 로드
        ▼                                            this.Scope().LoadAsync<Sprite>("Icons/sword[sword]")
 카탈로그  "Icons/sword" → 엔진 id                 3. 스코프가 끝남 → 에셋이 해제됨
        │
        │  빌드
        ▼
 StreamingAssets/MainContent/  (콘텐츠 디렉터리: 빌드된 에셋 + 카탈로그)
```

| 용어 | 무엇인가 |
|---|---|
| **소스 루트** | 에셋을 빌드할 폴더. 기본은 `Assets/Content`. |
| **이름** | 로드할 때 쓰는 것: 소스 루트 아래 에셋 경로에서 확장자를 뺀 것 — `Icons/sword`. |
| **카탈로그** | 모든 이름과 그 뒤의 엔진 id 를 적은 `ContentCatalog` 에셋. 창이 쓰고, 손으로 고치지 않습니다. 빌드가 콘텐츠 디렉터리에 넣습니다. |
| **프로파일** | 콘텐츠 디렉터리 하나의 빌드 설정: 소스 루트, 출력 폴더, 카탈로그, 아카이브 형식. |
| **콘텐츠 디렉터리** | 빌드의 출력 폴더. 런타임에 등록한 뒤 이름으로 로드합니다. |
| **스코프** | 로드한 것의 소유자. 스코프가 끝나면 그것이 로드한 모든 것이 해제됩니다. |
| **링크 · 참조** | 이름(링크)이나 에셋 GUID(참조)를 담는 직렬화 필드. 인스펙터에서 정합니다. |

## 튜토리얼: 첫 콘텐츠 디렉터리

프로파일 하나, 카탈로그 하나, 빌드 한 번을 따라갑니다: 플레이어 안에 넣어 출하하는 콘텐츠입니다.

### 1단계 — 에셋을 소스 루트 아래에 둔다

`Assets/Content` 폴더를 만들고 콘텐츠를 넣습니다. 하위 폴더는 마음대로 나눠도 됩니다:

```
Assets/Content/Icons/sword.png        (Texture Type: Sprite)
Assets/Content/Prefabs/Cat.prefab
Assets/Content/Audio/click.wav
Assets/Content/Scenes/Arena.unity
```

파일마다 이름이 생깁니다 — `Assets/Content` 아래 경로에서 확장자를 뺀 것입니다:

| 파일 | 이름 | 로드되는 것 |
|---|---|---|
| `Icons/sword.png` | `Icons/sword` | `Texture2D` |
| — 그 스프라이트 `sword` | `Icons/sword[sword]` | `Sprite` |
| `Prefabs/Cat.prefab` | `Prefabs/Cat` | 프리팹(`GameObject`) |
| `Audio/click.wav` | `Audio/click` | `AudioClip` |
| `Scenes/Arena.unity` | `Scenes/Arena` | 씬 — `LoadSceneAsync` 로 로드 |

이 파일들이 쓰는 에셋 — 머티리얼의 텍스처, 프리팹의 메시 — 은 자동으로 함께 빌드됩니다. 이름으로 로드할 것만 루트
아래에 있으면 됩니다.

### 2단계 — 빌드 창을 연다

*Window > ContentLink > Build Window*. 처음 열면 창에 프로파일 `Main` 하나가 있습니다:

| 필드 | 값 | 뜻 |
|---|---|---|
| Name | `Main` | 프로파일 이름 |
| Name style | `PathWithoutExtension` | 확장자 없는 이름(`Icons/sword`) — [이름 스타일](#이름-스타일) 참고 |
| Source root | `Assets/Content` | 에셋을 빌드할 폴더 |
| Output path | `Assets/StreamingAssets/MainContent` | 빌드를 쓸 곳 — `StreamingAssets` 는 플레이어 안에 들어갑니다 |
| Categories | 한 행: *(whole root)* | 루트 전체가 카탈로그 하나로 — `Assets/ContentCatalog.asset` 에 만들어짐 |
| Archive | `None` | 평평한 파일 — [아카이브 형식](#아카이브-형식) 참고 |
| Compression | `Uncompressed` | 아카이브에만 적용 |
| Target | `NoTarget` | 지금 활성인 플랫폼으로 빌드 |

설정은 `ProjectSettings/ContentLinkBuild.asset` 에 저장됩니다. 이 파일을 커밋하세요.

### 3단계 — 카탈로그를 만든다

창 아래쪽의 **Update catalogs** 를 누릅니다. `Assets/Content` 를 훑어 `Assets/ContentCatalog.asset` 을 씁니다. Project
창에서 그 에셋을 고르면 인스펙터에 모든 이름이 나옵니다.

- 카탈로그 에셋을 커밋하세요. 인스펙터 필드가 이것을 읽어 이름이 유효한지 보여 줍니다.
- 손으로 고치지 않습니다. **Update catalogs** 와 빌드가 매번 다시 씁니다.
- 소스 루트 **밖에** 두세요 — 루트 아래의 카탈로그는 거부됩니다.

빌드 전마다 **Update catalogs** 를 누를 필요는 없습니다: 빌드가 카탈로그를 스스로 갱신합니다. 에셋을 추가하거나
옮긴 뒤 인스펙터가 새 이름을 바로 알아보게 하고 싶을 때 누르세요.

### 4단계 — 디렉터리를 등록하고 로드한다

앱이 시작할 때, 무엇이든 로드하기 전에 콘텐츠 디렉터리를 한 번 등록합니다. 첫 씬의 `Boot` 컴포넌트면 충분합니다:

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
        ContentService.RegisterDirectory(path);   // Archive = None 또는 Engine
        // Archive = Packed:  await ContentService.RegisterPackedDirectoryAsync(path);  (async 메서드 안에서)
    }
}
```

그다음 이름으로 로드합니다. `this.Scope()` 는 이 GameObject 의 스코프입니다: GameObject 가 파괴되면 스프라이트가
해제됩니다.

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

또는 인스펙터에서 에셋을 고르게 합니다 — 필드에 `Assets/Content/Icons/sword.png` 를 끌어다 놓으세요:

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;
    public ContentLinkSprite icon;   // 인스펙터가 "Icons/sword" + 스프라이트 "sword" 를 저장

    async void Start() => image.sprite = await icon.LoadAsync(this);
}
```

Play 를 누릅니다. **빌드 없이** 동작합니다: 에디터에서는 프로파일의 출력 폴더를 등록하면 그 프로파일을 프로젝트가
대신합니다([에디터 시뮬레이션](#에디터-시뮬레이션)). 에셋을 추가 · 수정 · 삭제하고 다시 Play 를 누르면 그대로 보입니다.

### 5단계 — 빌드한다

**Build 'Main'** 을 누릅니다. 창의 *Last build* 에 결과가 나오고, `Assets/StreamingAssets/MainContent` 에 콘텐츠
디렉터리 — 엔진의 파일과 카탈로그 — 가 생깁니다.

그다음 평소처럼 플레이어를 빌드합니다(*File > Build Profiles*). `StreamingAssets` 가 플레이어에 들어가고, `Boot` 가 그
폴더를 찾습니다.

- 콘텐츠가 바뀌었다면 **플레이어 빌드 전마다 콘텐츠를 빌드**하세요. 플레이어는 마지막 콘텐츠 빌드가 쓴 것을 싣습니다.
- **콘텐츠 디렉터리는 플랫폼 하나용으로 빌드됩니다** — 에디터에서 활성인 플랫폼입니다. 플랫폼을 바꾸면 콘텐츠를 다시
  빌드하세요.

### 6단계 — 에디터에서 빌드 확인 (선택)

*Window > ContentLink > Editor Simulation* 을 끄고 Play 를 누르면, 에디터가 플레이어처럼 빌드된 폴더를 로드합니다. 다시
켜면 빌드 없이 작업하는 상태로 돌아갑니다.

이것이 한 바퀴 전부입니다: 에셋을 루트 아래에 두고, 이름으로 로드하고, 출하 전에 빌드합니다.

## 카탈로그 설정

### 이름

- 이름은 소스 루트 아래 에셋 경로에서 확장자를 뺀 것입니다: `Assets/Content/Icons/sword.png` → `Icons/sword`.
- **스프라이트**는 `<파일>[<스프라이트>]`: `Icons/sword[sword]`. 스프라이트 시트 `sheet.png` 는 `Icons/sheet[run_01]`,
  `Icons/sheet[run_02]`, … 를 줍니다.
- 루트 아래의 **스프라이트 아틀라스**(Sprite Atlas V2)는 그것이 묶는 스프라이트마다 아틀라스를 거친 이름을 하나 더
  붙입니다: `Atlas/UI[icon]`. 스프라이트 자신의 이름과 아틀라스의 이름은 같은 스프라이트를 로드합니다.
- **씬**은 에셋처럼 이름이 붙고(`Scenes/Arena`) `LoadSceneAsync` 로 로드합니다.
- 이름은 **프로파일 안에서 유일**합니다. 같은 이름이 될 두 에셋 — 같은 폴더의 `sword.png` 와 `sword.prefab` — 은 두
  파일을 밝히며 빌드가 실패합니다.
- 이름은 대소문자를 가리고 파일의 철자를 그대로 씁니다. 한글 · 가나 · 악센트가 든 이름도 어느 기기에서나 똑같이
  동작합니다.

### 이름 스타일

프로파일의 **Name style** 이 이름에 확장자를 남길지 정합니다:

| Name style | `Icons/sword.png` | 나란히 놓인 `sword.png` 와 `sword.prefab` |
|---|---|---|
| `PathWithoutExtension` (기본) | `Icons/sword` | 거부 — 둘 다 `Icons/sword` |
| `PathWithExtension` | `Icons/sword.png` | 허용 — `Icons/sword.png` 와 `Icons/sword.prefab` |

**일찍 정하세요.** 바꾸면 다음 카탈로그 갱신에서 루트의 모든 이름이 바뀝니다: 옛 이름을 쥔 링크 · 코드 · 데이터는
로드되지 않습니다. `ContentRef` 필드는 GUID 를 쥐므로 영향이 없습니다. 프로파일과 카탈로그가 어긋나 있는 동안 창이
안내를 보여 줍니다.

### 카테고리: 디렉터리 하나에 카탈로그 여러 개

기본은 루트 전체가 카탈로그 하나에 들어갑니다. 큰 프로젝트는 루트를 하위 폴더로 나눠 폴더마다 카탈로그 파일을 따로 둘
수 있습니다 — 파일이 작아지고, 다른 폴더에서 일하는 사람끼리 같은 카탈로그를 건드리지 않습니다.

빌드 창의 **Categories** 에서:

1. *(whole root)* 행을 **−** 로 지웁니다. 카탈로그 하나로 충분하면 그대로 둡니다.
2. **Add category** 에 루트의 하위 폴더 — `Characters` — 를 적고 **Add** 를 누릅니다. `UI` 와 `Levels` 도 같은 방법으로.
3. **Catalog** 칸은 비워 둡니다: 카탈로그는 첫 빌드나 카탈로그 갱신 때 `Assets/ContentCatalogs/<프로파일>.<카테고리>.asset`
   에 만들어집니다(창에 경로가 나옵니다). 또는 이미 있는 `ContentCatalog` 를 고릅니다(*Assets > Create > xpTURN >
   ContentLink > Content Catalog*).

```
프로파일 "Main"                소스 루트 Assets/Content
  카테고리  Characters    →    Assets/ContentCatalogs/Main.Characters.asset
  카테고리  UI            →    Assets/ContentCatalogs/Main.UI.asset
  카테고리  Levels        →    Assets/ContentCatalogs/Main.Levels.asset
```

- **카테고리는 이름을 바꾸지 않습니다.** `Assets/Content/UI/logo.png` 는 어느 카탈로그에 들든 `UI/logo` 입니다.
- 프로파일의 모든 카탈로그는 **한** 콘텐츠 디렉터리에 들어갑니다: 한 번 등록하면 모든 이름을 로드할 수 있습니다.
- 카테고리는 겹칠 수 없습니다: *(whole root)* 행과 하위 폴더 행을 함께 쓸 수 없고, `UI` 와 `UI/Icons` 도 함께 쓸 수
  없습니다.
- 어느 카테고리에도 들지 않는 루트 아래 에셋은 **빌드되지 않습니다** — 빌드가 그 개수와 함께 경고를 남깁니다.

### 프로파일 여러 개: 따로 등록하는 콘텐츠 디렉터리

프로파일마다 콘텐츠 디렉터리 하나를 빌드하고, 각각 따로 등록합니다. 콘텐츠를 따로 출하하거나 받을 때 여러 개를
씁니다 — 본편과 DLC 팩, 또는 [로케일](#로컬라이즈-콘텐츠).

| 프로파일 | 소스 루트 | 출력 경로 | Archive |
|---|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` | `Packed` |
| `Dlc1` | `Assets/Dlc1` | `Build/Content/Dlc1` | `Packed` |

추가하려면 **New profile** 에 이름을 적고 **Add** 를 누릅니다. 새 프로파일은 출력 경로 `Build/Content/<이름>`, LZ4 압축의
`Packed` 아카이브, `Assets/ContentCatalogs/<이름>.asset` 의 카탈로그 하나로 시작합니다. **Set active** 는
*Window > ContentLink* 메뉴 항목이 빌드할 프로파일을 고릅니다.

- 소스 루트는 서로 안에 넣지 말고 **나란히** 두세요.
- 한 루트 아래의 에셋이 다른 루트 아래의 에셋을 참조하면 그 **사본**이 자기 디렉터리로 들어갑니다. 공유하는 에셋은
  루트를 건너 참조하지 말고 이름으로 로드하세요.

## 빌드

### 빌드 창

| 필드 | 무엇을 넣나 |
|---|---|
| **Content version** | 선택. 모든 Packed 빌드에 찍히는 표지 — 받을 때 본편과 그 로케일을 짝짓는 데 씁니다 |
| **Name** | 프로파일 이름. `Packed` 프로파일은 영문자 · 숫자 · `.` · `-` · `_` 만(아카이브 이름이 됩니다) |
| **Name style** | [이름 스타일](#이름-스타일) 참고 |
| **Source root** | 빌드할 폴더(**…** 로 고르기) |
| **Output path** | 빌드를 쓸 곳(**…** 로 고르기) — [출력을 둘 곳](#출력을-둘-곳) 참고 |
| **Target** | `NoTarget` 은 활성 플랫폼으로 빌드. 다른 값이면 그 밖의 플랫폼에서는 빌드를 거부 |
| **Categories** | 하위 폴더와 그 카탈로그 — [카테고리](#카테고리-디렉터리-하나에-카탈로그-여러-개) 참고 |
| **Archive** | `None` · `Engine` · `Packed` — [아카이브 형식](#아카이브-형식) 참고 |
| **Max archive MB** | `Packed` 만: 아카이브 하나에 들어갈 콘텐츠의 대략적인 크기(기본 20) |
| **Compression** | `Uncompressed` · `LZ4` · `LZMA` — 아카이브에만. `LZ4` 가 무난한 기본 |
| **Clean build cache** | 엔진의 빌드 캐시를 무시하고 전부 다시 빌드 |
| **Disable type trees** | 더 작은 출력, 대신 **정확히 같은 Unity 버전에서만** 로드됨 |
| **Fail when errors are logged** | 기본 켬: 빌드 중 오류가 로그되면 빌드 실패 |
| **Partitioner** | `Packed` 만: 파일을 아카이브로 묶는 방식(기본값 그대로 두세요) |
| **Post-processors** | 빌드 뒤에 도는 단계 — [빌드 뒤](#빌드-뒤) 참고 |

아래쪽: **Update catalogs**(카탈로그만 쓰기), **Build '\<프로파일\>'**, **Build all**. 문제는 무엇이든 빌드하기 전에 그
버튼들 위에 나오고, 카테고리의 문제는 그 행 아래에 나옵니다.

### 아카이브 형식

| Archive | 출력 모양 | 등록 | 언제 쓰나 |
|---|---|---|---|
| `None` | 엔진의 평평한 파일 | `ContentService.RegisterDirectory(path)` | 콘텐츠를 플레이어에 넣고 가장 단순하게 가고 싶을 때 |
| `Engine` | `content0.archive` 하나 | `ContentService.RegisterDirectory(path)` | 플레이어 안에 압축 파일 하나로 |
| `Packed` | *Max archive MB* 쯤 되는 아카이브 몇 개 + `archives.json` · `archives.hash` | `await ContentService.RegisterPackedDirectoryAsync(path)` | 콘텐츠를 내려받을 때, 또는 압축하되 패치용으로 나눌 때 |

`Packed` 빌드는 **결정론적**입니다: 같은 입력은 바이트까지 같은 아카이브로 묶이고, 작은 변경은 아카이브 몇 개만
건드립니다 — 나머지는 이름과 바이트를 그대로 지키므로 패치가 작습니다. *Max archive MB* 는 압축 전 콘텐츠 크기를
셉니다. LZ4 면 디스크의 파일은 그 절반쯤이 됩니다.

### 출력을 둘 곳

| 출력 경로 | 어떻게 되나 |
|---|---|
| `Assets/StreamingAssets/` 아래 | 폴더가 플레이어 안에 들어갑니다. `Application.streamingAssetsPath/<폴더>` 를 등록 |
| `Assets/` 밖(예: `Build/Content/Main`) | 임포트되지 않고 플레이어에도 들어가지 않습니다 — 서버에 올리거나([CDN](#cdn-에서-콘텐츠-받기)) 직접 복사 |

출력 폴더는 비어 있거나 이전 콘텐츠 빌드여야 합니다: 다른 것이 든 폴더는 손대지 않고 빌드가 실패합니다. 엔진이 돌기
전에 실패한 빌드는 이전 출력을 그대로 남깁니다.

### 설정 예

**모두 앱 안에** — 가장 단순한 경우:

| 필드 | 값 |
|---|---|
| Output path | `Assets/StreamingAssets/MainContent` |
| Archive | `None`(또는 더 작은 파일을 원하면 `Packed` + `LZ4`) |

```csharp
var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
ContentService.RegisterDirectory(path);                      // Archive = None
// await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

**CDN 에서 받기** — 앱을 다시 빌드하지 않고 갱신:

| 필드 | 값 |
|---|---|
| Output path | `Build/Content/Main` |
| Archive | `Packed`, Compression `LZ4`, Max archive MB `20` |

빌드마다 출력 폴더를 올리고, 시작할 때 받습니다 — [CDN 에서 콘텐츠 받기](#cdn-에서-콘텐츠-받기) 참고.

**본편과 내려받는 팩** — [프로파일 여러 개](#프로파일-여러-개-따로-등록하는-콘텐츠-디렉터리)처럼 각각 프로파일 하나씩.

### 흔한 빌드 오류

| 메시지 | 고치는 법 |
|---|---|
| `source root '…' is not a folder` | 폴더를 만들거나 *Source root* 를 고치세요 |
| `duplicate name '…': … / …` | 두 파일 중 하나의 이름을 바꾸거나, 프로파일을 `PathWithExtension` 으로 |
| `'…' is a ContentCatalog under the source root` | 카탈로그 에셋을 루트 밖으로 옮기세요 |
| `categories '…' and '…' overlap` | *(whole root)* 행이나 안쪽 하위 폴더 행을 지우세요 |
| `output folder '…' is neither empty nor a previous content build` | *Output path* 를 빈 폴더로 하거나 폴더를 비우세요 |
| `… asset(s) under '…' belong to no category` (경고) | 그 폴더의 카테고리를 추가하세요 — 그 에셋들은 빌드되지 않았습니다 |
| `'…' is a V1 sprite atlas under the source root` | 아틀라스를 Sprite Atlas V2 로 옮기세요 — [알아 둘 점](#알아-둘-점) 참고 |
| `profile is for … but the active build target is …` | 플랫폼을 바꾸거나 *Target* 을 `NoTarget` 으로 |

### 명령줄로 빌드

CI 에서는 배치 모드로 프로파일을 빌드합니다:

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget Android -logFile build.log \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main
```

| 옵션 (`-contentlink.<키> <값>`) | 값 |
|---|---|
| `profile` | 프로파일 이름, 또는 모든 프로파일이면 `all`. 생략하면 활성 프로파일 |
| `output`, `source` | 이번 실행의 경로를 덮어씀 |
| `archive` | `None` · `Engine` · `Packed` |
| `compression` | `Uncompressed` · `LZ4` · `LZMA` |
| `maxArchiveMB` | 1 이상의 정수 |
| `contentVersion` | 이번 실행의 Packed 빌드에 찍을 콘텐츠 버전 |

덮어쓰기는 그 실행에만 적용되고 설정 파일은 바뀌지 않습니다. 빌드 실패, 모르는 옵션이나 프로파일이면 종료 코드
**1** 로 끝납니다. 다른 에디터에서 프로젝트를 먼저 닫으세요: 배치 모드에는 프로젝트 잠금이 필요합니다.

`xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` 는 같은 옵션을 받아 카탈로그만 씁니다 — CI 가 플레이어 빌드
전에 최신 카탈로그만 필요할 때 씁니다. 이 저장소에서는 `tools/build-content.sh` 가 두 호출을 감쌉니다.

에디터 스크립트에서는 `ContentBuildRunner.Build(profile)` 과 `ContentBuildRunner.BuildAll()` 이 같은 일을 하고
`ContentBuildResult` 를 돌려줍니다.

### 빌드 뒤

후처리기는 그것을 추가한 프로파일의 빌드마다 빌드 뒤에 돕니다 — 출력을 올리거나 복사할 자리입니다. Editor
어셈블리에 두세요:

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

그다음 빌드 창의 **Post-processors** 에 추가합니다. 여러 개면 거기 적힌 차례로 돕니다. 예외를 던진 단계는 빌드를
실패시키고 출력을 그대로 남깁니다.

## 로드

### 먼저 등록

모든 콘텐츠 디렉터리를 시작할 때 한 번, **첫 로드 전에** 등록합니다:

```csharp
int dir = ContentService.RegisterDirectory(path);                      // Archive = None 또는 Engine
// int dir = await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

둘 다 디렉터리 id 를 돌려줍니다(폴더를 쓸 수 없으면 `-1` 과 오류 로그 하나). 같은 폴더를 두 번 등록하면 거부됩니다.
`ContentService.UnregisterDirectory(dir)` 로 다시 뺍니다 — 그 디렉터리의 에셋을 아직 쥐고 있으면 거부됩니다.

### 스코프

모든 로드는 **스코프**에 들어갑니다. 스코프가 끝나면 그것이 로드한 것이 해제됩니다.

| 스코프 | 얻는 법 | 끝나는 때 |
|---|---|---|
| GameObject | `this.Scope()` / `gameObject.Scope()` | GameObject 가 파괴될 때 |
| Scene | `ContentScope.ForScene(scene)` | 씬이 언로드될 때 |
| Explicit | `parent.CreateChild("Stage3")` | `Dispose()` 를 부를 때 — 또는 부모가 끝날 때 |
| Root | `ContentScope.Root` | 앱이 끝날 때 |

```csharp
// GameObject — 일상적인 경우
var sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");

// Scene — 씬이 사는 만큼
var sky = await ContentScope.ForScene(gameObject.scene).LoadAsync<Material>("Materials/Sky");

// Explicit — 스테이지 · 화면 · 팝업: 언제 끝낼지 직접 정한다
var stage = ContentScope.Root.CreateChild("Stage3");
await stage.LoadAsync<AudioClip>("Audio/boss");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... 스테이지 진행 ...
stage.Dispose();   // 스테이지가 로드한 것이 전부 돌아간다

// Root — 앱 전체 동안
var font = await ContentScope.Root.LoadAsync<Font>("Fonts/Main");
```

모든 스코프에서:

```csharp
var mat  = scope.Load<Material>("Materials/Steel");   // 동기
bool has = scope.Contains("Icons/sword");             // 이미 여기 로드됨: 다시 로드해도 공짜
scope.Release("Icons/sword");                         // 선택: 일찍 돌려주기
```

- 한 스코프에 같은 이름을 두 번 로드해도 한 번만 쥡니다 — 매 프레임 로드해도 괜찮습니다.
- 여러 스코프가 같은 이름을 로드할 수 있습니다: 에셋은 한 번 로드되고, 마지막 스코프가 놓을 때 해제됩니다.
- 다른 스코프에 에셋을 넘기려면 `source.ShareTo(target, name)` 이나 `target.Adopt(asset)` 을 쓰세요.

### 인스펙터 필드: 링크

**링크**는 이름을 쥡니다. 인스펙터에서 소스 루트 아래의 에셋을 끌어다 놓으세요:

```csharp
public ContentLinkSprite icon;              // ContentLinkGameObject, ContentLinkTexture2D 도 있음
public ContentLink<AudioClip> music;        // 어떤 에셋 형식이든

image.sprite = await icon.LoadAsync(this);  // 이 GameObject 의 스코프로
var clip = music.Load(this);                // 동기

// 아이템 교체: 옛것을 돌려주고 새것을 로드
icon.Release();
icon.SetName("Icons/shield", "shield");
image.sprite = await icon.LoadAsync(this);
```

필드의 색이 상태를 알려 줍니다: 보통 — 정상, **호박색** — 아직 카탈로그에 없음(**Update catalogs** 를 누르세요),
**빨강** — 그 이름이 필드가 담을 수 없는 것을 로드함.

### 인스펙터 필드: GUID 참조

**참조**는 이름 대신 에셋의 GUID 를 쥡니다: 파일을 옮기거나 이름을 바꿔도 계속 동작합니다.

```csharp
public ContentRef<Sprite> icon;
public ContentSceneRef level;

image.sprite = await this.Scope().LoadAsync(icon);
var scene = await this.Scope().LoadSceneAsync(level);
```

같은 에셋의 참조와 이름은 같은 객체를 로드합니다: `Release(icon)` 은 `LoadAsync<Sprite>("Icons/sword")` 가 로드한 것도
해제합니다.

### 프리팹 인스턴스

```csharp
var cat = await this.Scope().InstantiateAsync("Prefabs/Cat", parent);
```

프리팹은 인스턴스가 사는 동안 로드된 채로 있고, 인스턴스를 파괴하면 해제됩니다. `Object.Instantiate(cat)` 으로 만든
복제본도 프리팹을 붙잡아 두므로 스포너 클래스가 필요 없습니다.

### 씬

```csharp
// 스코프가 소유: 스코프가 끝나면 언로드 (additive)
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
this.Scope().ReleaseScene(arena);   // 선택: 일찍 언로드

// 인스펙터 필드에서
public ContentSceneLink level;
var scene = await level.LoadAsync(this);

// 소유자 없음, 모든 모드 — SceneManager.UnloadSceneAsync 로 직접 언로드
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

### Resources 폴더

`res:` 로 시작하는 이름은 플레이어의 `Resources` 폴더에서 로드합니다 — 같은 스코프와 필드를 거치므로 프로젝트를
`Resources.Load` 에서 한 호출씩 옮겨 올 수 있습니다:

```csharp
var boot  = this.Scope().Load<TextAsset>("res:Config/boot");                 // Resources/Config/boot.txt
var logo  = await this.Scope().LoadAsync<Sprite>("res:UI/logo[logo]");       // 스프라이트: [그 이름]
var enemy = await this.Scope().InstantiateAsync("res:Prefabs/Enemy");
```

프리팹과 그것이 쓰는 에셋은 `ContentService.SweepResources()` 로만 메모리에서 나갑니다 — 로딩 화면처럼 끊김이 괜찮은
곳에서 부르세요.

### 원시 핸들

수명을 스스로 관리하는 코드(풀 · 스트리밍)를 위해:

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");
var tex = h.Asset;
h.Dispose();   // 돌려주기 — 또는 스코프에 넘기기: scope.Attach(h)
```

## 해제

대부분의 코드는 `Release` 를 부르지 않습니다:

| 상황 | 할 일 |
|---|---|
| 컴포넌트 · 팝업 · 스테이지 · 씬의 콘텐츠 | 없음 — 알맞은 스코프에 로드 |
| 에셋이 많은 스테이지나 화면 | Explicit 스코프 하나, 스테이지가 끝나면 `Dispose()` |
| 셀을 재사용하는 스크롤 목록 | 셀마다 자식 스코프 하나, 셀을 재사용할 때 Dispose |
| 장비 · 스킨 교체 | 새것을 로드하기 전에 `Release(name)` 이나 `link.Release()` |
| 다른 시스템에 에셋 넘기기 | `ShareTo(target, name)` 이나 `target.Adopt(asset)` |

**어떻게 동작하나.** 스코프가 끝나면 — GameObject 파괴, 씬 언로드, `Dispose()` 호출, 앱 종료 — 그것이 로드한 것을 모두
놓습니다. 아무도 쥐지 않는 에셋은 **프레임마다 한 번, 프레임 끝에서** 해제됩니다. 여러분의 코드가 모두 돈 뒤이고, 코드
중간에는 일어나지 않습니다. 한 프레임 안에서 놓았다가 다시 로드하면 아무것도 다시 로드되지 않습니다.

**잊은 스코프.** 씬이 언로드되거나 앱이 끝날 때까지 열려 있는 Explicit 스코프는 대신 끝내지고, 그 경로와 함께 경고로
남습니다(`Root/Field/ForgottenPopup`). 어디서 만들었는지 보려면 `ContentService.CaptureStackTrace = true` 로 두세요.

## CDN 에서 콘텐츠 받기

`Packed` 프로파일의 출력은 캐시 폴더로 내려받아 거기서 등록할 수 있습니다.

**1. 빌드하고 올린다.** 출력 경로가 `Assets/` 밖인 `Packed` 프로파일을 씁니다. 빌드마다 출력 폴더 전체 — 아카이브,
`archives.json`, `archives.hash` — 를 플랫폼과 앱 버전마다 URL 하나에 올립니다. 아카이브 이름에는 내용의 해시가 들어
있어 영구히 캐시해도 되고, `archives.json` 과 `archives.hash` 는 CDN 에서 캐시 시간을 짧게 주세요.

**2. 시작할 때 확인 · 받기 · 등록:**

```csharp
var options = new ContentRemoteOptions
{
    BuiltInDirectory = Path.Combine(Application.streamingAssetsPath, "MainContent"),   // 선택: 앱이 함께 싣는 사본
    RetryCount = 3,
    Timeout = 30,
};
var remote = await ContentRemote.CheckAsync("Main", $"https://cdn.example.com/content/{platform}/Main", options);

if (remote.BytesToDownload > 0)
    ShowDownloadSize(remote.BytesToDownload);

if (await remote.DownloadAsync())   // 진행은 remote.Status 를 폴링
{
    await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
    remote.CleanCache();            // 새 빌드가 더는 쓰지 않는 아카이브 삭제
}
else
    Debug.LogError(remote.Error);
```

- 캐시에 없는 아카이브만 받습니다. 끊긴 받기는 이어지고, MD5 가 맞지 않는 파일은 절대 쓰이지 않습니다.
- 오프라인이면 캐시가 마지막으로 받은 것, 그다음 `BuiltInDirectory` 로 물러섭니다. 어느 쪽을 썼는지는 `remote.Source`,
  그 까닭은 `remote.Error` 가 알려 줍니다.
- 앱이 이미 싣고 있는 아카이브(`BuiltInDirectory`)는 받지 않고 복사합니다.
- 한 프로파일의 캐시 폴더 **또는** 내장 폴더 중 하나만 등록하세요. 둘 다는 안 됩니다: 엔진이 같은 빌드의 두 번째
  디렉터리를 거부합니다.
- 폴더가 등록된 채로 받아도 안전합니다. 다음에 그 폴더를 등록할 때 반영됩니다.
- 에디터에서 시뮬레이션이 켜져 있으면 확인이 요청 없이 프로젝트에서 답합니다. 실제 받기를 해 보려면 시뮬레이션을
  끄세요.

| 옵션 | 기본 | 뜻 |
|---|---|---|
| `CacheRoot` | `persistentDataPath/ContentLink` | 캐시가 놓일 곳: `<CacheRoot>/<profile>` |
| `BuiltInDirectory` | 없음 | 앱이 함께 싣는 Packed 빌드 |
| `Timeout` | `0`(없음) | 새 바이트 없이 이만큼 초가 지나면 요청을 다시 시도 |
| `RetryCount` | `0` | 요청마다 재시도 횟수 |
| `VerifyCachedFiles` | `false` | 이미 캐시된 파일의 MD5 도 검사 |
| `WebRequestOverride`, `TransformUrl` | 없음 | 모든 요청(헤더)이나 URL(서명)을 조정 |

## 로컬라이즈 콘텐츠

로케일은 언어가 바꾸는 파일만 같은 경로에 담은 두 번째 프로파일입니다. 본편 뒤에 등록하면 둘이 함께 가진 이름은
로케일의 파일이 답하고, 나머지 이름은 본편 그대로입니다.

| 프로파일 | 소스 루트 | 출력 경로 |
|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` |
| `Main.ko` | `Assets/Content.ko` | `Assets/StreamingAssets/MainContent.ko` |

`Assets/Content.ko/Icons/flag.png` 가 `Assets/Content/Icons/flag.png` 를 대신합니다 — 둘 다 `Icons/flag` 입니다. 로케일
루트는 본편 루트 **옆에** 두고, 두 프로파일의 이름 스타일을 같게 하세요.

```csharp
static string Dir(string name) => Path.Combine(Application.streamingAssetsPath, name);

ContentService.RegisterDirectory(Dir("MainContent"));      // 본편 먼저
ContentService.RegisterDirectory(Dir("MainContent.ko"));   // 그다음 로케일, 첫 로드 전에

var flag = await this.Scope().LoadAsync<Sprite>("Icons/flag");   // 한국어 깃발
```

**실행 중 언어 바꾸기.** 로케일이 바꾸는 것은 스코프 하나에 로드해, 바꿀 때 한꺼번에 놓을 수 있게 합니다:

```csharp
ContentScope localized = ContentScope.Root.CreateChild("Localized");
int locale = -1;

bool SwitchLocale(string code)   // "ko", "ja", … — 본편만이면 null
{
    localized.Dispose();                                    // 옛 로케일의 에셋을 놓는다
    if (locale >= 0 && !ContentService.UnregisterDirectory(locale))
        return false;                                       // 아직 어딘가 쥐고 있다 — 오류가 무엇인지 알려 준다
    locale = code == null ? -1 : ContentService.RegisterDirectory(Dir("MainContent." + code));
    localized = ContentScope.Root.CreateChild("Localized");
    return true;                                            // 다시 로드: 이름이 이제 새 언어로 답한다
}
```

Unity Localization 패키지를 쓴다면 문자열 번역은 그 패키지에 맡기고, `LocalizationSettings.SelectedLocaleChanged` 에서
`SwitchLocale` 을 부르세요.

## 에디터 시뮬레이션

*Window > ContentLink > Editor Simulation*(기본 켬). Play Mode 에서 프로파일의 출력 폴더를 등록하면 그 프로파일을
**프로젝트가 대신**합니다: 추가 · 수정 · 삭제한 에셋이 그대로 보이고, 빌드할 것이 없습니다. 폴더가 아예 없어도 됩니다.

- 플레이어처럼 등록은 여전히 필요합니다.
- 디스크에 아무것도 쓰지 않습니다.
- 에디터에서는 해제한 에셋이 메모리에 남습니다 — 메모리와 해제 동작은 시뮬레이션을 끄고 확인하세요.
- 아카이브를 쓰지 않습니다. 실제 빌드 · 아카이브 · 받기를 시험하려면 시뮬레이션을 끄세요.
- Play Mode 가 끝나면 콘솔이 로드했지만 아직 어느 카탈로그에도 없는 이름을 알려 줍니다: 출하 전에 빌드하세요.

## 진단

- *Window > ContentLink > Dump Scope Tree*(Play Mode), 또는 `ContentScope.DumpTree()`:

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```

- `ContentService.Snapshot(list)` — 지금 쥐고 있는 모든 참조: 그 이름, 잡은 프레임, 그리고 잡은 곳
  (`ContentService.CaptureStackTrace = true` 일 때)
- `ContentService.LiveAssetCount` — 쥐고 있는 에셋 수
- `ContentScope.DefaultWarnThreshold` — 한 스코프가 이만큼보다 많은 에셋을 쥐면 경고
- 빌드 창이 마지막 빌드를 보여 주고, 콘솔에는 빌드마다 `[ContentLink] built …` 한 줄이 남습니다

## Addressables · Resources 에서 옮겨 오기

| Addressables | ContentLink |
|---|---|
| 그룹과 번들 | 프로파일과 카테고리, `Packed` 아카이브 |
| `Addressables.LoadAssetAsync` + `Release` | `scope.LoadAsync` — 해제는 자동 |
| `AssetReference` / `AssetReferenceSprite` 필드 | `ContentRef<T>` — 필드 형식만 바꾸면 저장된 GUID 가 그대로 이어짐 |
| `ref.LoadAssetAsync()` + `ref.ReleaseAsset()` | `this.Scope().LoadAsync(ref)` |
| `InstantiateAsync` + `ReleaseInstance` | `scope.InstantiateAsync` — 인스턴스를 파괴 |
| *Use Asset Database* 플레이 모드 | 에디터 시뮬레이션 |
| `CheckForCatalogUpdates` / `UpdateCatalogs` | `ContentRemote.CheckAsync` |
| `GetDownloadSizeAsync` / `DownloadDependenciesAsync` | `remote.BytesToDownload` / `remote.DownloadAsync()` |
| `ClearDependencyCacheAsync` | `ContentRemote.ClearCache(profile)` / `remote.CleanCache()` |

`AssetReference` 에서 넘어온 `ContentRef<T>` 는 에셋이 콘텐츠 루트 아래에 있어야 합니다(그리로 옮겨도 GUID 는
유지됩니다).

| Resources | ContentLink |
|---|---|
| `Resources.Load<TextAsset>("Config/boot")` | `scope.Load<TextAsset>("res:Config/boot")` |
| `Resources.Load<Sprite>("UI/logo")` | `scope.Load<Sprite>("res:UI/logo[logo]")` |
| `Resources.UnloadAsset(asset)` | 없음 — 스코프가 해제 |
| `Resources.UnloadUnusedAssets()` | `ContentService.SweepResources()` |

## 알아 둘 점

- **메인 스레드 전용.** 로드 · 등록 · 취소는 메인 스레드에서.
- **빌드 하나에 플랫폼 하나.** 한 플랫폼용으로 빌드한 콘텐츠 디렉터리는 다른 플랫폼에서 로드되지 않을 수 있습니다.
- **스프라이트 아틀라스**는 *Project Settings > Editor > Sprite Packer > Mode* 가 Sprite Atlas V2 여야 합니다. 옛(V1)
  `.spriteatlas` 파일은 콘텐츠 루트 아래에서 거부됩니다 — V2 로 옮기세요.
- 서브에셋은 **스프라이트만** 로드할 수 있습니다. 모델 파일 안의 메시는 따로 로드할 수 없습니다.
- **씬은 공유되지 않습니다.** 씬을 두 번 로드하면 인스턴스가 둘 생기고, 각각 따로 언로드됩니다.
- **비활성 복제본.** 비활성으로 만든 복제본은 처음 깨어날 때까지 아무것도 쥐지 않습니다. 원본이 먼저 파괴될 수 있다면
  `Instantiate` 바로 뒤에 `clone.Scope()` 를 부르세요.
- **참조와 이름은 한 자리를 씁니다.** 하나를 해제하면 다른 것도 해제됩니다. 두 필드를 따로 해제해야 하면 링크나 자식
  스코프를 쓰세요.
- **콘텐츠 루트와 `Resources` 폴더를 떨어뜨려 두세요.** 양쪽에 든 파일은 이름 둘로 두 번 빌드됩니다.
- **콘텐츠 씬이 로드되는 도중에 Play Mode 를 멈추지 마세요** — 6000.6 에디터가 ContentLink 유무와 관계없이 그 경우
  크래시할 수 있습니다.
- **에디터에서는 `Resources.UnloadAsset` 이 아무것도 언로드하지 않습니다.** `res:` 에셋의 해제는 플레이어에서 확인하세요.

## 라이선스

Apache License 2.0
