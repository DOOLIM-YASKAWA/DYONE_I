namespace DyOne.Spec.Dept;

/// <summary>
/// 부서(역할)별로 사양서에서 확인할 항목. 사양정리 초안을 만든 뒤 '부서별 확인' 탭에서 역할마다 카드로 보여 준다.
/// 근거: 그룹웨어 결재·앱, NAS 부서 이슈·클레임, ERP 실행예산 틀을 읽어 정리한 '조직도별 사양서 중요 항목'(2026-10-28).
/// - Rows: 사양정리 초안 줄(DraftLayout.RowKeys) — 그 줄 값·근거 쪽을 함께 보여 준다.
/// - Keywords: 사양서 본문에서 근거 쪽을 찾을 정규식(대소문자 무시).
/// - Incident: 같은 유형의 과거 사고(⚠). AskIfMissing: 사양서에 없으면 "사양서에 없음 — 확인 필요"로 띄운다(사고가 잦던 항목).
/// 역할 키는 바뀌지 않는 이름이고, 부서 이름(Dept)은 임시 연결이다 — 역할↔부서 연결표('부서 자동 부여')가 생기면 그쪽을 쓴다.
/// </summary>
public sealed record DeptItem(string Id, string Title, string[] Rows, string[] Keywords, string? Incident = null, bool AskIfMissing = false);

public sealed record DeptRole(string Key, string Dept, string Group, string Does, string? Budget, DeptItem[] Items);

public static class DeptChecklist
{
    private static DeptItem I(string id, string title, string[] rows, string[] kw, string? incident = null, bool ask = false) =>
        new(id, title, rows, kw, incident, ask);

    private static readonly string[] None = [];

    public static readonly DeptRole[] Roles =
    [
        // ---------------- 영업사업부
        new("sales.paint", "도장영업팀", "영업사업부", "입찰·실행예산 보고, 장납기 선발주, 사양서 접수", null,
        [
            I("scope", "공사 범위 경계·고객 공급분", None, [@"공급\s*범위", @"업무\s*분장", @"공사\s*범위", @"주설비", @"1차\s*(유틸|전원|접지)", @"(고객|발주처)\s*(측|공급|제공)"],
                "범위 누락(경계가 흐린 하부보강·타공·에어·전원)", true),
            I("process", "공정·신설/교체·로봇 대수", ["Robot"], [@"신설", @"교체", @"\d+\s*대"]),
            I("commercial", "보증·검수·지체보상·FAC 조건", None, [@"보증", @"검수", @"지체", @"FAC"]),
            I("delivery", "인도조건", None, [@"D\.?D\.?P", @"인도\s*조건", @"CIF", @"FOB"]),
            I("schedule", "SOP·셧다운 기간", ["Schedule"], [@"SOP", @"셧다운", @"shut\s*down", @"시험\s*생산"]),
            I("permit", "PSM·인허가·안전 비용", None, [@"PSM", @"인허가", @"안전\s*관리비"]),
            I("deviation", "사양과 우리 제안이 다른 부분(사양 차이 표기)", None, [@"사양\s*차이", @"상이", @"deviation", @"고객\s*사양.{0,10}우선"]),
            I("change", "기존 대비 변경점", None, [@"기존\s*대비", @"변경\s*(사항|점)", @"기설치"], "변경점 미공지(PCF→DCM)", true),
            I("spare", "스페어 요구", None, [@"스페어", @"spare", @"예비\s*품"], "스페어·케이블 누락(동희 SPC)", true),
            I("attach", "별지·별첨 문서 확보", None, [@"별지", @"별첨", @"별도\s*(문서|자료)"], "사양서 밖 문서로 범위 확대"),
        ]),
        new("presales.review", "선행검토팀", "영업사업부", "공정 검토(앱 341)에 사양을 가장 먼저 기록", null,
        [
            I("uph", "UPH·사이클타임·바디 피치", ["Basic Condition"], [@"UPH", @"JPH", @"C/T", @"cycle\s*time", @"피치", @"pitch"]),
            I("conveyor", "컨베이어 형식·속도", None, [@"컨베이어", @"conveyor", @"stop\s*&\s*go", @"연속\s*(이송|타입|방식)?"]),
            I("booth", "부스 크기", ["Booth size"], [@"부스\s*(길이|폭|크기|사이즈|치수)", @"booth\s*size"]),
            I("body", "차종·옵션 수·차량 최대 제원", ["Body Size"], [@"차종", @"제원", @"body\s*size", @"전장|전폭|전고"]),
            I("material", "도포 재료 물성(점도)", ["Paint"], [@"점도", @"비중", @"viscosity"]),
            I("vision", "비전 요구", None, [@"비전", @"vision"]),
            I("restrict", "설치 제한 조항(문제 장비 설치 불가 등)", None, [@"설치\s*불가", @"사용\s*불가", @"사용\s*금지", @"적용\s*금지"]),
        ]),
        new("sales.auto", "자동화영업팀", "영업사업부", "실러·데드너·샌딩·폴리싱·검사 자동화 입찰·선발주", null,
        [
            I("scope", "자동화 공정 범위(실러·샌딩·검사)", None, [@"실러", @"sealer", @"데드너", @"샌딩", @"sanding", @"폴리싱", @"와이핑", @"제진"]),
            I("material", "도포 재료 물성(실러 점도 → 모터 용량)", None, [@"실러.{0,20}점도", @"점도"]),
            I("vision", "비전·검사 요구", None, [@"비전", @"vision", @"검사\s*(장치|시스템)"]),
        ]),
        new("sales.gi", "GI영업실", "영업사업부", "범퍼 로딩·마스킹 등 비자동차 자동화", null,
        [
            I("maker", "고객 표준 Maker 리스트", None, [@"메이커", @"maker", @"표준\s*(부품|사양)"]),
            I("cert", "인증·통관 조건", None, [@"인증", @"통관"]),
            I("material", "제품 재질·색", None, [@"재질", @"RAL\s*\d+", @"색상"]),
            I("edu_spare", "교육·스페어 포함 여부", ["Training"], [@"교육", @"스페어"], "빠지면 별도 협의", true),
            I("partner", "협력사 설치 범위", None, [@"협력\s*(사|업체)", @"설치\s*범위"]),
        ]),
        new("cs", "CS팀·ES영업팀", "영업사업부", "A/S·클레임(앱 281), 고객 로봇 리스트(앱 294), 스페어 판매", "PROJECT SPARE · EMERGENCY PART",
        [
            I("warranty", "설비보증 기간(사내 기준과 차이)", None, [@"보증"], "사내 기준(1년 또는 설치 후 18개월)과 다름"),
            I("life", "장비수명·부품 공급 기간", None, [@"수명", @"단종", @"공급\s*보장", @"\d+\s*년\s*(이상|간)\s*공급"]),
            I("accept", "예비·최종 검수(CS 인수 시점)", None, [@"예비\s*검수", @"최종\s*검수", @"error[- ]?free", @"가동률"]),
            I("spare", "스페어 리스트·비상 대응 파트", None, [@"스페어", @"spare", @"비상\s*(대응|용)\s*(파트|부품)?", @"소모품"], null, true),
            I("initdata", "로봇 초기 데이터(접지·절연·부하율)", None, [@"절연\s*저항", @"접지\s*저항", @"부하율", @"초기\s*데이터"]),
            I("existing", "교체 공사면 기설치 이력 대조(앱 294)", None, [@"기설치", @"기존\s*설비", @"노후\s*교체"]),
        ]),

        // ---------------- 기술부
        new("pm", "PM팀", "기술부", "사양정리 SHEET 작성, 설치·공사 관리", "공수관리 PM/SM · 현장설치 · PM관리비용",
        [
            I("schedule", "공사 일정·셧다운·티칭 가능 시간·SOP", ["Schedule"], [@"SOP", @"셧다운", @"시험\s*생산", @"마스터\s*스케줄", @"일정"]),
            I("longlead", "장납기 품목(로봇·RC·7축·케이블)", ["Robot"], [@"7\s*축", @"레일\s*하우스", @"납기", @"\d+\s*m\s*케이블"], "2026 프로젝트 48%가 로봇 입고 이슈", true),
            I("carryin", "반입로·반입 방법·보양 주체", None, [@"반입", @"해포", @"보양", @"양중", @"운송"], null, true),
            I("supply", "고객 제공 범위", None, [@"(고객|발주처|기아|현대)\s*(측|에서)?\s*(제공|공급|지급)", @"테스트\s*바디", @"티칭\s*바디"]),
            I("deliver", "납품물(백업·도면·매뉴얼·교육)", None, [@"백업", @"backup", @"최종\s*도면", @"매뉴얼", @"제출\s*(서류|자료|일정)"]),
            I("standby", "스탠바이·상주 조건", ["Stand by"], [@"stand[- ]?by", @"스탠바이", @"상주", @"양산\s*(대응|지원)"]),
            I("safety", "안전기능품·공사 안전", None, [@"라이트\s*커튼", @"비상\s*정지", @"안전\s*작업\s*허가", @"공사\s*안전"]),
            I("lineloss", "라인 정지 손실 조항", None, [@"라인\s*정지", @"생산\s*손실", @"중대\s*문제", @"\d+\s*분\s*이상\s*정지", @"손해\s*배상"], "209분 정지로 약 3,950만 원 청구", true),
        ]),
        new("modular", "모듈라팀", "기술부", "모듈라빌드(로봇 기종·케이블·통신·도장기), FGP·OLT", "모듈라 빌드 기계·전기·주행장치 · 공수 모듈라",
        [
            I("ccv", "CCV 상세(칼라 수·A/B 포트·덤프 에어·인서트)", ["Color(CCV)"], [@"CCV", @"칼라\s*밸브", @"color\s*valve", @"듀얼|dual", @"싱글|single"], "CCV 타입 혼재 — HMGMA 23개 교체"),
            I("fgp", "FGP 구성·압력센서 범위", ["FGP"], [@"FGP", @"기어\s*펌프", @"gear\s*pump", @"압력\s*센서"]),
            I("cleaner", "세정장치 범위(오프너 세정 포함 여부)", ["Bell Cleaner"], [@"세정\s*장치", @"cleaner", @"세정기"]),
            I("atomizer", "도장기 P/N·버전·방폭", ["Atomizer"], [@"도장기\s*(사양|모델|버전)", @"atomizer", @"정전\s*벨", @"P/N"]),
            I("export", "수출 여부(방청·패킹)", None, [@"포장", @"패킹", @"packing", @"방청", @"수출"]),
            I("olt", "OLT 진행·사전 검증", None, [@"OLT", @"사전\s*검증"]),
            I("fmea", "FMEA·설비 개선 요구", None, [@"FMEA", @"강건화"], "과거 클레임과 같은 유형"),
        ]),
        new("teach", "어플리케이션팀", "기술부", "신차 티칭·양산 대응", "공수 티칭",
        [
            I("body", "차종·제원·옵션·신차 일정·도장 부위", ["Body Size"], [@"차종", @"제원", @"도장\s*부위", @"신차"]),
            I("quality", "품질 기준(막두께 등)", None, [@"막\s*두께", @"도막", @"㎛|μm|um\b", @"선명도", @"품질\s*(기준|목표)"], "기준이 없으면 품확 부담이 큼", true),
            I("paint", "수성/유성·1K/2K·하전·칼라체인지", ["Paint", "Color(CCV)"], [@"수성|유성", @"1K|2K", @"하전", @"칼라\s*체인지|color\s*change"]),
            I("opener", "오프너 사양·바디 안착 편차", None, [@"오프너", @"opener", @"안착", @"편차"], "안착 편차 ±30mm로 NG"),
            I("degrade", "디그레이드·비상운전 수준", None, [@"디그레이드", @"degrade", @"비상\s*운전"]),
            I("teaching", "티칭 조건·기간", None, [@"티칭", @"teaching", @"TCP"]),
        ]),
        new("process.review", "공정검토팀", "기술부", "시뮬레이션·공정 검토(로봇 대수·부스 길이)", "공수 티칭",
        [
            I("sim", "시뮬레이션·OLP 요구", None, [@"시뮬레이션", @"simulation", @"OLP", @"오프라인"]),
            I("layout", "부스 치수·로봇 배치", ["Booth size", "Robot"], [@"부스\s*(길이|폭)", @"배치", @"layout"]),
            I("uph", "UPH·피치(시뮬레이션 입력)", ["Basic Condition"], [@"UPH", @"C/T", @"피치"]),
        ]),
        new("control", "소프트웨어팀", "기술부", "공정 PLC 표준 프로그램, 트래킹·인터록, 시운전", "공수 시스템",
        [
            I("plc", "PLC 메이커·기종·고객 표준 프로그램", ["System"], [@"PLC", @"지멘스|siemens", @"미쯔비시|mitsubishi", @"allen|\bAB\b", @"\bLS\b"]),
            I("fieldbus", "필드버스·통신", None, [@"profinet", @"ethernet/?ip", @"CC-?Link", @"필드\s*버스", @"이더넷\s*통신", @"통신\s*카드"]),
            I("tracking", "컨베이어·엔코더 공급 주체·L/S 위치", None, [@"엔코더", @"encoder", @"트래킹", @"tracking", @"L/S", @"리미트\s*스위치", @"펄스"], "화성2 SOP 후 68분 정지", true),
            I("vehicle", "차종 수신 방식(MIP·IVDS·바코드)", None, [@"MIP", @"IVDS", @"차종\s*(정보|입력|인식|비전)", @"바코드"]),
            I("interlock", "인터록(화재·공조·비상정지·PCS)", None, [@"인터록", @"interlock", @"소화\s*설비", @"공조", @"하드\s*와이어"]),
            I("mes", "MES 연동·고객 IT 정책", None, [@"MES", @"상위\s*(시스템|서버)"]),
            I("ups", "UPS/AVR 허용 여부", None, [@"UPS", @"AVR"], "광명2는 UPS 금지"),
            I("docs", "알람리스트·백업·매뉴얼", None, [@"알람", @"alarm", @"백업", @"고스트"]),
        ]),
        new("design", "설계팀", "기술부", "현장 실측, 부스·설치 공간 설계, 변경관리(PSM)", "공수 설계 · 현장설치 기계·전기",
        [
            I("layout", "레이아웃·간섭물(기둥·덕트·지관)", None, [@"레이아웃", @"layout", @"간섭", @"기둥", @"덕트"], "하계 이슈 1위 — 설계 간섭", true),
            I("mount", "로봇 거치(Floor/Wall)·7축·레일", ["Robot"], [@"벽\s*걸이", @"wall", @"floor", @"7\s*축", @"레일"]),
            I("structure", "부스 구조·건축 레벨·하부보강", None, [@"하부\s*보강", @"그레이팅", @"레벨", @"하중"], "레벨 오차 25mm로 작업량 2~3배", true),
            I("panel", "판넬 위치·판넬룸·바닥 덕트", ["Panel"], [@"판넬\s*룸|패널\s*룸", @"(제어반|판넬|패널)\s*(위치|배치|설치)", @"바닥\s*덕트"]),
            I("fab", "현장 제작품(레일하우스·데크·안전펜스·커버)", None, [@"레일\s*하우스", @"데크", @"안전\s*펜스", @"낙수", @"사다리"]),
            I("piping", "배관·호스 설계", None, [@"배관", @"호스", @"피팅", @"STS"]),
            I("wall", "벽체 타공", None, [@"타공"]),
        ]),
        new("site", "호남·영남지사", "기술부", "상주 사업장 티칭·시운전·교체 공사", "공수 STAND BY · 휴일근무",
        [
            I("removal", "기존 설비 철거·재활용 범위", None, [@"철거", @"재활용", @"재사용", @"기존\s*설비"]),
            I("air", "에어 압력(현장 실측값)", ["Utility"], [@"에어\s*압", @"dry\s*air", @"kg\s*/?\s*㎠|kgf", @"\d\s*bar"], "에어 4.8~7.3bar로 하루 5~6회 정지", true),
            I("di", "D.I Water 압력", None, [@"순수", @"D\.?I\.?\s*water", @"탈이온"]),
            I("power", "1차 전원 공급점·접지", None, [@"전원", @"VSP", @"AC\s*\d{3}\s*V", @"접지"]),
            I("mass", "양산·라인 대응 M/D, 콜·신차 티칭 포함", ["Stand by"], [@"양산\s*(대응|지원)", @"M/D", @"상주"]),
            I("handover", "인수인계 자료", None, [@"인수\s*인계", @"인계"]),
        ]),

        // ---------------- 사업기획부
        new("paint.lab", "도장공정실(공정운영팀)", "사업기획부", "랩 테스트, 벨컵·쉬라우드 사양 선정", "LAB TEST",
        [
            I("paint", "도료 종류·점도", ["Paint"], [@"점도", @"도료\s*(종류|조건|사양)", @"비중"]),
            I("atomizer", "도장기·벨컵 사양", ["Atomizer"], [@"벨\s*컵|bell\s*cup", @"쉐이핑|셰이핑|shaping", @"RPM", @"고전압"]),
            I("quality", "막두께·품질 기준", None, [@"막\s*두께", @"도막", @"㎛|μm"], null, true),
            I("labtest", "LAB TEST 포함 여부", None, [@"LAB\s*TEST", @"랩\s*테스트", @"도료\s*테스트"]),
            I("standby", "Stand by 조건", ["Stand by"], [@"stand[- ]?by"]),
        ]),
        new("overseas", "해외법인", "사업기획부", "중국·미국·멕시코·체코·인도 현지 수행", null,
        [
            I("lang", "업무 언어", None, [@"영문", @"영어", @"english", @"한국어", @"한글"]),
            I("delivery", "인도조건", None, [@"D\.?D\.?P", @"CIF", @"FOB", @"인도\s*조건"]),
            I("cert", "현지 법규·방폭 인증(ATEX/KCS)", None, [@"ATEX", @"KCS", @"\bUL\b", @"\bCE\b"]),
            I("interpret", "통역·현지 교육", None, [@"통역"]),
        ]),

        // ---------------- 연구소
        new("research.spc", "시스템SW팀", "연구소", "SPC 도장 제어기·Smart View HMI·EVO 연동", null,
        [
            I("atomizer", "도장기 종류·수량(벨·EVO·2K·듀얼)", ["Atomizer"], [@"도장기", @"EVO", @"2K", @"듀얼|dual", @"정전\s*벨"]),
            I("control", "쉐이핑 에어·고전압·토출량 제어", None, [@"쉐이핑|셰이핑|shaping", @"고전압", @"토출량", @"터빈"]),
            I("hmi", "HMI 메이커·해상도·다국어·알람 화면", None, [@"HMI", @"MMI", @"다국어", @"표시\s*언어"]),
            I("ex", "방폭 조건·SPC 설치 공간", None, [@"방폭", @"본질\s*안전", @"SPC"]),
            I("cc", "칼라체인지·세정 시퀀스", ["Color(CCV)"], [@"칼라\s*체인지|color\s*change", @"세정\s*(시퀀스|액)"]),
            I("auth", "파라미터 권한·이력", None, [@"패스워드|password", @"권한", @"수정\s*이력"]),
        ]),
        new("research.smart", "스마트SW팀", "연구소", "모니터링·예지보전·자동경로·SRS·DY ONE", null,
        [
            I("monitor", "모니터링·예지보전·생산 이력", None, [@"모니터링", @"monitoring", @"예지\s*보전", @"가동\s*현황", @"생산\s*(이력|현황)"]),
            I("network", "네트워크(FA망·OA망)·서버 위치·원격 접속", None, [@"FA\s*망", @"OA\s*망", @"네트워크", @"서버", @"원격"]),
            I("data", "고객 PLC·로봇 데이터 제공 범위", None, [@"토출량", @"데이터", @"축\s*부하"]),
            I("license", "라이선스", None, [@"라이선스|라이센스|license"]),
            I("sim", "오프라인 시뮬레이션", None, [@"OLP", @"시뮬레이션"]),
        ]),

        // ---------------- 경영관리부
        new("purchasing", "구매팀", "경영관리부", "내자·외자 발주 전부(로봇 거래처 3곳, 구매품 138곳)", "공정별 설비 · ROBOT SYSTEM 구매품",
        [
            I("robot", "로봇 통신·방폭·7축·케이블 길이", ["Robot"], [@"방폭\s*(로봇|6축)?", @"가반", @"반복\s*정밀도", @"7\s*축", @"로봇.{0,10}케이블"], "로봇 장납기 — 일찍 확정해야 Frame Order 지연 없음"),
            I("maker", "고객 지정 메이커", None, [@"SMC", @"omron", @"weidmuller", @"advantech", @"메이커|maker", @"동등\s*(이상|품)"]),
            I("pn", "사양마다 다른 품번(압력센서·CCV 타입·PLC 모듈)", ["FGP", "Color(CCV)"], [@"압력\s*센서", @"통신\s*카드", @"모듈"]),
            I("spare", "스페어 범위·장기 공급", None, [@"스페어", @"단종", @"\d+\s*년.{0,6}공급"], "스페어 누락(GMK 부평 발주 누락)"),
            I("cert", "인증품(KC·KCS·ATEX)", None, [@"KC\s*인증", @"인증품", @"KCS", @"ATEX"]),
        ]),
        new("finance", "재무팀", "경영관리부", "매출·충당·지체보상 관리", null,
        [
            I("delay", "지체보상금(율·상한)", None, [@"지체", @"0\.1\s*%", @"상한"]),
            I("warranty", "하자보수 충당(보증 기간)", None, [@"보증", @"하자"]),
            I("fac", "FAC 발행 조건(매출 시점)", None, [@"FAC", @"최종\s*검수"]),
            I("safetycost", "안전관리비 사용 내역", None, [@"안전\s*관리비"]),
            I("export", "수출 여부", None, [@"수출", @"CIF", @"FOB", @"D\.?D\.?P"]),
        ]),
        new("admin", "총무팀", "경영관리부", "현장 숙소·출장·보안·안전서류", "공사수행경비",
        [
            I("lodging", "현장 숙소·교통 부담", None, [@"숙소", @"숙박", @"교통", @"편의\s*시설"]),
            I("security", "보안서약·자료 반납", None, [@"보안", @"서약", @"반납"]),
            I("safety", "안전 조직·산재보험·보호구", None, [@"안전\s*관리자", @"화기\s*감시", @"신호수", @"산재", @"보호구", @"ISO\s*45001"]),
            I("meeting", "회의 언어·회의록", None, [@"회의록", @"회의는"]),
        ]),
        new("education", "교육실", "경영관리부", "고객 교육 수행·결과 보고", "교육(CS)",
        [
            I("scope", "교육 범위(생산/보전, 업체·현장)", ["Training"], [@"교육\s*(센터|기간|범위|일정|계획)", @"보전\s*\d+\s*주", @"생산\s*\d+\s*주"]),
            I("fac", "FAC 조건의 교육 완료", None, [@"교육\s*완료", @"교육.{0,6}완료"]),
            I("maint", "보전 담당자 별도 교육", None, [@"보전\s*(담당|교육)"]),
            I("logistics", "통역·숙박·교통 부담", None, [@"통역", @"숙박", @"편의\s*시설"]),
            I("equipment", "교육용 장비", None, [@"교육용"]),
        ]),
    ];
}
