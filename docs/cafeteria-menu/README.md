# 수담푸드 메뉴 (구내식당 식단) — 출처 분석 (2026-10-30)

## 출처
- 위탁 급식업체 **수담푸드**가 매주 올리는 네이버 QR '메뉴판' 페이지: `https://m.site.naver.com/25qXx`
  - 단축 링크가 아니라 페이지 자체(HTTP 200, Next.js). 로그인 없음. `robots.txt` = `Allow: /`, 응답 헤더 `x-robots-tag: noindex`.
  - iframe 차단 헤더(X-Frame-Options·frame-ancestors) 없음 — 그래도 임베드는 쓰지 않음(모바일 레이아웃, 오늘만 골라 보기 불가).
- 식단은 **글이 아니라 이미지(PNG 1260×891) 6장**: 1번째 = 주간표(월~금), 2~6번째 = 요일별 한 장. **점심만** 있음.
  - 메인 메뉴는 **빨간 글씨**. 하단 안내 "식자재 수급에 따라 메뉴 변경될 수 있음".
  - 주 1회(보통 일요일) 이미지 교체. 이미지 주소 경로에 업로드 날짜(`…/20261025_205/…`).
- 페이지 데이터: `<script id="__NEXT_DATA__">` → `props.pageProps.qrDetail.sessions[type=Image].attachments[].imgOriginUrl`(orderId 순).
  - 페이지의 한글 제목·안내문은 네이버 응답에서 이미 깨져 옴(U+FFFD) → 쓰지 않음.

## 가져오는 방법을 고른 이유
- 업체가 이미 네이버에 올리므로 DY ONE 직접 입력(이중 작업) 대신 Worker가 읽어 온다.
- 이미지 → 글자는 사내 AI(이미지 입력)로 읽고, 요일 한 장짜리 결과를 주간표보다 우선한다. 틀릴 수 있어 화면에 '원본 이미지' 버튼을 항상 둔다.
- 결과는 DB가 아니라 캐시 파일(`FileStorage:Root\_meals\sudam.json`) — 지난 주 식단을 보관할 필요가 없어서.
- 트레이 알림은 하지 않기로 함(2026-10-30 사용자 결정).

## 코드
- Worker `server/src/DyOne.Worker/Jobs/SudamMenuJob.cs`(`SudamMenu` 설정), API `Modules/Meals/MealsModule.cs`(`GET /api/meals/sudam`), 웹 `web/src/pages/MealsPage.tsx`·`styles/meals.css`.
- 운영 차이는 `DEPLOY.md` 13장.
