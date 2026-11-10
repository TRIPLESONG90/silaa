# silaa

silaa는 Windows용 **Microsoft Agent Framework** 코딩 하네스입니다. OpenAI 호환 LLM API에 직접 연결하며 별도의 CLI, JavaScript 런타임 또는 로컬 HTTP 서버를 실행하지 않습니다.

기존 하네스에서 필요한 구조만 분리해 다시 구현했습니다.

- 작업 폴더별 로컬 세션과 대화 기록
- Microsoft Agent Framework `AIAgent`의 스트리밍, 함수 호출, 실행 수명주기
- OpenAI 호환 `chat/completions` provider
- C# 네이티브 도구: 파일 목록, 읽기, 검색, 쓰기, 정확 치환, 명령 실행
- 작업 상태 이벤트와 취소
- Markdown 시스템 프롬프트와 기본 스킬
- Claude Code 및 Codex 사용자 스킬 로딩
- Windows DPAPI로 API 키 암호화 저장
- 프로젝트별 Memory·파일·웹 주소·코드·노트 리소스와 암호화 Vault
- 로컬 앱과 생성물을 확인하는 내장 브라우저
- 추론 → 도구 → 답변 순서를 보존하는 선택 가능한 Markdown Agent 타임라인
- `chart` JSON 블록을 외부 CDN 없이 HTML/SVG로 렌더링하는 막대·선·도넛 차트
- Agent가 등록하는 영속 백그라운드 작업과 5필드 cron 반복 실행
- 약속·행사·마감을 독립 저장하는 실제 캘린더와 선택형 알람·준비 작업
- 하단 상태바에서 연결별 추론 수준을 즉시 바꾸는 빠른 선택기
- 로컬·UTC 시각과 시간대를 구조화해 제공하는 `get_current_datetime` 도구
- 코드 근거 기반 Mermaid 뷰를 만드는 `architecture-diagram` 내장 Skill
- 활성 작업이 있을 때 창 닫기 → Windows 트레이 이동, 완료·실패 Windows 알림
- 블랙 & 화이트 기반의 다크·라이트 UI, 검색 가능한 스킬 목록과 프로젝트 Inspector
- AXAML View와 CommunityToolkit.Mvvm ViewModel/RelayCommand로 분리된 메인 셸·홈·연결 설정
- 전체·프로젝트·세션 범위로 분리된 Agent 접근 허용 폴더와 파일 도구 경계 검사
- 승인 카드와 연동된 C# 네이티브 웹 검색·본문 조회, 출처 URL 반환과 리다이렉트별 정책 검사

플러그인, MCP, PTY, 별도 서버, 외부 에이전트 데이터베이스 같은 부가 계층은 포함하지 않습니다.

## 개발 실행

필요한 것은 .NET 10 SDK뿐입니다.

```powershell
dotnet run --project src/silaa/silaa.csproj
```

## UI 구조

앱 셸, 세션 사이드바, 대화 컴포저, 홈/프로젝트 목록, Memory, Vault, 스킬, 백그라운드 작업, 내장 브라우저와 LLM 연결 설정은 AXAML View로 선언합니다. 화면 상태와 명령은 `CommunityToolkit.Mvvm`의 `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`가 담당하며 compiled binding을 기본으로 사용합니다. 스트리밍 Markdown·추론/도구 타임라인과 프로젝트 인터랙션 카드처럼 증분 렌더링이 중요한 부분만 전용 Avalonia 컨트롤로 유지합니다.

## 설정

| 항목 | 설명 |
|---|---|
| LLM API 주소 | OpenAI 호환 기본 주소. `/chat/completions` 제외 |
| 모델 ID | 서버가 제공하는 모델 이름 |
| API 키 | 선택 사항. DPAPI(CurrentUser) 암호화 저장 |
| 작업 폴더 | 하네스 파일·검색·명령 도구의 루트 |
| 접근 허용 폴더 | 전체·프로젝트·세션별로 추가하는 파일 읽기·검색·편집 허용 루트 |
| 화면 테마 | 다크 또는 라이트. 저장 후 앱 재실행 시 적용 |
| 최대 출력 토큰 | 비우면 서버 기본값 |
| 컨텍스트 길이 | 오래된 대화와 스킬 지침을 줄이는 기준 |
| 최대 토큰 필드 | `max_tokens` 또는 `max_completion_tokens` |
| Temperature / Top P | 비우면 서버 기본값 |
| Frequency / Presence penalty | -2~2, 비우면 서버 기본값 |
| Seed | 재현성용 정수, 비우면 서버 기본값 |
| 요청 타임아웃 | 비우면 120초 |
| 추론 수준 | `서버 기본값`, `끔`, `낮음`, `보통`, `높음`, `최대`. 저장값은 호환 서버에 `reasoning_effort`로 전달 |

추론 수준은 LLM 연결 편집 화면뿐 아니라 하단 상태바에서도 바꿀 수 있습니다. 변경한 값은 활성 연결에 즉시 저장되며 다음 요청부터 적용됩니다. 실제 일정 등록 설문은 불필요하게 길어지지 않도록 연결 설정과 무관하게 `low`를 사용합니다.

API 키는 `%LOCALAPPDATA%\silaa\llm-credential.json`에 암호문으로 저장됩니다. 빈 API 키로 다시 저장하면 기존 키를 유지하고, `저장된 API 키 삭제`를 선택해야 제거됩니다.

세션은 `%LOCALAPPDATA%\silaa\harness-sessions.json`에 저장됩니다. 이전 엔진이 만든 세션 데이터는 읽거나 변경하지 않습니다.

파일 도구는 기본 작업 폴더와 명시적으로 허용한 폴더만 접근합니다. 전체 권한은 모든 실행에, 프로젝트 권한은 해당 프로젝트의 모든 세션에, 세션 권한은 현재 세션에만 적용됩니다. 셸 명령은 기본 작업 폴더에서 실행되지만 운영체제 수준의 완전한 샌드박스는 아니므로 신뢰할 수 없는 명령은 실행하지 않아야 합니다.

최신 정보가 필요한 요청에서 Agent는 `web_search`와 `fetch_url`을 사용합니다. 최초 목적지 또는 리다이렉트 목적지가 허용되지 않았다면 도구가 정확한 `host:port` 승인 카드를 자동으로 띄우고, 승인 후 중단된 조회를 이어갑니다. 응답은 2MB로 제한되며 HTML의 스크립트·스타일을 제거한 읽기용 텍스트와 원본 `SOURCE` URL을 모델에 전달합니다. 검색 결과 자체는 발견용으로만 사용하고, 최종 답변은 조회한 공식·1차 출처 링크를 인용하도록 시스템 지침에 포함되어 있습니다.

### Ollama 추론

Ollama는 API 주소를 `http://127.0.0.1:11434/v1`로 설정합니다. thinking 모델이 OpenAI 호환 스트림의 `reasoning` 필드로 보내는 추론을 silaa가 Agent Framework의 reasoning 이벤트로 변환해 답변·도구 호출과 같은 타임라인에 표시합니다. `gemma4:*`에서 추론 수준을 `none` 이외로 지정하면 Gemma 4용 `<|think|>` 시스템 토큰도 자동으로 적용합니다.

## HTML 차트

Agent 답변의 `chart` 코드 블록은 검증된 JSON만 받아 네트워크 접근이 차단된 self-contained HTML/SVG로 렌더링합니다. 지원 형식은 `bar`, `line`, `donut`입니다.

````text
```chart
{"type":"bar","title":"분기 매출","unit":"억","labels":["1Q","2Q","3Q"],"series":[{"name":"매출","values":[12,18,24]}]}
```
````

일반 Markdown과 스트리밍은 기존 Avalonia 네이티브 렌더러를 유지하므로 차트가 없는 긴 답변의 선택·복사·스크롤 성능에는 WebView 비용이 추가되지 않습니다.

## 백그라운드 작업과 cron

대화에서 “백그라운드에서 실행해”라고 요청하면 Agent가 독립 작업을 큐에 등록합니다. 반복 작업은 표준 5필드 cron 표현식(`분 시 일 월 요일`)과 컴퓨터의 로컬 시간대를 사용합니다. 예를 들어 평일 오전 9시는 `0 9 * * 1-5`입니다. 작업은 별도 세션에서 실행되므로 완료 후 `백그라운드 작업` 화면에서 전체 결과를 열 수 있습니다.

작업·스케줄 상태는 `%LOCALAPPDATA%\silaa\background-automation.json`에 저장됩니다. 창 닫기 버튼은 앱을 종료하지 않고 항상 Windows 트레이로 숨깁니다. 트레이 메뉴의 `silaa 종료`를 선택하면 실행 중인 작업이 취소됩니다. silaa가 완전히 종료된 동안에는 cron을 실행할 수 없으며, 다음 시작 시 놓친 실행을 한 번만 재개합니다.

## 프롬프트와 스킬

기본 에이전트 행동은 `prompts/system.md`, 기본 스킬은 `skills/*/SKILL.md`에 있습니다. 실행 시 다음 순서로 합쳐 LLM 시스템 메시지에 넣습니다.

1. 앱에 번들된 기본 스킬
2. `~/.claude/skills/*/SKILL.md`
3. `~/.claude/commands/*.md`
4. `~/.codex/skills/*/SKILL.md`의 일반 사용자 스킬
5. Codex review-agent 스킬이 있으면 추가

## Skill 설치와 실습 모드

Skill 화면에서 GitHub 저장소/폴더 URL 또는 로컬 폴더를 선택해 설치할 수 있습니다.

- 프로젝트 범위: `<workspace>/.silaa/skills/<name>`
- 전체 범위: `%LOCALAPPDATA%/silaa/skills/<name>`
- GitHub URL 예: `https://github.com/owner/repo/tree/main/skills/example`
- 저장소 URL만 입력한 경우 저장소 안의 `SKILL.md` 폴더를 별도로 지정합니다.

설치기는 `SKILL.md`의 이름과 설명, 파일 개수와 전체 크기, 경로 탈출 및 링크 여부를 검사합니다. 패키지의 `scripts`, `references`, `assets`는 함께 복사하지만 설치 중 실행하지 않습니다. 같은 이름을 교체하려면 사용자가 업데이트 옵션을 명시해야 합니다. 대화에서는 “이 저장소의 Skill을 프로젝트에 설치해줘”처럼 요청할 수 있으며 필요한 네트워크와 폴더 권한은 승인 카드로 처리됩니다.

`guided-practice` 기본 Skill은 비개발자에게 하네스 용어를 노출하기보다 실습 목표, 준비 상태, 다음 단계와 검증 결과를 대화형으로 안내합니다.

## 검증

```powershell
dotnet run --project tests/silaa.Tests.csproj
dotnet build src/silaa/silaa.csproj -c Release
dotnet run --project tests/silaa.Tests.csproj -c Release -- --xaml-smoke
dotnet run --project tests/silaa.Tests.csproj -c Release -- --render-bench
dotnet run --project tests/silaa.Tests.csproj -c Release -- --ui-latency-probe
```

테스트는 모의 OpenAI 호환 서버를 띄워 DPAPI 저장, NVIDIA용 `max_tokens` 호환 처리, 샘플링·테마 옵션, Bearer 헤더, MD 스킬 주입, Agent Framework 함수 호출 왕복, 세션 영속화와 UI 접근성을 확인합니다. 별도 벤치마크 명령은 Markdown 스트리밍과 누적 Agent 타임라인의 렌더링 지연을 측정합니다.

## Windows 배포

```powershell
.\scripts\publish-win-x64.ps1
```

결과는 `publish\win-x64`에 생성됩니다. 스크립트는 스테이징 폴더에 먼저 게시한 뒤 기존 배포 폴더를 교체하므로 오래된 런타임 파일이 남지 않습니다.
