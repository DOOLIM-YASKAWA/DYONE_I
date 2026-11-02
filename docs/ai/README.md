# 사내 AI 공통 환경 — 화면마다 에이전트 붙이기 (2026-10-30)

DY ONE의 모든 AI 기능은 **사내 AI 하나**(dy-linux SGLang, OpenAI 호환, 모델 `qwen3.8-27b-fp8`)를 같은 길로 부른다. 고객 자료는 사내 서버 밖으로 나가지 않는다.

## 구조

| 자리 | 파일 | 하는 일 |
|---|---|---|
| 서버 공통 클라이언트 | `server/src/DyOne.Api/Platform/Llm.cs` | `ChatAsync`(글자)·`JsonAsync`(JSON) · 키 없으면 `IsConfigured=false` · 생각 과정 끔 · 기능별 시간·토큰 로그 |
| 에이전트·공통 기능 API | `server/src/DyOne.Api/Modules/Ai/AiAgentModule.cs` | `GET /api/ai/status` · `POST /api/ai/agent/{agent}` · `POST /api/ai/classify-docs` |
| 화면 공통 | `web/src/ai/aiClient.ts` | `useAiStatus()` · `agentChat()` · `classifyDocs()` |
| 화면 에이전트 창 | `web/src/ai/AgentPanel.tsx` | 떠 있는 창(끌어 옮기기·접기), 빠른 질문 버튼, 예시 질문 |
| 설정 | `Llm:BaseUrl`·`Llm:Model`(appsettings) · `Llm:ApiKey`(user-secrets / 운영 환경변수 `Llm__ApiKey`) | 키는 저장소에 넣지 않는다 |

## 새 화면에 에이전트 붙이기

1. **서버** `AiAgentModule.Agents`에 한 줄: 키 → (보이는 이름, 역할 지시). 역할 지시에 "[화면 상황]에 있는 것만 근거로" 같은 원칙을 넣는다.
2. **화면**에서
   ```tsx
   <AgentPanel agent="키" title="보이는 이름"
     context={() => '지금 화면 상황(목록 요약 등, 3만 자까지)'}
     actions={[{ label: '요약', prompt: '…' }]} examples={['…']} />
   ```
3. 대화는 저장하지 않는다(창을 닫으면 사라짐). 질문 앞 200자는 감사 기록 `ai.agent`.

## 원칙

- AI는 **제안**만 한다. 반영(저장·상태 변경)은 사람이 누른다.
- 키가 없거나 실패하면 AI 없이 동작한다(규칙·기존 화면).
- 프롬프트(역할 지시)는 서버에만 둔다 — 화면이 마음대로 바꾸지 못하게.
- 협력사 로그인에는 AI API가 열리지 않는다(기본 차단 정책).

## 지금 붙은 것

| 화면 | 에이전트/기능 | 비고 |
|---|---|---|
| 프로젝트 자료함 | 올릴 때 종류 분류(`classify-docs`) | 근거: [project-docs/NAS 13칸 분류 기준](../project-docs/) |
| 프로젝트 자료함 | `project-docs` 자료함 도우미 | 자료 목록을 보고 찾기·요약·빠진 자료 |
| 사양서 분석 | 사양정리 초안(`SpecAi.cs`) | 공통 클라이언트로 옮기는 것은 사양서 분석 담당 쪽에서 |
| 수담푸드 메뉴(Worker) | 메뉴 이미지 글자 읽기 | Worker는 따로 호출(같은 설정) |
