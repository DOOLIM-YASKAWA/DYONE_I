// 프로젝트 메인 시안 공통: 같은 데이터·동작을 세 디자인(A·B·C)이 함께 쓴다. 디자인마다 CSS와 뼈대만 다르다.
// 숫자·코드·이름은 11-03 시험 계산(ERP·모듈라·활동)에서 가져온 실제 값, 문장은 예시.
(() => {
// 브리핑 = 관심·주요 프로젝트의 '지금 상태'(단계·최근 진행·다음 일정). 어제 변화는 줄 끝 '어제 +n'으로만(11-04 정정)
const BRIEF = {
  fav: [
    { code: 'DLP-24-260', name: '기아 화성5 하도 및 신기술', text: '모듈라 단계로 OLT가 진행 중이며, 최근 2주 설계 도면 14건이 올라왔습니다. 양산은 2027-06-30 예정입니다.', ch: 3, ev: 3 },
    { code: 'DLP-25-281', name: '화성2 무빙실러 2차', text: '현장 설치 중이고 최근 일지는 시운전 준비 위주입니다. ERP 공사 종료는 12/28입니다.', ch: 2, ev: 2 },
    { code: 'DLP-25-070', name: '에코 사바나 가니쉬 신공장', text: 'SOP 뒤 양산대응 단계이며, 최근 30일 발주 4건이 이어지고 있습니다.', ch: 1, ev: 2 },
  ],
  feat: [
    { code: 'DLP-24-054', name: '현대 사우디 신공장 중상도 로봇', text: '양산(08/15) 뒤 양산대응 중이며, 최근 일지는 현장 보완 위주입니다. 공사 종료는 12/31입니다.', ch: 3, ev: 3 },
    { code: 'DLP-25-069', name: 'BHMC#4 이설', text: '현장 설치가 진행 중이고 최근 30일 활동 9건으로 꾸준합니다. 공사 종료는 12/31입니다.', ch: 1, ev: 2 },
    { code: 'DLP-25-104', name: '에코플라스틱 국내 신공장', text: '설계·검토 단계로 사양서 개정판(v4)이 반영 중입니다. 로봇 29대 규모입니다.', ch: 0, ev: 2 },
  ],
}
const FAV = [
  { code: 'DLP-24-260', name: '기아 화성5 공장 하도 및 신기술', sub: '어제 · 설계팀 도면 업로드', pm: '김철수', stage: ['mod', '모듈라'] },
  { code: 'DLP-25-281', name: '화성2 무빙실러 2차 10대 자동화', sub: '어제 · 업무 일지 2건', pm: '박민수', stage: ['site', '현장'] },
  { code: 'DLP-25-070', name: '에코 사바나 가니쉬 신공장', sub: '어제 · ERP 발주 2건', pm: '정수진', stage: ['line', '라인 대응'] },
  { code: 'DLP-26-119', name: '화성5 신기술 상도 샌딩 폴리싱 와이핑 자동화', sub: '3일 전 · 옛 코드 DLP-26-033 자료 연결', pm: '-', stage: ['design', '설계·검토'] },
  { code: 'DLP-25-274', name: '현대 아산 중도 도어내판 교체', sub: '45일 전', pm: '-', stage: ['line', '라인 대응'] },
]
const FEAT = [
  { code: 'DLP-24-054', name: '현대 사우디 신공장 중상도 로봇', ai: 'PM 이도현 · 공사 종료 12/31', amt: '128억', stage: ['line', '라인 대응'] },
  { code: 'DLP-25-069', name: 'BHMC#4 이설 프로젝트', ai: 'PM 미지정 · 공사 종료 12/31 · 로봇 38대', amt: '57억', stage: ['site', '현장'] },
  { code: 'DLP-25-104', name: '에코플라스틱 국내 신공장', ai: 'PM 미지정 · 공사 종료 12/31 · 로봇 29대', amt: '59억', stage: ['design', '설계·검토'] },
  { code: 'DLP-25-229', name: 'GMK 부평1공장 TOP1,2 베이스외판 로보 교체', ai: 'PM 미지정 · 공사 종료 12/31 · 로봇 16대', amt: '56억', stage: ['design', '설계·검토'] },
  { code: 'DLP-25-022', name: '기아 조지아 6차수 중상도 로봇 교체', ai: 'PM 미지정 · 공사 종료 12/31 · 로봇 16대', amt: '48억', stage: ['site', '현장'] },
]
const CAND = [
  { s: 7, code: 'DLP-26-147', name: '현대 인도 MP2 PCS 신공장', why: '해외 신공장 대형 건이고 아직 초기 단계입니다.', parts: ['계약 37억 +4', '신공장 +2', '해외 +1'] },
  { s: 7, code: 'DLP-26-068', name: '서연이화 미국 중소물 도장 신공장', why: '미국 신공장으로 최근 활동이 늘고 있습니다.', parts: ['계약 18억 +2', '로봇 5대 +1', '신공장 +2', '해외 +1', '활동 +1'] },
  { s: 6, code: 'DLP-25-281', name: '화성2 무빙실러 2차 10대 자동화', why: '대형 건이 양산 직전입니다.', parts: ['계약 53억 +4', '종료 임박 +2'] },
  { s: 6, code: 'DLP-25-129', name: '현대 전주 트럭 도장합리화 도료공급설비 개조', why: '연내 종료 예정인 대형 개조 건입니다.', parts: ['계약 52억 +4', '종료 임박 +2'] },
]
const ROWS = [
  ['DLP-24-260', '기아 화성5 공장 하도 및 신기술 프로젝트', '기아 화성', '수주', '', ['mod', '모듈라'], '김철수', '어제 · 모듈라 수정', 1],
  ['DLP-26-033', '기아 화성 5공장 신공장 신기술 자동화 프로젝트', '기아 화성', '중단', '영업중', ['line', '—'], '-', '—', 0, '→ DLP-26-119~122로 나뉨'],
  ['DLP-26-119', '화성5 신기술 상도 샌딩 폴리싱 와이핑 자동화', '기아 화성', '수주', '', ['design', '설계·검토'], '-', '3일 전 · 옛 코드 033 자료 연결', 1],
  ['DLP-25-281', '화성2 무빙실러 2차 10대 자동화 프로젝트', '기아 화성', '수주', '', ['site', '현장'], '박민수', '어제 · 업무 일지', 1],
  ['DLP-25-104', '에코플라스틱 국내 신공장', '에코플라스틱', '수주', '영업중', ['design', '설계·검토'], '-', '5일 전 · 사양서', 0],
  ['DLP-25-188', '에코플라스틱 GP신공장 범퍼 로딩/언로딩 자동화', '에코플라스틱', '수주', '영업중', ['site', '현장'], '최가영', '어제 · ERP 발주', 0],
  ['DLP-24-054', '현대 사우디 신공장 중상도 로봇', '현대자동차', '수주', '', ['line', '라인 대응'], '이도현', '어제 · 업무 일지 3건', 0],
  ['DLP-25-274', '현대 아산 중도 도어내판 교체', '현대 아산', '수주', '영업중', ['line', '라인 대응'], '-', '45일 전', 1],
]

// 로고: 이미지 대신 SVG로 직접 그림(확대해도 흐려지지 않음). data-logo="light"(어두운 바탕) | "dark"(밝은 바탕)
const LOGO = {
  light: { o: '#A9CCFF', ne: '#FFFFFF', t: '#DDE8FF' },
  brand: { o: '#9FD0FF', ne: '#FFFFFF', t: '#D6E9FF' },
  dark: { o: '#4C8DF0', ne: '#0C1A33', t: '#5A6680' },
}
// 원본 로고(글자 높이 24px)를 픽셀 단위로 재서 외곽선을 그대로 옮김. 글꼴에 기대지 않도록 INTERNAL도 선으로 직접 그림.
//  o: 바깥 28×24, 왼쪽 모서리 r2.5 · 오른쪽 r5, 굵기 가로 6.5 · 세로 5.5
//  n: 왼쪽 위 거의 직각, 오른쪽 위 r6(안쪽 r2)
//  e: 위·왼쪽 아래 r6, 가운데 가로획, 오른쪽 아래가 트임
const LOGO_PATH = {
  o: 'M2.5 0H23A5 5 0 0 1 28 5V19A5 5 0 0 1 23 24H2.5A2.5 2.5 0 0 1 0 21.5V2.5A2.5 2.5 0 0 1 2.5 0Z M6.5 5.5V18.5H20.5A1 1 0 0 0 21.5 17.5V6.5A1 1 0 0 0 20.5 5.5Z',
  n: 'M30 24V1A1 1 0 0 1 31 0H48.5A6 6 0 0 1 54.5 6V24H48V7.5A2 2 0 0 0 46 5.5H36.5V24Z',
  e: 'M57 6A6 6 0 0 1 63 0H75.5A6 6 0 0 1 81.5 6V14H63.5V18.5H81.5V24H63A6 6 0 0 1 57 18Z M63.5 7A1.5 1.5 0 0 1 65 5.5H73.5A1.5 1.5 0 0 1 75 7V9H63.5Z',
  t: 'M91.6 9.2V16.8 M95.6 16.8V9.2L100.9 16.8V9.2 M103.1 9.2H108.9M106 9.2V16.8 M116.2 9.2H111.9V16.8H116.4M111.9 13H115.7 M119.9 16.8V9.2H123.1A2 2 0 0 1 123.1 13.2H119.9M122.5 13.2L125.3 16.8 M128.2 16.8V9.2L133.5 16.8V9.2 M136.1 16.8L139.2 9.2L142.3 16.8M137.3 14.1H141.1 M145.5 9.2V16.8H150.3',
}
// K안용 다듬은 로고(data-logo="refined"): 획을 정수 픽셀에 맞춰(굵기 6/5) 높이 24px에서 가장자리가 번지지 않게,
// o는 하늘색 그라디언트, ne는 아주 옅은 그라디언트, e와 INTERNAL 사이에 가는 구분선.
const LOGO2 = {
  o: 'M2 0H23A5 5 0 0 1 28 5V19A5 5 0 0 1 23 24H2A2 2 0 0 1 0 22V2A2 2 0 0 1 2 0Z M6 5V19H21A1 1 0 0 0 22 18V6A1 1 0 0 0 21 5Z',
  n: 'M30 24V1A1 1 0 0 1 31 0H48A6 6 0 0 1 54 6V24H48V7A2 2 0 0 0 46 5H36V24Z',
  e: 'M57 6A6 6 0 0 1 63 0H75A6 6 0 0 1 81 6V14H63V19H81V24H63A6 6 0 0 1 57 18Z M63 6.5A1.5 1.5 0 0 1 64.5 5H73.5A1.5 1.5 0 0 1 75 6.5V9H63Z',
}
let logoSeq = 0
document.querySelectorAll('[data-logo="refined"]').forEach((el) => {
  const id = `lg${++logoSeq}`
  el.innerHTML = `<svg viewBox="0 0 151 24" height="${el.dataset.h ?? 24}" role="img" aria-label="DY ONE INTERNAL" style="display:block" shape-rendering="geometricPrecision">
    <defs>
      <linearGradient id="${id}o" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#D4EDFF"/><stop offset="1" stop-color="#86C2FF"/></linearGradient>
      <linearGradient id="${id}n" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#E4F0FF"/></linearGradient>
    </defs>
    <path d="${LOGO2.o}" fill="url(#${id}o)" fill-rule="evenodd"/>
    <path d="${LOGO2.n}" fill="url(#${id}n)"/>
    <path d="${LOGO2.e}" fill="url(#${id}n)" fill-rule="evenodd"/>
    <path d="M86.5 5V19" stroke="#FFFFFF" stroke-opacity=".32" stroke-width="1"/>
    <path d="${LOGO_PATH.t}" fill="none" stroke="#E3EEFF" stroke-width="1.5" stroke-linecap="square" stroke-linejoin="miter"/></svg>`
})
document.querySelectorAll('[data-logo]:not([data-logo="refined"])').forEach((el) => {
  const c = LOGO[el.dataset.logo] ?? LOGO.dark
  el.innerHTML = `<svg viewBox="-0.5 -0.5 152 25" height="${el.dataset.h ?? 22}" role="img" aria-label="DY ONE INTERNAL" style="display:block" shape-rendering="geometricPrecision">
    <path d="${LOGO_PATH.o}" fill="${c.o}" fill-rule="evenodd"/>
    <path d="${LOGO_PATH.n}" fill="${c.ne}"/>
    <path d="${LOGO_PATH.e}" fill="${c.ne}" fill-rule="evenodd"/>
    <path d="${LOGO_PATH.t}" fill="none" stroke="${c.t}" stroke-width="1.5" stroke-linecap="square" stroke-linejoin="miter"/></svg>`
})

const $ = (s, r = document) => r.querySelector(s)
const $$ = (s, r = document) => [...r.querySelectorAll(s)]
const stage = ([k, n]) => `<span class="pm-stage"><i style="background:var(--st-${k})"></i>${n}</span>`
const favRow = (p) => `<a class="pm-row" data-go="${p.code}">
  <span class="pm-ico pm-star">★</span>
  <span class="pm-main"><span class="pm-name">${p.name}</span><span class="pm-code">${p.code}</span><span class="pm-sub">${p.sub} · PM ${p.pm}</span></span>
  <span class="pm-meta">${stage(p.stage)}</span><span class="pm-go">›</span></a>`
const featRow = (p, i) => `<a class="pm-row" data-go="${p.code}">
  <span class="pm-ico pm-rank">${i + 1}</span>
  <span class="pm-main"><span class="pm-name">${p.name}</span><span class="pm-code">${p.code}</span><span class="pm-sub">${p.ai}</span></span>
  <span class="pm-meta"><span class="pm-amt">${p.amt}</span>${stage(p.stage)}</span><span class="pm-go">›</span></a>`
const candRow = (c) => `<div class="pm-row pm-cand">
  <span class="pm-score">${c.s}</span>
  <span class="pm-main"><span class="pm-name">${c.name}</span><span class="pm-code">${c.code}</span><span class="pm-sub">${c.why}</span><span class="pm-parts">${c.parts.map((x) => `<span>${x}</span>`).join('')}</span></span>
  <span class="pm-meta"><button class="pm-btn pm-ok">선정</button><button class="pm-btn">제외</button></span></div>`
const briefLi = (b) => `<li><b data-go="${b.code}">${b.name}</b><span class="pm-txt">${b.text}</span>${b.ch ? `<span class="pm-ch">어제 +${b.ch}</span>` : ''}<a class="pm-ev">근거 ${b.ev}</a></li>`

const render = (noFav) => {
  $('#bfFav').innerHTML = noFav ? '<li class="pm-bf-empty">★로 고른 관심 프로젝트가 없습니다. 아래에서 ☆을 누르면 내일부터 여기에 요약이 나옵니다.</li>' : BRIEF.fav.map(briefLi).join('')
  $('#bfFeat').innerHTML = BRIEF.feat.map(briefLi).join('')
  $('#favList').innerHTML = noFav
    ? '<div class="pm-empty"><b>★로 고른 프로젝트가 없습니다</b><span>전체 목록에서 ☆을 누르면 여기에 보입니다.<br>PM·투입 프로젝트를 자동으로 넣지 않습니다.</span></div>'
    : FAV.slice(0, 3).map(favRow).join('')
  $('#favCount').textContent = noFav ? 0 : FAV.length
  $('#favMore').style.display = noFav ? 'none' : ''
  $('#featList').innerHTML = FEAT.slice(0, 3).map(featRow).join('')
  $('#featCount').textContent = FEAT.length
}
const tableRows = () => ($('#rows').innerHTML = ROWS.map((r) => `<tr data-go="${r[0]}">
  <td class="pm-tstar-cell"><button class="pm-tstar ${r[8] ? 'on' : ''}" aria-label="관심">${r[8] ? '★' : '☆'}</button></td>
  <td><span class="pm-tname">${r[1]}</span>${r[9] ? `<span class="pm-flag">${r[9]}</span>` : ''}<span class="pm-tcode">${r[0]}</span></td>
  <td>${r[2]}</td>
  <td><span class="pm-erp pm-erp-${r[3] === '수주' ? 'order' : r[3] === '중단' ? 'off' : 'sales'}">${r[3]}</span></td>
  <td>${stage(r[5])}</td><td>${r[6]}</td><td class="pm-muted">${r[7]}</td></tr>`).join(''))

// 서랍(모두 보기·후보 검토)
document.body.insertAdjacentHTML('beforeend', `<div class="pm-dim" id="pmDim"></div>
<aside class="pm-drawer" id="pmDrawer" aria-hidden="true"><header><h3 id="pmDTitle"></h3><button id="pmDClose" aria-label="닫기">×</button></header><p id="pmDNote"></p><div id="pmDBody"></div></aside>
<div class="pm-tools" title="시안 전용"><span>시안</span><button data-m="" class="on">직원</button><button data-m="sel">주요 선정 담당</button><button id="pmNoFav">★ 없을 때</button></div>
<div class="pm-toast" id="pmToast"></div>`)
const DRAWERS = {
  fav: ['내 관심 프로젝트', '★한 프로젝트 전체 · 끌어서 순서 변경', () => FAV.map(favRow).join('')],
  feat: ['회사 주요 프로젝트', '영업 부서(주요 선정 담당)가 고른 것만 보입니다 · 지금 상태 문장은 위 브리핑에', () => FEAT.map(featRow).join('')],
  cand: ['새 후보 검토', '규칙 점수로 매일 계산 · 점수 근거는 DB 값, 이유 문장은 사내 AI · [선정]해야 전원에게 보입니다', () => CAND.map(candRow).join('')],
}
const open = (k) => {
  const [t, n, body] = DRAWERS[k]
  $('#pmDTitle').textContent = t; $('#pmDNote').textContent = n; $('#pmDBody').innerHTML = body()
  document.body.classList.add('pm-open')
}
const close = () => document.body.classList.remove('pm-open')
$('#pmDClose').onclick = close; $('#pmDim').onclick = close
const toast = (t) => { const el = $('#pmToast'); el.textContent = t; el.classList.add('on'); clearTimeout(el._t); el._t = setTimeout(() => el.classList.remove('on'), 1500) }

// 브리핑 넘기기: 탭·화살표·밀기·키보드
let bi = 0
const go = (i) => {
  bi = (i + 2) % 2
  $('#bfTrack').style.transform = `translateX(-${bi * 100}%)`
  $$('[data-bf]').forEach((b) => b.classList.toggle('on', +b.dataset.bf === bi))
  $$('.pm-dots i').forEach((d, k) => d.classList.toggle('on', k === bi))
}
$$('[data-bf]').forEach((b) => (b.onclick = () => go(+b.dataset.bf)))
$$('[data-bfd]').forEach((b) => (b.onclick = () => go(bi + +b.dataset.bfd)))
const view = $('#bfView')
let sx = null, moved = false
view.addEventListener('pointerdown', (e) => { sx = e.clientX; moved = false })
view.addEventListener('pointermove', (e) => { if (sx !== null && Math.abs(e.clientX - sx) > 10) moved = true })
view.addEventListener('pointerup', (e) => { if (sx === null) return; const dx = e.clientX - sx; sx = null; if (Math.abs(dx) > 40) go(bi + (dx < 0 ? 1 : -1)) })
view.addEventListener('click', (e) => { if (moved) { e.stopPropagation(); moved = false } }, true)
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') close()
  if (e.target.tagName === 'INPUT') return
  if (e.key === 'ArrowLeft') go(bi - 1)
  if (e.key === 'ArrowRight') go(bi + 1)
  if (e.key === '/') { e.preventDefault(); $('.pm-search input').focus() }
})

document.addEventListener('click', (e) => {
  const s = e.target.closest('.pm-tstar')
  if (s) { s.classList.toggle('on'); s.textContent = s.classList.contains('on') ? '★' : '☆'; return }
  const ok = e.target.closest('.pm-ok')
  if (ok) { ok.classList.add('done'); ok.textContent = '선정됨 ✓'; return }
  const m = e.target.closest('.pm-tools [data-m]')
  if (m) { $$('.pm-tools [data-m]').forEach((x) => x.classList.toggle('on', x === m)); document.body.classList.toggle('sel', m.dataset.m === 'sel'); return }
  if (e.target.closest('#pmNoFav')) { const on = document.body.classList.toggle('no-fav'); e.target.classList.toggle('on', on); render(on); go(on ? 1 : 0); return }
  if (e.target.closest('.pm-btn')) return
  const o = e.target.closest('[data-open]')
  if (o) { e.preventDefault(); open(o.dataset.open); return }
  if (e.target.closest('.pm-ev')) { toast('근거 자료·기록으로 이동 (시안)'); return }
  const g = e.target.closest('[data-go]')
  if (g) { e.preventDefault(); toast(`${g.dataset.go} 상세 화면으로 이동 (시안)`); return }
  const c = e.target.closest('.pm-chip')
  if (c) { const grp = c.dataset.g; $$(`.pm-chip[data-g="${grp}"]`).forEach((x) => x.classList.toggle('on', x === c)) }
})

render(false)
tableRows()
})()
