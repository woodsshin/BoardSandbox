# Board Sandbox Engine

**Unity 6.3** 기반의 테이블탑 시뮬레이터 방식 보드게임 플랫폼입니다. 게임 규칙은 **Luau 스크립트 모드**로 작성하며, Unity(C#)는 규칙을 실행·검증해 그 결과를 3D 장면으로 시각화합니다. 

`config.json`과 `.luau` 파일이 담긴 폴더 하나가 곧 하나의 독립된 게임이 되며, C# 코어 엔진에는 특정 게임에 종속된 하드코딩이 전혀 없습니다.

* **지원 플랫폼**: Windows (x64), Android (ARM64, ARMv7, x86_64)
* **기본 내장 모드**: 오목, 체스, 스도쿠(1인 및 2인 협동), 고스톱, 3D 큐브 퍼즐(Cubic 3x3)

![Chess](Image/Chess.png)

---

## 핵심 기술

* **샌드박스 스크립트 런타임**
  * 정적 링크된 **Luau 0.745 VM**을 버전이 명시된 C ABI(`bse_*` 함수 10개)로 래핑하여 P/Invoke 및 JSON 입출력으로 호출합니다.
  * 호출당 실행 시간(기본 250ms)과 메모리(64MB) 한도를 엄격히 제한하며, 위험한 전역 함수(`io`, `os`, `loadstring` 등)를 제거했습니다.
  * 스크립트 예외 발생이나 한도 초과 시 VM을 즉시 재생성하고, 마지막 정상 스냅샷 상태로 안전하게 복구합니다.
* **호스트 권한 명령 파이프라인**
  * 마우스, 콘솔, AI 플레이어, 원격 참가자의 모든 입력은 단일 진입점(`GameSession.Submit`)으로 모입니다.
  * `ICommandInterceptor`(`TurnGuard`, `RejectionLog`)와 모드의 `rules.command` 검증을 통과한 입력만 반영되며, 처리 결과는 `Accepted`, `Rejected`, `Vetoed`, `Invalid`, `Faulted`의 5가지 상태로 명확히 분류됩니다.
* **이벤트 기반 뷰와 연출 재생**
  * 규칙 엔진이 발행한 이벤트로 뷰 모델 미러를 갱신하고, `PlaybackQueue`가 보조 뷰 모델에 이벤트를 순차 재생합니다.
  * 회전 애니메이션 등 시각 효과(`fx`) 구간마다 재생을 일시 정지하며, 표현 계층과 게임 로직이 완전히 분리되어 있습니다.
* **시간 되감기(Time Rewind)**
  * 매 수마다 상태 스냅샷을 최대 4,096단계까지 기록하여 되돌리기(Undo), 다시 실행(Redo), 임의 시점 이동을 지원합니다. 모드는 자신의 상태를 저장하고 복원하는 `snapshot`/`restore` 인터페이스만 구현하면 됩니다.
* **결정론적 헤드리스 시뮬레이션**
  * 백그라운드 스레드에서 별도의 VM을 구동해 무작위 대국을 고속으로 대량 실행하고 승률, 무승부 비율, 평균 수 등을 집계합니다. 동일한 모드, 옵션, 시드(Seed)를 입력하면 언제나 100% 동일한 결과를 보장합니다.
* **엔진 비의존 코어 아키텍처**
  * `BoardSandbox.Core`는 `UnityEngine`을 참조하지 않아(`asmdef noEngineReferences`) 콘솔 애플리케이션이나 `.NET` 테스트 환경에서도 동일하게 동작합니다.
  * 모드 소스를 `IModSource`(`Built-in`, `Local`, `Workshop`)로 추상화했습니다. Android 환경에서는 APK 내부에서 내장 모드를 직접 읽으며(`ZipModSource`), 일반 폴더에서 읽을 때와 동일한 내용 해시(Content Hash)를 유지합니다.

---

## 멀티플레이

* **호스트 권한 방 구조 및 멀티 백엔드**
  * `NetHostRoom`이 규칙을 실행해 이벤트를 전파하고 `NetClientRoom`이 이를 재생하며, 모든 참가자의 입력은 호스트가 검증합니다.
  * 로비와 전송 계층을 `ILobbyBackend`, `INetTransport`로 분리하여 **Steam**(Windows, `STEAMWORKS_NET`)과 **Epic Online Services**(Android/Windows, 기기 ID 익명 로그인 및 P2P)를 모두 지원합니다.
  
  ![Cubic](Image/Cube.png)
  
* **패킷 분할 및 재조립**
  * `PacketTransport` 계층을 통해 전송 크기 제한이 작은 네트워크 환경(EOS P2P 기준 1,170바이트)에서도 방 메시지를 자동으로 분할 전송하고 수신 측에서 재조립합니다.
* **버전 호환성 및 매칭**
  * 방 매칭 시 게임 ID, 프로토콜 버전, 모드 내용 해시를 대조해 무결성을 보장합니다.
  * AI 좌석 채우기, 분할 화면 시점 공유, 채팅 등 신규 기능은 선택적(Optional) 프로토콜 필드로 확장하여 기존 프로토콜 버전(1) 및 구버전 클라이언트와의 하위 호환성을 유지했습니다.
* **다양한 플레이 모드 및 채팅**
  * **3가지 화면/진행 방식**: 단일 화면 공유, 턴 제한 없는 협동 플레이, 각자의 보드를 풀면서 상대의 시점을 실시간으로 확인하는 분할 화면(호스트가 갱신 빈도와 값 범위 검증)을 지원합니다.
  * **채팅 시스템**: 텍스트 정제(Sanitization), 도배 방지 속도 제한(5초당 최대 5줄), 최근 100줄 메시지 보관 정책이 적용되어 있습니다.

---

## Luau 기반 AI 플레이어

진행 중인 게임 상태에 영향을 주지 않도록 **동일한 모드로 생성한 별도의 보조 VM**에 현재 국면을 복원한 뒤, 백그라운드 작업 스레드에서 `rules.query("think")`를 호출하여 UI 프리징을 방지합니다. AI가 도출한 명령 역시 일반 플레이어의 입력과 동일한 검증 파이프라인을 거치며, 설정된 난이도(탐색 단계)는 앱 재시작 후에도 유지됩니다.

![Gostop](Image/Gostop.png)

* **오목**: 구간 단위 증분 평가, 반복 심화 알파-베타 탐색, 사(4목) 연장 탐색 적용. *(자체 대국 벤치마크: 5,000단계 vs 50단계 — 20전 19승)*
* **체스**: MVV-LVA, 킬러·히스토리 무브 정렬, 체크 연장, 정지 탐색(Quiescence Search), 기물-칸 가치 평가 테이블을 결합한 알파-베타 탐색 적용. *(자체 대국 벤치마크: 30,000단계 vs 6,000단계 — 10전 8승)*
* **고스톱**: 1단계는 규칙 기반 알고리즘을 사용하며, 2·3단계는 보이지 않는 패를 무작위로 재분배하는 몬테카를로 시뮬레이션(Determinization)을 수행합니다. *(자체 대국 벤치마크: 1,200단계 vs 규칙 기반 — 360전 198승 158패 4무)*

---

## AI 보조 모드 제작

게임 내 AI 콘솔에 원하는 보드게임의 규칙을 자연어로 설명하면 연동된 AI 에이전트가 `config.json`과 Luau 스크립트를 자동으로 생성합니다.

![Edit with AI](Image/EditwithAI.png)

* **지원 에이전트**: Claude Code, Codex CLI, Gemini CLI, Antigravity, Grok, 로컬 Ollama 및 OpenAI 호환 API 엔드포인트
* **자동 검증 및 자가 수정**: 엔진이 생성된 모드를 즉시 로드해 무작위 수를 최대 60수까지 시뮬레이션하며, 오류 발생 시 에러 로그를 에이전트에게 전달해 스스로 수정하도록 합니다(최대 2회 재시도).
* **안전성 및 복구**: `/undo` 명령어로 언제든 이전 버전의 파일로 되돌릴 수 있으며, 사용자 프롬프트는 커맨드라인 인자 노출을 피해 표준 입력(stdin)이나 임시 파일로만 안전하게 전달됩니다.

---

## 빌드 및 검증

* **네이티브 빌드 및 바이너리 검사**
  * 고정된 Luau 커밋 버전을 빌드하는 CMake 구성을 제공합니다. Windows DLL(MSVC 및 Zig 크로스 빌드)과 Android `.so`(NDK) 빌드를 지원하며 GitHub Actions CI 워크플로가 포함되어 있습니다.
  * 전용 ELF 검사 스크립트를 통해 타깃 CPU 아키텍처, 허용된 의존성 라이브러리(`libc`, `libm`, `libdl`), 내보낸 심볼 목록, **16KB 페이지 정렬(Page Alignment)** 준수 여부를 검증합니다.
* **다계층 테스트 스위트**
  * Unity 없이 실행되는 `.NET` 코어 하네스 테스트 480여 개, Unity EditMode 테스트, 그리고 C ABI·VM 수명 주기·샌드박스 격리·리소스 한도·멀티스레딩 안정성을 검증하는 네이티브 테스트를 갖추고 있습니다.
  * 네이티브 라이브러리는 Linux ASan/UBSan 메모리·정의되지 않은 동작 검사를 통과했으며, 크로스 빌드된 Windows DLL 역시 Wine 환경에서 정상 동작을 검증했습니다.
* **Android Gradle 9.1 / AGP 9.0 호환성 지원**
  * EOS 플러그인의 레거시 라이브러리 모듈 설정을 교정하고 Core Library Desugaring을 활성화하는 자동화 빌드 단계와, Unity 에디터 없이 독립 실행 가능한 PowerShell 스크립트를 제공합니다.
* **정적 분석 및 실기기 검증**
  * `bse_analyze` 도구를 통해 `bse` 타입 선언에 기반한 모드 스크립트의 정적 타입 검사를 수행합니다.
  * 코어 로직·네트워킹·CLI 도구의 하네스 테스트뿐만 아니라 Unity 렌더링 파이프라인, 실제 Android 기기 구동, 실제 Steam 및 EOS 라이브 서비스 연동 검증까지 완료했습니다.

### 빌드
- 윈도우 빌드 : [다운로드](https://drive.google.com/file/d/1_2vO5xoq66Gf55g6tXKK89JYEZO3Eki4/view?usp=drive_link)
- 안드로이드 빌드 : [다운로드](https://drive.google.com/file/d/1SPcVkRUQnaPEPeV-nXK4K22yn-9Bdz_e/view?usp=sharing)

## 라이선스

Board Sandbox의 저작권과 사용 조건은 [LICENSE.md](LICENSE.md)에 있습니다.

Luau는 Board Sandbox의 일부가 아니며 저자의 권한이 미치지 않는 제3자 소프트웨어입니다. MIT 라이선스(Copyright (c) 2019-2026 Roblox Corporation, 1994–2019 Lua.org, PUC-Rio)로 배포되고 `bse_luau.dll`과 `libbse_luau.so`에 정적으로 들어 있습니다. MIT 라이선스는 사본마다 저작권 고지와 허락 문구를 넣도록 요구하므로 DLL, .so나 이를 담은 빌드(APK 포함)를 배포할 때 `THIRD_PARTY_NOTICES.txt`를 함께 넣어야 합니다. zig로 만든 DLL과 NDK로 만든 .so가 담는 런타임 라이브러리(LLVM libc++ 등)의 고지도 같은 파일에 있습니다.

Steam 연동에 쓰는 Steamworks.NET(MIT, Riley Labrecque)과 그 패키지에 든 Steamworks SDK 재배포 파일(`steam_api64.dll`, Valve)도 제3자 소프트웨어입니다. 저장소에는 들어 있지 않고 Unity가 패키지로 받아 오며, 빌드를 배포할 때는 각자의 라이선스와 Steamworks SDK 약관을 따릅니다.

멀티플레이(Android, Windows)에 쓰는 [EOS Plugin for Unity](https://github.com/EOS-Contrib/eos_plugin_for_unity)(MIT, Copyright (c) 2026 Epic Games Inc)와 그 패키지에 든 Epic Online Services SDK(Epic Games)도 제3자 소프트웨어입니다. 마찬가지로 저장소에는 들어 있지 않고 Unity가 패키지로 받아 오며, 빌드를 배포할 때는 각자의 라이선스와 [Epic Online Services](https://onlineservices.epicgames.com/)의 약관을 따릅니다.

고스톱의 화투 도안(`Assets/StreamingAssets/Mods/gostop/cards.png`)은 Marcus Richert의 [Hwatu overview.svg](https://commons.wikimedia.org/wiki/File:Hwatu_overview.svg)(Louie Mantia, Jr.의 화투 그림을 바탕으로 제작)를 고쳐 만든 것이며 [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)으로 배포됩니다. 고친 내용은 48장을 잘라 한 장의 PNG로 모으고, 선 그림 뒤에 흰 바탕을 넣고, 한국식 월 순서(11월 오동, 12월 비)로 배열하고, 뒷면과 보너스 패 석 장을 더한 것입니다(`Tools/Hwatu/make_atlas.py`). 이 라이선스는 `cards.png`에만 적용되며 같은 조건(저작자 표시, 동일 조건 변경 허락)으로 다시 배포해야 합니다. 같은 고지가 `Mods/gostop/ARTWORK-LICENSE.txt`와 `THIRD_PARTY_NOTICES.txt`에 있고, 게임 안에서는 고스톱의 설명과 `query credits`에 표시됩니다.