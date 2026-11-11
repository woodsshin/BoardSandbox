# Board Sandbox Engine

Unity 6.3 기반 보드게임 플랫폼(테이블탑 시뮬레이터 방식)입니다. 게임 규칙은 Luau 스크립트 모드로 작성하고, Unity(C#)는 규칙을 실행·검증하며 결과를 3D 보드로 보여 줍니다. 기본으로 오목(Gomoku) 모드가 들어 있습니다.

- **모드로 게임 추가**: `config.json`과 `.luau` 파일이 든 폴더 하나가 게임 하나입니다.
- **게임 선택 화면**: 앱에 들어 있는 게임과 로컬 폴더의 게임을 한 목록에서 검색하고 고릅니다. 모드는 출처(`Built-in`, `Local`, `Workshop`)를 갖고, 출처별로 모드를 읽어 오는 부분은 `IModSource`로 분리돼 있습니다.
- **호스트 권한**: 마우스, 콘솔, 컴퓨터 플레이어의 모든 입력은 `GameSession.Submit` 하나를 거쳐 규칙 스크립트(`rules.command`)의 검증을 통과해야 반영됩니다. 거부된 입력은 코드와 사유가 붙은 결과로 돌아옵니다.
- **인터셉터**: `ICommandInterceptor`로 입력이 규칙에 닿기 전에 막거나(`TurnGuard`), 처리 뒤 결과를 기록할 수 있습니다(`RejectionLog`).
- **시간 되감기**: 수마다 스냅샷을 남겨(최대 4096단계) 되돌리기, 다시 실행, 임의 지점 이동을 지원합니다. 모드가 `snapshot`과 `restore`를 제공해야 합니다.
- **헤드리스 시뮬레이션**: 화면 없이 별도 VM에서 무작위 대국을 대량으로 돌려 승패, 무승부, 평균 수를 집계합니다. 같은 모드, 옵션, 시드이면 결과가 같습니다.
- **콘솔**: 텍스트 명령으로 게임을 진행하고 상태를 조회합니다. 스크립트 로그도 여기에 나옵니다.

## 요구 사항

| 항목 | 내용 |
|---|---|
| Unity | 6000.3 (6.3 LTS) |
| 플랫폼 | Windows 10 이상, x64 (에디터와 Standalone 빌드) |
| 렌더 파이프라인 | Built-in, URP (셰이더에 `LightMode` 태그가 없어 둘 다에서 그려지도록 작성) |
| UI와 입력 | IMGUI(`OnGUI`). `UnityEngine.Input`, uGUI, UI Toolkit은 쓰지 않습니다 |

네이티브 라이브러리 `bse_luau.dll`(Luau 0.745를 정적으로 포함, 외부 의존은 KERNEL32와 Windows UCRT뿐)은 `Assets/BoardSandbox/Plugins/Windows/x86_64/`에 들어 있어 따로 설치할 것이 없습니다. 직접 빌드하려면 [네이티브 라이브러리 빌드](#네이티브-라이브러리-빌드)를 보세요.

## 빠른 시작

1. Unity Hub에서 **Add > Add project from disk**로 이 폴더를 열고 6000.3.x 에디터를 선택합니다.
2. 메뉴 **Board Sandbox > Check Native Plugin**을 실행해 Luau 버전이 표시되는지 확인합니다.
3. 아무 씬에서 **Play**를 누르면 `BoardSandboxApp`이 자동으로 만들어집니다. 읽은 게임이 하나뿐이면(들어 있는 구성은 오목 하나) 바로 시작하고, 둘 이상이면 게임 선택 화면이 열립니다([시작 규칙](#시작-규칙)). 씬에 카메라가 없으면 `Board Camera`도 만듭니다.
4. 씬 파일이 필요하면 **Board Sandbox > Create Demo Scene**을 실행합니다.

| 에디터 메뉴 | 동작 |
|---|---|
| Board Sandbox/Check Native Plugin | 네이티브 라이브러리를 불러 Luau 버전을 보여 줍니다. 실패하면 사유를 보여 줍니다 |
| Board Sandbox/Create Demo Scene | 기본 카메라와 조명, `BoardSandboxApp`이 든 `Assets/BoardSandbox/Scenes/Board.unity`를 만들고 Build Settings의 씬 목록을 이 씬 하나로 설정합니다 |
| Board Sandbox/Fix Native Plugin Settings | `bse_luau.dll`의 플러그인 설정(에디터와 Windows x64에서만 사용)을 다시 적용합니다. 같은 설정을 에디터가 스크립트를 불러올 때마다 자동으로 확인합니다 |

자동 시작은 환경 변수 `BSE_AUTOSTART=0` 또는 스크립팅 정의 `BSE_NO_AUTOSTART`로 끕니다. 씬에 `BoardSandboxApp`이 이미 있으면 자동으로 만들지 않습니다.

`BoardSandboxApp`의 인스펙터 값:

| 필드 | 기본값 | 설명 |
|---|---|---|
| Initial Mod | 비어 있음 | 시작할 게임의 id. 비어 있을 때와 찾지 못할 때의 동작은 [시작 규칙](#시작-규칙)을 보세요 |
| Extra Mod Folders | 비어 있음 | 모드를 찾을 추가 폴더 (출처 `Local`) |
| Start Game | 켜짐 | 시작할 게임이 정해졌으면 바로 새 게임을 엽니다. 끄면 게임만 고르고 `New game`을 기다립니다 |
| Ui Scale | 0 | 0이면 화면 높이에 맞춰 자동 (1~2.5배, 0.25 단위) |

## 조작

| 입력 | 동작 |
|---|---|
| 왼쪽 클릭 | 말 놓기. 모드가 `client.click.action`을 지정했고 사람 좌석의 차례일 때만 동작하며, 현재 차례 좌석으로 `{ action, x, y }` 명령을 제출합니다 |
| 왼쪽 드래그, 가운데 버튼 드래그 | 카메라 이동 |
| 오른쪽 드래그 | 카메라 회전 |
| 마우스 휠 | 확대/축소 |
| `F` | 보드 전체가 보이도록 카메라 맞춤 |
| `G` | 게임 선택 화면 열기 |
| `N` | 새 게임 |
| `Ctrl+Z`, `←` | 되돌리기 |
| `Ctrl+Y`, `Ctrl+Shift+Z`, `→` | 다시 실행 |
| `` ` ``, `F1` | 콘솔 열기/닫기 (`Esc`는 콘솔이 열려 있을 때 닫음) |

텍스트 입력란에 포커스가 있거나 게임 선택 화면이 열려 있으면 단축키는 동작하지 않습니다. `client.click.action`이 없는 모드에서는 보드를 클릭해도 아무 일도 일어나지 않으며, 왼쪽 패널에 안내가 표시됩니다. 이때는 콘솔의 `play`를 씁니다.

## 화면 구성

화면 문자열은 영어이고, 모드가 보내는 문자열(좌석 이름, 상태 메시지 등)은 한글 글꼴(맑은 고딕 등)이 설치돼 있으면 한글로 표시됩니다.

- **상단 배너**: 차례(`<좌석> to move`, 컴퓨터 차례면 `(computer)` 추가) 또는 결과(`<좌석> wins`, `Draw`)를 좌석 색 표시와 함께 보여 줍니다.
- **왼쪽 패널**: 모드 이름과 설명, `Games (G)` 버튼(게임 선택 화면 열기), 좌석별 `Human | Computer`, `New game` / `Undo` / `Redo`, 기록 슬라이더(기록이 2단계 이상일 때), `Options (apply to the next game)`.
  - 옵션은 모드의 `options` 기본값을 보여 줍니다. 불리언은 체크박스, 숫자와 문자열은 입력란이고, 그 밖의 값은 읽기 전용입니다. 바꾼 값은 다음 새 게임과 Simulation 패널에 적용됩니다.
  - `Computer` 좌석은 모드의 무작위 합법 수를 0.45초 간격으로 둡니다. 모드가 `random_command` 또는 `legal_commands`를 제공하지 않으면 선택할 수 없습니다(안내 토스트). 컴퓨터가 둘 수를 못 찾거나 수가 거부되면 그 좌석은 `Human`으로 돌아갑니다. 되돌린 시점이 컴퓨터 차례면 컴퓨터가 다시 둡니다.
- **오른쪽 도구 열**: `Fit view`, `Console`, `Simulation`.
  - **Simulation 패널**: `Games`(기본 1000, 1~1,000,000)와 `Seed`(기본 1)를 정해 `Run`하면 무작위 대국을 백그라운드로 돌리고 진행률과 결과를 보여 줍니다. `Cancel`로 중단합니다. 화면에는 아무것도 그리지 않고, 진행 중인 게임에도 영향이 없습니다.
- **콘솔 창**: 화면 아래. 명령 입력(`↑`/`↓`로 입력 기록 이동)과 스크립트 로그(`bse.log` 등)가 함께 나옵니다.
- **토스트**: 규칙이 거부한 입력(`메시지 [CODE]`), 스크립트 오류, 모드 로드 오류, 모드의 `bse.status` 메시지.
- **게임 선택 화면**: 화면 전체를 덮는 창입니다. 왼쪽 패널의 `Games (G)` 버튼이나 `G`로 열고, 시작할 게임이 정해지지 않았으면 앱이 시작할 때 저절로 열립니다.
  - 검색란은 이름, id, 설명에서 찾으며 공백으로 나눈 단어가 모두 들어 있어야 합니다(대소문자 무시). 게임이 둘 이상의 출처에 있으면 `All`과 게임이 있는 출처(`Built-in`, `Local`, `Workshop`)로 거르는 필터가 나타납니다.
  - 목록은 `Built-in`, `Local`, `Workshop` 순서이고 출처 안에서는 이름순입니다. 행에는 색 타일, 이름, 인원과 출처가 보이고 진행 중인 게임에는 `Playing`이 붙습니다. 행이 60개를 넘으면 보이는 행만 그립니다.
  - 오른쪽에는 고른 게임의 이름, 출처, 인원, 버전, 설명, 좌석 이름, 옵션 기본값이 나옵니다.
  - `Play`는 고른 게임으로 새 게임을 시작하며 진행 중이던 게임은 끝납니다. 고른 게임이 이미 진행 중이면 `Resume`(그대로 돌아가기)과 `New game`이 나옵니다. `Rescan`은 폴더를 다시 읽습니다. 읽지 못한 모드와 id가 겹친 모드는 `Problems (n)`에서 사유를 볼 수 있습니다.
  - `↑`/`↓`로 고르고 `Enter`로 `Play` 또는 `Resume`, `Esc`로 닫습니다. 행을 더블 클릭하면 `Enter`와 같습니다.
  - 열려 있는 동안 보드 입력과 단축키는 동작하지 않고 컴퓨터 플레이어도 멈춥니다. 게임이 하나도 없으면 찾아본 폴더와 안내가 나옵니다.

## 콘솔 명령

`` ` `` 또는 `F1`로 콘솔을 열고 입력합니다. `key=value`의 값은 JSON으로 읽히면 JSON 값(숫자, `true`, 따옴표 문자열 등), 아니면 문자열입니다. 공백이 든 값은 큰따옴표로 감쌉니다. `seat`는 1부터 시작하는 좌석 번호이고, 생략하면 지금 차례인 좌석입니다.

| 명령 | 설명 |
|---|---|
| `help`, `?` | 명령 목록 |
| `new [key=value ...]` | 옵션을 지정해 새 게임 시작 (모드 기본값 위에 덮어씀) |
| `state` | 모드, 옵션, 차례 또는 결과, 화면 객체 수, 기록 위치 |
| `board` | 격자를 텍스트로 그리기 |
| `play [seat] <action> [k=v ...]` | `{ "action": <action>, k: v, ... }` 명령 제출 |
| `raw <seat> <json>` | JSON 명령을 그대로 제출 |
| `legal [seat]` | 합법 명령 목록(24개까지 표시). 모드에 `legal_commands`가 있어야 합니다 |
| `random [seat] [seed=N]` | 무작위 명령 하나 두기 |
| `ff <n> [seed=N]` | 무작위 명령을 최대 n번 두기 (게임이 끝나면 멈춤, 기본 시드 1) |
| `undo [n]`, `redo [n]`, `goto <i>`, `history` | 기록 탐색 (`history`는 최근 24개 표시) |
| `save <name>`, `load <name>` | 이름 붙인 스냅샷. 메모리에만 보관합니다 |
| `sim <games> [seed=N] [max_moves=N] [k=v ...]` | 별도 VM에서 무작위 대국 실행. 끝날 때까지 기다립니다. 옵션을 주지 않으면 진행 중인 게임의 옵션을 씁니다 |
| `query <name> [json]` | 모드에 데이터 질의. 모든 모드에서 `view`(화면 객체 목록)와 `session`(차례, 결과)을 쓸 수 있고, 그 밖의 이름은 모드의 `rules.query`가 답합니다 |
| `eval <luau>` | Luau 실행. 개발 모드 세션에서만 쓸 수 있으며 앱의 세션은 개발 모드가 꺼져 있습니다 |
| `logs`, `stats`, `rejections` | 스크립트 로그, VM 통계(호출 수, 메모리, 마지막 호출 시간), 거부된 입력의 코드별 집계 |

```
new size=19
play place_stone x=10 y=10
board
ff 20 seed=7
undo 3
query state
rejections
sim 1000 seed=1
```

`query state`는 오목 모드가 제공하는 질의입니다. 시뮬레이션 결과는 `<N> games in <초> s (<초당 게임 수> games/s), seat 1 won <a>, seat 2 won <b>, draws <d>, aborted <x>, rejected <r>, avg moves <m>` 형식입니다. `aborted`는 결과 없이 끝난 게임(둘 곳이 없거나 `max_moves`에 닿음), `rejected`는 무작위 명령을 규칙이 거부해 멈춘 게임입니다.

## 모드

모드는 폴더 하나입니다. 아래 위치에서 하위 폴더마다 `config.json`을 찾으며, `config.json`이 없는 폴더는 건너뜁니다. 위치마다 출처가 있고 위에서부터 차례로 읽습니다. 같은 id가 이미 있으면 뒤의 것은 오류로 알리고 목록에는 앞의 것만 남기므로, `Built-in` 모드의 id는 다른 출처가 가져갈 수 없습니다. 같은 폴더가 두 번 나오면 앞의 출처로 한 번만 올립니다.

| 순서 | 위치 | 출처 |
|---|---|---|
| 1 | `Assets/StreamingAssets/Mods` (빌드에서는 `<앱>_Data/StreamingAssets/Mods`) | `Built-in` |
| 2 | 환경 변수 `BSE_MODS_DIR` (여러 개면 `;`로 구분) | `Local` |
| 3 | `BoardSandboxApp`의 Extra Mod Folders | `Local` |
| 4 | `Application.persistentDataPath/Mods` (Windows: `%USERPROFILE%\AppData\LocalLow\<회사>\<제품>\Mods`) | `Local` |

`Workshop` 출처는 목록, 필터, 정렬에서 쓸 수 있는 값이지만 이 앱에는 Workshop 폴더를 읽는 소스가 없습니다. 소스를 더하려면 `IModSource`를 구현해 `BoardSandboxApp.ModSources()`가 만드는 목록의 끝에 넣습니다([C#에서 사용하기](#c에서-사용하기)).

모드는 앱을 시작할 때와 게임 선택 화면에서 `Rescan`을 누를 때 읽습니다. 스크립트를 고친 뒤에는 `Rescan`하고 다시 `Play`해야 고친 내용으로 시작합니다. 진행 중인 게임은 `Rescan`으로 바뀌지 않으며, `New game`(`N`)은 그 게임을 읽어 온 그대로의 스크립트로 다시 시작합니다.

폴더 안의 모든 `.luau` 파일(하위 폴더 포함, 이름이 `.`으로 시작하는 파일과 폴더는 제외)이 모드 스크립트입니다. 제한은 모드당 파일 4096개, 파일당 4 MiB, 전체 32 MiB입니다. 읽지 못한 모드는 오류 토스트로 알리고 목록에서 뺍니다. 사유는 게임 선택 화면의 `Problems`에도 남습니다. 모드가 정한 문자열은 화면에 나올 때 길이를 제한합니다(토스트 300자, 왼쪽 패널의 이름 40자와 설명 600자, 게임 선택 화면의 이름 32자와 설명 1200자). 넘치는 부분은 `…`로 줄이고, 모드가 정한 문자열에는 서식 태그를 쓰지 않습니다.

### 시작 규칙

앱을 시작하면 Initial Mod와 읽은 게임 수로 첫 화면이 정해집니다.

| 조건 | 동작 |
|---|---|
| Initial Mod가 있고 그 게임이 있음 | 그 게임을 고르고, Start Game이 켜져 있으면 바로 시작합니다 |
| Initial Mod가 있는데 그 게임이 없음 | 경고 토스트(`The game '<id>' was not found.`)와 함께 게임 선택 화면을 엽니다 |
| Initial Mod가 비어 있고 게임이 하나 | 그 게임을 고르고, Start Game이 켜져 있으면 바로 시작합니다 |
| Initial Mod가 비어 있고 게임이 없거나 둘 이상 | 게임 선택 화면을 엽니다 |

고른 게임을 불러오지 못해 열린 게임이 없으면 게임 선택 화면을 엽니다.

```
Mods/
└─ gomoku/
   ├─ config.json
   └─ logic.luau
```

### config.json

| 키 | 필수 | 설명 |
|---|---|---|
| `id` | 예 | 1~64자. `a-z`, `0-9`, `_`, `-`만 쓰며 첫 글자는 영소문자나 숫자 |
| `name` | 예 | 표시 이름 |
| `api` | 예 | `1` |
| `entry` | 예 | 진입 스크립트. `/`로 구분한 상대 경로이며 `.luau`로 끝나야 합니다 (200자 이하, 절대 경로와 `..` 불가) |
| `version` | 아니오 | 기본 `0.0.0` |
| `description` | 아니오 | 게임 선택 화면과 왼쪽 패널에 표시되는 설명 |
| `players` | 아니오 | `{ "min": 2, "max": 2 }`. `min` 기본 2, `max` 기본값은 `min`, 조건은 1 ≤ min ≤ max ≤ 16 |
| `seats` | 아니오 | `[{ "name": "Black", "color": "#161616" }, ...]`. `max`개까지 읽고, 생략한 이름은 `Player N`, 생략한 색은 기본 8색 팔레트를 순환합니다. 색은 `#rrggbb` |
| `options` | 아니오 | 새 게임 옵션의 기본값 객체 |
| `client` | 아니오 | 화면 표시 힌트 ([클라이언트 힌트](#클라이언트-힌트)) |

오목의 `config.json`:

```json
{
  "id": "gomoku",
  "name": "Gomoku",
  "version": "1.0.0",
  "api": 1,
  "entry": "logic.luau",
  "players": { "min": 2, "max": 2 },
  "seats": [
    { "name": "Black", "color": "#161616" },
    { "name": "White", "color": "#f3efe6" }
  ],
  "options": { "size": 15, "win_length": 5, "exact_win": false },
  "client": {
    "board": { "style": "lines" },
    "click": { "action": "place_stone", "preview": "stone" },
    "pieces": { "stone": { "shape": "stone" } }
  }
}
```

### 규칙 스크립트

진입 스크립트는 규칙 테이블을 반환합니다. 전역 `bse`로 게임 상태를 알리고, `require("./경로")`는 호출한 파일 기준의 상대 경로(`./`, `../`)로 `경로.luau` 또는 `경로/init.luau`를 불러옵니다(모드 폴더 밖으로 나가거나 순환하면 오류).

| 함수 | 필수 | 역할 |
|---|---|---|
| `rules.new_game(options)` | 예 | 새 게임 준비. `options`는 모드 기본값 위에 사용자가 바꾼 값을 덮은 것입니다. 시작할 때 화면 객체, 차례, 결과는 비어 있으며, `bse.turn(seat)`으로 첫 차례를 지정합니다 |
| `rules.command(seat, cmd)` | 예 | 입력 검증과 적용. 성공하면 `nil`(또는 `true`)을 반환하고, 거부하면 `bse.reject(...)`를 반환합니다 |
| `rules.legal_commands(seat)` | 아니오 | 합법 명령 목록. 콘솔 `legal`에 쓰이며 `random_command`가 없을 때 무작위 선택의 후보가 됩니다 |
| `rules.random_command(seat)` | 아니오 | 합법 명령 하나(없으면 `nil`). `Computer` 좌석, `random`, `ff`, `sim`에 쓰입니다. 둘 다 없으면 이 기능들을 쓸 수 없습니다 |
| `rules.snapshot()`, `rules.restore(data)` | 아니오 (둘 다) | 규칙 상태를 JSON으로 직렬화되는 값으로 저장하고 복원합니다. 둘 다 있어야 되돌리기, 다시 실행, `goto`, `save`, `load`가 켜집니다 |
| `rules.query(name, args)` | 아니오 | 콘솔 `query`에 답할 값을 반환합니다 |

- **거부는 상태를 바꾸기 전에 합니다.** 화면 객체를 바꾸거나 이벤트를 내보낸 뒤 거부를 반환하면 스크립트 오류(`INVALID_REJECTION`)로 처리됩니다.
- **스냅샷의 범위**: 엔진이 화면 객체, 차례, 종료 상태를 함께 저장하고 복원합니다. `snapshot`/`restore`에는 규칙 자신의 상태만 넣으면 됩니다. 복원할 때 화면 미러가 비워졌다가 복원 이벤트로 다시 채워지므로, 클라이언트가 읽는 사용자 정의 이벤트(`board`, `last_move`)는 `restore`에서도 다시 내보내야 합니다.
- **무작위**: 무작위는 `math.random`을 씁니다. 엔진이 시드를 `math.randomseed`로 정해 주므로 같은 시드는 같은 결과를 냅니다.
- **오류**: 스크립트가 예외를 던지거나 시간·메모리 한도를 넘으면 VM을 새로 만들어 마지막 정상 스냅샷으로 복구하고 토스트로 알립니다. `snapshot`/`restore`가 없는 모드는 복구할 수 없어 게임이 멈추고 `New game`이 필요합니다.
- **환경**: Luau 샌드박스입니다. 표준 라이브러리 테이블과 `bse`는 쓰기 금지이고 `io`, `os`, `loadstring`, `dofile`, `getfenv`, `setfenv`는 없습니다. 기본 한도는 호출당 250 ms, 부트 2000 ms, 메모리 64 MB입니다 (시뮬레이션 VM은 메모리 256 MB, 호출 60초).

### bse API

| API | 설명 |
|---|---|
| `bse.view.spawn(kind, x, y, { owner = seat, props = {...} })` | 말을 만들고 id를 반환합니다. 마지막 인자는 생략할 수 있습니다 |
| `bse.view.move(id, x, y)` | 말 이동 |
| `bse.view.set(id, changes)` | `props`에 `changes`를 합칩니다 |
| `bse.view.remove(id)`, `bse.view.clear()` | 말 하나 제거, 전체 제거 |
| `bse.view.get(id)` | `{ id, kind, x, y, owner, props }` 복사본 또는 `nil` |
| `bse.turn(seat)` | 차례 지정 |
| `bse.over({ winner = seat, reason = "...", cells = { {x, y}, ... } })` | 게임 종료. `winner`가 없거나 0이면 승자 없음(무승부) |
| `bse.emit({ t = "이름", ... })` | 사용자 정의 이벤트. `reset`, `spawn`, `move`, `set`, `remove`, `turn`, `over`, `status`는 예약된 이름입니다 |
| `bse.status(text, level)` | 토스트로 보일 메시지. `level`은 `info`(기본), `warning`(또는 `warn`), `error` |
| `bse.reject(code, message, details)` | `command`가 반환할 거부 값을 만듭니다 |
| `bse.deepcopy(value)`, `bse.is_headless()` | 깊은 복사(64단계까지), 헤드리스 여부 |
| `bse.log(...)`, `bse.warn(...)`, `bse.err(...)` | 콘솔과 `logs`에 나오는 로그 |
| `bse.json.encode(value)`, `bse.json.decode(text)`, `bse.json.null` | JSON 변환 |

인자가 잘못되면(`BAD_ARGUMENT`) 또는 없는 객체를 가리키면(`NO_SUCH_OBJECT`) 스크립트 오류가 납니다. 좌표는 1부터 시작하는 칸 번호입니다. 타입 선언은 `Native/bse_luau/luau/bse.d.luau`에 있고, `bse`의 구현은 `Native/bse_luau/luau/prelude.luau`입니다.

헤드리스 시뮬레이션(`bse.is_headless()`가 `true`)에서는 화면 갱신을 하지 않습니다. `bse.view.*`는 객체를 만들지 않고(`bse.view.get`은 `nil`), `bse.emit`과 `bse.status`는 무시됩니다. `bse.turn`과 `bse.over`는 그대로 동작하므로 승패 판정은 `bse.view`가 아니라 스크립트 자신의 상태로 해야 하고, `new_game`은 `bse.turn`을 호출해야 합니다.

### 최소 예제

`Mods/race/config.json`:

```json
{
  "id": "race",
  "name": "Race to 10",
  "api": 1,
  "entry": "main.luau",
  "players": { "min": 2, "max": 2 }
}
```

`Mods/race/main.luau` (두 사람이 번갈아 1~3을 더해 먼저 10에 닿으면 이깁니다):

```lua
--!strict
local rules = {}

local scores = { 0, 0 }
local current = 1

function rules.new_game(options: { [string]: any })
	scores = { 0, 0 }
	current = 1
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	if cmd.action ~= "add" then
		return bse.reject("UNKNOWN_ACTION", "unknown action", { action = cmd.action })
	end
	if seat ~= current then
		return bse.reject("NOT_YOUR_TURN", "it is not your turn")
	end
	local amount = cmd.amount
	if type(amount) ~= "number" or amount ~= math.floor(amount) or amount < 1 or amount > 3 then
		return bse.reject("INVALID_ARGUMENT", "amount must be 1, 2 or 3")
	end

	scores[seat] += amount
	bse.status(`seat {seat} has {scores[seat]}`)
	if scores[seat] >= 10 then
		bse.over({ winner = seat, reason = "reached 10" })
	else
		current = 3 - seat
		bse.turn(current)
	end
	return nil
end

function rules.random_command(seat: number): any
	return { action = "add", amount = math.random(1, 3) }
end

function rules.query(name: string, args: any): any
	if name == "scores" then
		return scores
	end
	return nil
end

function rules.snapshot(): any
	return { scores = scores, current = current }
end

function rules.restore(data: any)
	scores = data.scores
	current = data.current
end

return rules
```

화면 객체가 없는 예제이므로 콘솔에서 진행합니다.

```
play add amount=2
play 2 add amount=3
play add amount=5
query scores
undo
ff 20 seed=3
sim 1000 seed=1
```

기준 구현은 `Assets/StreamingAssets/Mods/gomoku`입니다. 모든 훅과 `bse.view`, `bse.emit`을 쓰며 격자 보드를 위한 클라이언트 힌트도 갖추고 있습니다.

### 클라이언트 힌트

`config.json`의 `client`는 화면 표시에만 쓰이며 코어는 해석하지 않습니다. 격자 보드(`GridBoardView`)가 다음을 읽습니다.

| 키 | 값 | 설명 |
|---|---|---|
| `board.style` | `lines`, `checker` | 선의 교차점에 말을 두는 판, 체크무늬 칸에 두는 판 (기본 `lines`) |
| `board.columns`, `board.rows` | 정수 | 보드 크기 |
| `click.action` | 문자열 | 보드를 클릭하면 `{ action = <값>, x, y }`를 제출합니다. 비우면 클릭 입력이 없습니다 |
| `click.preview` | 말 kind | 마우스를 올린 자리에 반투명으로 보여 줄 말 |
| `pieces.<kind>.shape` | `stone`, `disc`, `cube` | 말 모양. 기본은 kind가 `stone`이면 `stone`, 그 외 `disc` |

보드 크기는 모드가 `bse.emit({ t = "board", columns = n, rows = m })`로 알린 값이 가장 우선이고, 없으면 `board.columns`/`board.rows`, 그것도 없으면 말 위치에 맞춰 정합니다(최소 8, 최대 64). 열 1은 기본 시점에서 왼쪽, 행 1은 먼 쪽(화면 위쪽)입니다.

말 색은 `props.color`가 정하고(`#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, 또는 `black` `white` `red` `green` `blue` `yellow` `orange` `purple` `brown` `gray` `cyan` `pink`), 없으면 소유 좌석의 색입니다. `props.scale`(0.25~3)로 크기를 바꿉니다. 사용자 정의 이벤트 `last_move { x, y, seat }`는 마지막 수 표시에, `bse.over`의 `cells`는 승리 줄 강조에 쓰입니다. 한 화면에 그리는 말은 최대 6000개입니다.

### 정적 분석

`Native/bse_luau`를 `-DBSE_BUILD_TOOLS=ON`으로 설정해 `bse_analyze`를 빌드하면 모드 스크립트를 타입 검사할 수 있습니다.

```
bse_analyze --strict --defs=Native/bse_luau/luau/bse.d.luau Assets/StreamingAssets/Mods/gomoku/logic.luau
```

## C#에서 사용하기

`BoardSandbox.Core`는 UnityEngine을 참조하지 않으므로 콘솔 앱이나 테스트에서도 그대로 쓸 수 있습니다(`bse_luau` 네이티브 라이브러리는 필요합니다).

```csharp
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;

ModPackage mod = ModLoader.LoadDirectory("Assets/StreamingAssets/Mods/gomoku");
using (GameSession session = new GameSession(mod))
{
    session.AddInterceptor(new TurnGuard());
    session.NewGame();

    SubmitResult result = session.Submit(InputPacket.Action(1, "place_stone", "script").With("x", 8).With("y", 8));
    // result.Status: Accepted, Rejected, Vetoed, Invalid, Faulted
}

SimulationResult stats = SimulationRunner.Run(mod, new SimulationOptions { Games = 10000, Seed = 1 });

ModCatalog catalog = new ModCatalog(new IModSource[]
{
    new FolderModSource("Assets/StreamingAssets/Mods", ModOrigin.BuiltIn),
    new FolderModSource("MyMods", ModOrigin.Local)
});
using (GameHost host = new GameHost(catalog))
{
    GameLibrary library = new GameLibrary(host);
    library.SetQuery("go");          // 이름, id, 설명으로 거르기
    library.Play();                  // 고른 게임으로 새 게임 시작
}
```

- `GameSession`은 VM 하나와 화면 미러(`View`), 기록을 가집니다. 한 VM은 한 번에 한 스레드에서만 호출하세요. 서로 다른 VM은 동시에 써도 됩니다(시뮬레이션이 별도 VM을 백그라운드 스레드에서 돌립니다).
- `GameHost`는 UI가 필요로 하는 것을 묶습니다: 모드 목록(`ModCatalog`), 좌석별 사람/컴퓨터, 컴퓨터 플레이어 스케줄, 토스트, 콘솔, 거부 기록, 백그라운드 시뮬레이션. `StartGame(mod)`는 모드를 고르고 새 게임까지 시작하며, `IsRunning(mod)`는 그 모드의 같은 버전(내용 해시가 같음)이 진행 중인지 알려 줍니다.
- `ModCatalog`는 `IModSource` 목록에서 모드를 모읍니다. 항목(`ModEntry`)은 모드와 출처(`ModOrigin`: `BuiltIn`, `Local`, `Workshop`)를 가지며, `Search(query, origin)`이 이름, id, 설명으로 찾아 출처, 이름 순으로 돌려줍니다. `Refresh()`는 소스를 다시 읽고, `FindStartup(id)`는 [시작 규칙](#시작-규칙)의 게임을 고릅니다.
- `IModSource`는 `Origin`, `Description`(찾아보는 곳, 안내 문구에 쓰임), `Discover()`(`ModDiscovery`: 모드와 읽지 못한 모드의 오류)로 이루어집니다. 폴더를 읽는 `FolderModSource`가 들어 있고, `Discover()`가 예외를 던져도 오류로 보고될 뿐 다른 소스는 그대로 읽힙니다.
- `GameLibrary`는 게임 선택 화면의 상태(검색어, 출처 필터, 고른 게임, 이동, `Rescan`, `Play`)를 화면 없이 다룹니다. 화면(`GameLibraryPanel`)은 이 상태를 그리기만 합니다.
- `TurnGuard`는 차례가 아닌 좌석의 입력을 스크립트를 실행하지 않고 막는 인터셉터이며, 앱에는 기본으로 붙어 있지 않습니다. 앱은 `RejectionLog`만 붙입니다.

## 프로젝트 구조

```
Assets/
├─ BoardSandbox/
│  ├─ Runtime/Core/      Unity 비의존 C# (asmdef noEngineReferences)
│  ├─ Runtime/Client/    Unity 표현 계층: 앱, 카메라, 보드와 말 뷰, IMGUI HUD와 게임 선택 화면, 셰이더
│  ├─ Editor/            메뉴, 네이티브 플러그인 설정
│  ├─ Plugins/Windows/x86_64/   bse_luau.dll
│  └─ Tests/Editor/      NUnit 테스트 (EditMode)
└─ StreamingAssets/Mods/gomoku/   오목 모드
Native/
├─ bse_luau/             C ABI 래퍼(C++17), CMake, Luau 프렐류드, 네이티브 테스트, 분석 도구
└─ scripts/build_native.py
Tools/
├─ CoreTests/            Unity 없이 Core 테스트를 실행하는 dotnet 하네스
└─ UnityCheck/           Unity API 대비 컴파일 검사용 프로젝트
.github/workflows/ci.yml
THIRD_PARTY_NOTICES.txt
```

## 구조 개요

```
입력 (마우스, 콘솔, 컴퓨터 플레이어)
  │  InputPacket(seat, command, source)
  ▼
GameSession.Submit ─ 인터셉터 BeforeSubmit (막을 수 있음)
  │
  ▼
Luau VM: rules.command(seat, cmd) ─ 이벤트 (spawn, move, set, remove, turn, over, status, 사용자 정의)
  │
  ▼
ViewModel (이벤트로 갱신되는 미러) ─ 기록(스냅샷) ─ 인터셉터 AfterSubmit (RejectionLog)
  │
  ▼
SubmitResult (수락 / 거부 / 차단 / 무효 / 오류)   Client(GridBoardView, HUD)는 ViewModel만 읽어 그립니다
```

- **bse_luau** (네이티브): Luau VM을 C ABI(버전 1)로 감쌉니다. 모든 호출은 JSON 문자열 입출력(`bse_vm_call`)이고 메모리·시간 한도와 로그 수집을 제공합니다. 내보내는 함수는 `bse_*` 10개이며 헤더는 `Native/bse_luau/include/bse_luau.h`입니다.
- **BoardSandbox.Core**: `LuauVm`(P/Invoke), 모드 로더, 모드 출처와 카탈로그(`IModSource`, `ModCatalog`), `GameSession`(검증, 이벤트 미러, 스냅샷 기록, 오류 복구), `CommandConsole`, `SimulationRunner`, `GameHost`, `GameLibrary`.
- **BoardSandbox.Client**: `BoardSandboxApp` 하나가 일반 C# 클래스(보드 뷰, 카메라, HUD, 게임 선택 화면)를 구동하는 구성 루트입니다.

## 네이티브 라이브러리 빌드

`Assets/BoardSandbox/Plugins/Windows/x86_64/bse_luau.dll`을 다시 만들 때 사용합니다. Unity가 DLL을 잠그므로 에디터를 닫고 빌드하세요. Luau는 CMake가 GitHub에서 고정된 커밋(릴리스 0.745)을 받아 옵니다. 결과물은 `Native/build/<대상>`에 생기고, 테스트까지 통과한 DLL만 프로젝트에 복사됩니다.

Windows (Visual Studio C++ 도구, CMake, Python 3):

```
python Native\scripts\build_native.py host --run-tests
```

Linux, macOS에서 Windows DLL 만들기 (zig, CMake, Ninja, Python 3. 테스트 실행에는 wine64):

```
pip install ziglang
python3 Native/scripts/build_native.py windows-x64 --run-tests
```

옵션: `--luau-src <경로>`는 이미 받아 둔 Luau 소스를 사용, `--no-install`은 프로젝트에 복사하지 않습니다. 스크립트 없이 CMake만 쓸 때(CI와 같은 방식):

```
cmake -S Native/bse_luau -B Native/build/ci -A x64
cmake --build Native/build/ci --config Release --parallel --target bse_luau bse_luau_tests
ctest --test-dir Native/build/ci -C Release --output-on-failure
```

## 테스트

| 종류 | 실행 방법 | 범위 |
|---|---|---|
| Unity EditMode | **Window > General > Test Runner > EditMode** | `Assets/BoardSandbox/Tests/Editor`: JSON, Luau VM, 모드 로딩, 모드 출처와 카탈로그, 게임 선택 상태, 세션, 콘솔, 시뮬레이션, 호스트, 오목 규칙, 기하와 뷰 계산 |
| dotnet 하네스 | `Tools\CoreTests\run.cmd [이름 필터]` (Windows), `Tools/CoreTests/run.sh [이름 필터]` | 같은 테스트를 Unity 없이 실행. `Tools/UnityCheck/.reference/UnityEngine.dll`이 없으면 UnityEngine이 필요한 테스트(기하, 뷰)는 빼고 실행합니다 |
| 네이티브 | `bse_luau_tests` (`build_native.py --run-tests`, `ctest`) | ABI, VM 수명, 스냅샷, 뷰 API, 부트 실패, `require`, 샌드박스, 로그, JSON, 한도, 시뮬레이션, 멀티스레드 |
| CI | `.github/workflows/ci.yml` (`windows-latest`) | MSVC로 네이티브 빌드와 테스트, DLL 설치, 하네스 실행, DLL 산출물 업로드 |

하네스는 환경 변수 `BSE_NATIVE_DIR`(네이티브 라이브러리 폴더)와 `BSE_MODS_DIR`(모드 폴더)을 따릅니다. `Tools/UnityCheck`는 Unity 없이 C# 컴파일을 검사하는 개발용 프로젝트로, UnityEngine 타입이 든 어셈블리 `.reference/UnityEngine.dll`이 있어야 빌드됩니다. 이 파일은 저장소에 포함하지 않습니다.

## 상태와 제한

| 항목 | 상태 |
|---|---|
| 네이티브 라이브러리 | zig로 크로스 빌드한 Windows x64 DLL이 Wine에서 네이티브 테스트를 모두 통과했습니다. 같은 소스를 Linux에서 일반 빌드와 ASan/UBSan 빌드로 돌려도 모두 통과했습니다. 실제 Windows와 Unity에서 이 DLL을 불러 본 적은 없고, MSVC 빌드와 GitHub Actions 워크플로도 실행해 보지 않았습니다 |
| C# 코드 | Unity 6000.3 공개 참조 소스로 만든 UnityEngine 어셈블리를 기준으로 컴파일 검사를 통과했고, Core 로직은 dotnet 하네스 테스트를 통과했습니다. Unity 에디터와 플레이어에서의 실행(게임 선택 화면을 포함한 IMGUI 화면, 셰이더, 카메라, 입력)은 확인하지 못했습니다 |
| 패키지와 설정 | `Packages/manifest.json`, `ProjectSettings/ProjectVersion.txt`(6000.3.0f1), 플러그인 `.meta`는 직접 작성한 파일입니다. Unity가 프로젝트를 열면서 값을 고칠 수 있고, `com.unity.test-framework` 1.6.0이 6000.3과 맞는지는 확인하지 못했습니다 |
| 스크립팅 백엔드 | Mono 기준으로 작성했습니다. IL2CPP 빌드는 확인하지 않았습니다 |

기능 범위: 한 프로세스에서 하는 로컬 플레이(사람/컴퓨터 좌석)와 격자 보드. 게임 목록은 폴더에서 읽은 모드만 다루며 Steam Workshop 연동(구독한 항목 읽기, 업로드)은 없습니다. 네트워크 멀티플레이, 파일 저장(콘솔 `save`는 메모리에만 보관), 카드·주사위 같은 물리 오브젝트, 외부 모델·이미지 에셋은 없습니다. 말은 `stone`, `disc`, `cube` 기본 도형으로 그립니다. 컴퓨터 플레이어는 무작위 수만 둡니다. 씬 파일은 포함하지 않으며 메뉴가 만들어 줍니다.

## 라이선스

Board Sandbox의 저작권과 라이선스 권한은 저자에게 있습니다. 이 저장소에는 사용을 허락하는 라이선스 파일이 없으므로 사용과 배포의 조건은 저자가 정합니다.

Luau는 Board Sandbox의 일부가 아니며 저자의 권한이 미치지 않는 제3자 소프트웨어입니다. MIT 라이선스(Copyright (c) 2019-2026 Roblox Corporation, 1994–2019 Lua.org, PUC-Rio)로 배포되고 `bse_luau.dll`에 정적으로 들어 있습니다. MIT 라이선스는 사본마다 저작권 고지와 허락 문구를 넣도록 요구하므로 DLL이나 이를 담은 빌드를 배포할 때 `THIRD_PARTY_NOTICES.txt`를 함께 넣어야 합니다. zig로 만든 DLL이 담는 런타임 라이브러리(LLVM libc++ 등)의 고지도 같은 파일에 있습니다.
