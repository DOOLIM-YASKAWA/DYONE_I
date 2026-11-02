# 연구소(시스템SW팀·스마트SW팀) / 기술부 소프트웨어팀 — NAS 자료 분석 (2026-10-28)

- 원칙: 네트워크 드라이브는 읽기만 했음. 텍스트 추출 사본은 `C:\Users\112109\AppData\Local\Temp\claude\E--dy-one\adc750e3-9acf-46b5-8919-56aa025449aa\scratchpad\deptSW\` 에만 저장.
- IP 주소, 계정·비밀번호, 개인 정보(특허 발명자 주소 등)는 옮기지 않았음.
- 근거 표시: (Z:…) 같은 경로. 자료로 확인되지 않은 해석은 **추정**이라고 적음.

---

## 1) 읽은 경로 목록

### Z:\ (\\192.168.30.100\연구소)
| 구분 | 경로 | 읽은 방식 |
|---|---|---|
| 스마트SW팀 | Z:\스마트SW팀 (2단계 폴더 구성) | 구성 |
| | …\모니터링시스템\소개자료\SPSV2.5_소개자료(+교육 내용).pptx | 본문 |
| | …\모니터링시스템\설치 파일 (SPSCollector / SPSDataCollector / SPSMaster v1.0.2.0) | 파일명 |
| | …\모니터링시스템\적용\국내\한국 지엠 Plc address 확인 요청.xlsx | 본문 |
| | …\모니터링시스템\적용\해외\HMGMA\HMGMA Enet_IP_240829.xlsx | 구조만 |
| | …\모니터링시스템\Database 백업 (광명2·광주2·화성4, 월별) | 구성 |
| | …\모니터링시스템(파형측정)\수집 데이터 (GP8/GP20HL/GP25/MPX2600/3500/3600) | 구성 |
| | …\OpenDAY_202510\2025 DY 오픈데이 모니터링시스템.pptx, 파형측정시스템.pptx | 본문 |
| | …\IREX\2025 IREX 박람회 보고서 (시스템SW팀).pptx | 본문 |
| | …\자동경로생성_MAP\20. 자동경로생성_보전교육 자료\1. 자동경로생성_교육자료.pptx | 본문 |
| | …\자동경로생성_MAP\자기부 사점 검수 관련 (26.07.13-14)\DY 샌딩,폴리싱 패턴 생성 1차 검증 리스트_260708.pptx | 본문 |
| | …\자동경로생성_MAP\(화성4 전착·상도, 울산52, 화성5, Setting\MapManager_261026.zip) | 구성 |
| | …\Motosim Plugin 개발관련_어플팀 공유자료, Motosim2024/2025, 좌표변환 프로그램, ULSAN52, Chatbot, RISystem, rl, TEST-모니터링, 설치프로그램 | 구성 |
| 시스템SW팀 | Z:\시스템SW팀 (2단계 폴더 구성) | 구성 |
| | …\RC 설치 SPC 사이트 사진\SPC 대체품 설치안내 PPT\[기아 화성4] SPC 대체품 설치 안내.pptx (외 12개 고객사 동일 양식 파일명) | 본문 |
| | …\오재희\evo\1. NEW SAMES HMI.pptx, 1.KMM_EVO_HMI_.pptx, EVO 교육 시리즈(파일명) | 본문/파일명 |
| | …\오재희\CodesysDYDevice, HMMI(HMI 백업), 사우디 중도 HMI 백업, 경주에코클리어 백업, 설치 파일(CODESYS·Profinet GSD), 전장품 매뉴얼, FTPServer | 구성 |
| | …\RC 설치 SPC 사이트 사진\MPX3600 FGP 내장모터_U축 방폭규정.pptx | 열기 실패(파일 손상 또는 형식 문제) |
| 공통 | Z:\SW카달로그\연구소 SW 카달로그.pptx, 도장기_라인업.pptx | 이미지 위주라 제목만 확인 |
| | Z:\SmartCell\smartcell_2차공청회자료.zip (목록 + content.md) | 본문 |
| | Z:\DYBridge\logs\bridge.log (앞부분) | 본문 |
| | Z:\DYONE_I | 비어 있음 |
| | Z:\현대기아 비젼 Framework (Framework_Ver1.0_260331.locked + Unlock.exe) | 파일명 |
| | Z:\알람 조치 매뉴얼 양식\V1\HMI_Alarm_Description_KO_ALL.pptx | 파일명(M: V2로 본문 확인) |
| | Z:\CS점검자료 (고객 공장별 빈 폴더 + csv) | 구성 |
| | Z:\국책과제\ATC+\ATC+실태조사관련\ATC+ 실태조사 발표자료\ SmartLAB / SRSAnalyzer / SRSCreator / 도막 프로파일 예측 / IPS / 협업툴 .pptx | 본문 |
| | Z:\특허 출원\PP25-089, PP25-117 특허출원서.pdf | 발명 명칭만 |
| | Z:\안전점검 (2019~2026 연구실 안전점검) | 구성 |
| | Z:\데이터관리 (개인별 R&D 과제 폴더) | 구성 |
| | Z:\데이터관리\황인욱\임베디드\SystemParameter_Setting_Tool.pptx, Alarm,System_Parameter_List.xlsx | 본문 |
| | Z:\데이터관리\황인욱\SPC_알람리스트\SPC Alarm Code.xlsx | 본문 |
| | Z:\데이터관리\박종민\GIT (SPS 배포 구성: LSPlc, Melsec, PostgreSQL, EuresysGrabber 등) | 구성 |
| | Z:\데이터관리\양영하 (autopath, mapmanager, mapdb, motocal, sbx_rl_autopath, smartcell_dev) | 구성 |

### X:\ (\\192.168.30.103\기술부Dev)
| 경로 | 내용 |
|---|---|
| X:\2024\KIA\(KIA GEORGIA 1, KIA HWASEONG 2), X:\2024\PLAKOR\PLAKOR CZECH 1 | 고객사 → 공장 → 부스 → 존 → DLP 번호 폴더(파일 없음) |
| X:\STORAGE (121개 폴더) | `고객 공장 라인 공정 존\DLP-xx-xxx\날짜시각\SRS_BACKUP_*.zip + .ini` — 로봇 잡·도장 파라미터 백업 저장소. 동희오토 서산, GGM 광주, GM 부평, 현대 앨라배마 등. 최근 갱신 2026-08~09 |
| X:\FTP | 비어 있거나 권한 없음 |

### Q:\ / M:\
| 경로 | 읽은 내용 |
|---|---|
| Q:\소프트웨어팀\시스템 표준화 작업\1. 문서\시스템&알람 파라미터 용도 정리_260714.xlsx, 시스템 알람 표준화 리스트_260401.xlsx | 본문 |
| Q:\소프트웨어팀\시스템 표준화 작업\2. PLC (Siemens_Standard_260331.zap20), 담당자별 폴더(MELSEC 등) | 파일명 |
| Q:\소프트웨어팀\시스템 표준 문서\일정표\소프트웨어실 매뉴얼 작성 일정 표_20260930.xlsx | 본문 |
| Q:\소프트웨어팀\시스템 표준 문서\매뉴얼 트리 (AB, Atlas Copco, CIMON, Crevis, Doolim, Festo, Graco, Higen, LS, Mitsubishi, Omron, Pick it, PILZ, PSMP …) | 구성 |
| Q:\소프트웨어팀\시운전 절차서\메뉴얼\박승혁\시운전 절차서_2020.04.28.pptx (+ 항목별 절차서 파일명) | 본문 |
| Q:\소프트웨어팀\시운전 결과보고서\2022년\국내\광주1공장 중도 외판\시운전결과보고서_광주1공장_중도외판_2022.10.pptx | 본문 |
| Q:\소프트웨어팀\HMGMA\하도 메뉴얼\PCF Setup 매뉴얼.pptx (+ AB PLC, SCA, YRC1000 EIP 파일명) | 본문 |
| Q:\소프트웨어팀\EVO 백업 교육_230606 (고스트 백업, PLC·HMI·파라미터 파일 백업, TIA Portal Archive) | 구성 |
| Q:\Software (MotoSimEG-VRC 2022/2023, Sentinel 드라이버) | 파일명 |
| M:\소프트웨어팀 (지멘스 TIA 17 설치 파일) | 구성 |
| M:\DY 프로젝트 IP리스트(모니터링시스템)\최근\네트워크IP할당.xlsx, 동희오토 ENet_IP.xlsx (+ 아산·울산·전주·HMGMA·KMS·MSK·PCZ 파일명) | 구조만 |
| M:\SPC_알람조치_매뉴얼_개선\V2\HMI_Alarm_Ver2.pptx (199장) | 본문 |
| M:\HMI 디자인 자료\HMI(Smart View).pptx, HMI_Design_psd 폴더 구성 | 본문/구성 |
| M:\연구소_ChatBot 자료 (부서별 폴더) — 시스템기술실\1. 매뉴얼\Data Tracking Manaul\DY-SW-O-0-010_Zone Tracking Method.pptx, Software Team Manual\12.HMI 메뉴얼.pptx | 구성 + 본문 2건 |

---

## 2) 팀별 하는 일

### 2-1. 연구소 시스템SW팀 — 도장 제어기(SPC)·HMI·도장기 연동 (제어 쪽 제품)
| 영역 | 내용 | 근거 |
|---|---|---|
| **SPC(도장 제어기, 임베디드)** | 로봇마다 붙는 도장기 제어 장치. 고객사별 SPC 단종·대체품(OEM19-005/OEM23-029 → OEM25-037) 안내서를 13개 현장(HMGMA, 기아 화성3·4, CKD, KCN, 서연이화, 에코 사바나, 영산글로넷, 진성S&P, 아이아·프라코 당진, 경주 에코플라스틱, ADM)에 배포. 대체품은 깊이 +5mm, 높이 +15.4mm로 커져 설치 공간 확인 필요 | Z:\시스템SW팀\RC 설치 SPC 사이트 사진\SPC 대체품 설치안내 PPT |
| SPC 파라미터·알람 | 알람 코드 체계: 로봇 0~9999, SPC 운영 10000~, 도장기 11000~, 통신 12000~(main PLC·솔블록·GNM300), SPC 데이터 13000~, 공정 PLC 인터록 20000~, 데이터 트래킹 21000~. 알람 기준치 DB(메인/나노 에어 압력, 베어링 에어, 터빈 허용 오차·최대 60krpm, 셰이핑 에어 최대 500L/min 등) | Z:\데이터관리\황인욱\SPC_알람리스트\SPC Alarm Code.xlsx, …\임베디드\Alarm,System_Parameter_List.xlsx |
| SPC 설정 도구 | System Parameter Setting Tool: PC↔IPC 파라미터 다운로드/업로드, 다른 로봇 동기화, 이력·복원, 다국어 | …\임베디드\SystemParameter_Setting_Tool.pptx |
| CODESYS 런타임 | DOOLIM 전용 CODESYS V3.5.20 x64 디바이스, Profinet GSD — SPC/IPC 소프트 PLC 기반으로 **추정** | Z:\시스템SW팀\오재희\CodesysDYDevice, 설치 파일 |
| **HMI(Smart View)** | 공정 HMI. 화면: home/zone/booth/atomizer/switch/parameter/position/ghost/fieldbus/interlock/manual/program. 1280×986 기본, 1920×1080 와이드. 고객별(GGM, IKCO, HMC 하도마스킹, Cosmetic) 디자인. 현장 HMI 백업(사우디 중도, 경주 에코 클리어, HMMI 인도네시아) | M:\HMI 디자인 자료, Z:\시스템SW팀\사우디 중도 HMI 백업, …\오재희\HMMI |
| **EVO(SAMES AccuBell/Booster) 연동** | SAMES EVO HMI(Easy Tables/Events/Sequences) 운용, 컬러체인지·세정 시퀀스 23종, EVO IPC↔ET200SP Profinet 알람, 고장 진단 흐름도 | Z:\시스템SW팀\오재희\evo |
| **알람 조치 매뉴얼** | HMI에 띄우는 알람 조치 화면(코드·플로우차트·점검 위치·도면 링크). V1(9월) → V2(10/27) 개선 중. ATOM(BSC300 터빈), EVO IPC CV 알람 등 199장 | Z:\알람 조치 매뉴얼 양식\V1, M:\SPC_알람조치_매뉴얼_개선\V2\HMI_Alarm_Ver2.pptx |
| 전장품 자료 | 압력센서(내압방폭), 버튼, 에어히터, Autonics·Festo·SMC·Omron·LS·Higen·Schneider·TURCK·WAGO·SAMES 등 매뉴얼 | Z:\시스템SW팀\전장품 매뉴얼 |
| 신기술 조사 | IREX 2025: NVIDIA Jetson Thor AI IPC, 블록리(Blockly) 기반 시퀀스 UI(에코프로EM용 제작 중) | Z:\스마트SW팀\IREX\2025 IREX 박람회 보고서 (시스템SW팀).pptx |

### 2-2. 연구소 스마트SW팀 — 데이터·AI·자동화 SW (부가 제품)
| 제품/과제 | 내용 | 근거 |
|---|---|---|
| **모니터링 시스템 (SPS V2.5 / Mate.D Monitoring)** | 웹 기반 도장공장 예지보전. 라인/존 모니터링, 로봇 알람, 예지보전(토크 적산·평균, 모터 온도, 도장기 상태), 하드웨어 수명, 생산 이력(컨베어 OFF·스프레이 사이클·H.V ON·Gun ON·토출량 cc), **VIN 기준 조회**. 구성: SPSCollector·DataCollector·Master + PostgreSQL, LS/Melsec PLC 드라이버. "Edge computing", **라이선스 모델** 있음. 적용: 기아 광주2·소하리1·2·화성4·광명2, GM 부평, HMGMA, 기아인도, 서연이화 푸네, 에코 | Z:\스마트SW팀\모니터링시스템, OpenDAY_202510, Z:\데이터관리\박종민\GIT |
| 모니터링 현장 적용 작업 | 고객사 PLC 주소 확인 요청서(로봇별 Flow/Turbine/S/A/HV 명령·피드백, FGP A~F 입·출력 압력, Spray On), 프로젝트별 Ethernet IP 맵(PLC·OP PC·RC·M.Server·DBMS, FA망/서버렉망 분리) | …\적용\국내\한국 지엠 Plc address 확인 요청.xlsx, M:\DY 프로젝트 IP리스트(모니터링시스템) |
| **파형 측정 시스템** | 로봇 토크·속도 파형 자동 분석 → 고장 진단, 연 1회 정기점검 리포트. 모델별 수집(GP8/GP20HL/GP25/MPX2600/3500/3600). "DY 고객 지원 통합 구독 서비스"(모니터링+파형+챗봇)로 묶을 계획 | OpenDAY_202510\2025 DY 오픈데이 파형측정시스템.pptx |
| **자동경로생성 M.A.P** (샌딩·와이핑·폴리싱) | CPV 결점 검사 → PICKIT 3D 위치 보정 → MAP Manager(존당 PC 1)·MAP Planner(로봇 2대당 PC 1)가 결점별 최적 경로 생성 → 로봇 전송 → 결점 디스플레이. 현장: 화성4 전착샌딩·상도(폴리싱), 울산52 중도, 화성5, 기아 PBV. 검증 기준: 잡 생성 1초 이내(오물 5점), 인터록 영역, 엣지부 오프셋 | Z:\스마트SW팀\자동경로생성_MAP |
| **Smart Cell / MotoSim Launcher** | 로봇·바디 캘리브레이션(티칭 툴 끝맞춤) → MotoSim 셀 보정 → 잡 변환. Launcher로 존별 셀 업·다운로드·이력·플러그인 업데이트. 검증 현장: 현대 아산 중도 내판, GM 부평 베이스 외판, 동희오토 베이스 외판 | Z:\SmartCell\smartcell_2차공청회자료.zip |
| **DYBridge** | MotoSim 플러그인과 외부(Isaac 시뮬레이션 호스트)를 잇는 파일·COM 브리지 로그. 셀 전송 콘솔 폴링 | Z:\DYBridge\logs\bridge.log |
| SRS 계열 | SRS Creator(도장 궤적 자동 생성, 서울대 PyBullet 기반), SRS Analyzer(웹: 존 3D, 로봇별 토출량·속도 통계), SRS Parameter(도막 프로파일 ↔ 도장 파라미터 예측, 7↔44) | Z:\국책과제\ATC+\…\발표자료 |
| SmartLAB | 시편 자동 도막 측정(로봇+도막측정기) + 랩 테스트 DB·시각화 | 동 SmartLAB_수정.pptx |
| 챗봇 | 알람(로봇·SPC)·매뉴얼(atom·robot·sw)·사내규정·영상 문서 수집 → 사내 챗봇 | Z:\스마트SW팀\Chatbot, M:\연구소_ChatBot 자료 |
| 기타 R&D | 강화학습 자동경로(rl, sbx_rl_autopath), 좌표변환(조지아), 현대기아 비전 Framework(잠금 배포본), IPS 가상 도장 시뮬레이터, 특허 2건(공정 설계 기반 로봇 포스처 그래프 생성 / 다중 로봇 작업 할당 — 서강대 공동) | Z:\데이터관리\양영하, Z:\현대기아 비젼 Framework, Z:\특허 출원 |
| 개발 체계 | Jira·Confluence·Bitbucket(Git) CI/CD | …\협업툴_수정.pptx |

### 2-3. 기술부 소프트웨어팀(시스템기술실 소프트웨어실) — 프로젝트 납품 제어 SW·시운전
| 영역 | 내용 | 근거 |
|---|---|---|
| **공정 PLC 프로그램** | 고객 PLC 기종별 표준 프로그램(Siemens 표준 zap20, MELSEC, AB, LS산전). TIA Portal 17, STEP7 | Q:\소프트웨어팀\시스템 표준화 작업\2. PLC, M:\소프트웨어팀 |
| 시스템 표준화 (2026 진행) | 시스템 파라미터 용도(SH0 로봇 타입·수량, SH2 부스 트래킹·MIP·IVDS, SH4 컨베어 UPH·속도·원피치, SH6~9 부스/존 트래킹, 디그레이드 모드, 슈라우드 클린), 알람 파라미터(AH0 시스템: 인체 감지·컨베어 속도·바디 피치, AH4~13 로봇 L1~R5), E-STOP 알람 표준 문구 | Q:\소프트웨어팀\시스템 표준화 작업\1. 문서 |
| 시스템 레이아웃 표준 문서 | 화성4 기준 외판/내판/EVO 내판/와이핑/내판·무빙·하부 실러, HMGMA 기준 전착·중도 샌딩·상도 검사·외관 측정(LS PLC), PLC 기종별(지멘스·AB·미쯔비시), 장비별(SPC 싱글/듀얼/EVO/2K, PCF+CAPMES, SCA SYS6000·ASC5000+CAPMES+Q-GAGE, CPV+PICKIT+DY 자동경로/EZ로보틱스, BYK+MMS/TERAVIEW) | …\소프트웨어실 매뉴얼 작성 일정 표_20260930.xlsx |
| 데이터 트래킹 | 존/부스 트래킹, Work ID 읽기(Din), 로봇 잡 실행 포인트, 디그레이드 테이블 — 조건 예: UPH 45, 바디 피치 6750mm, CC 15초 | M:\연구소_ChatBot 자료\시스템기술실\…\DY-SW-O-0-010_Zone Tracking Method.pptx |
| HMI·OPD | OPD 버튼(전체 로봇 티칭/플레이, 도장기·고전압 사용, 컨베어 인터록, 수동 세정·컬러체인지, A/B 포트), CIMON HMI 셋업, Paint Studio 설치·셋업, Paint Table | …\Software Team Manual |
| 외부 장비 통신 | HMGMA 하도: Graco PCF(EtherNet/IP 게이트웨이, Swirl), SCA 실러 컨트롤러, AB PLC EDS, YRC1000 EIP | Q:\소프트웨어팀\HMGMA\하도 메뉴얼 |
| **시운전** | 절차서 체크 항목: PC(PLC·HMI·Paint Studio·비전 SW·백신·OS·RAID·키락), PLC 초기화·EDS/GSD, 통신(RC·SPC·EVO·AP·OP·MIP·Vision 스테이션, Camera·Touch Panel·**MES** IP), 인터록(컨베어·부스·화재·믹싱룸), 파라미터(서보앰프·GNM·A/P 압력센서·EVO 세정 시퀀스·펄스 분배기·플로우미터·세이프티 모듈(라이트 커튼·세이프티 PLC)·BSC-300). 모듈라/현장 2단계 | Q:\소프트웨어팀\시운전 절차서 |
| 시운전 결과보고서 | 공정·로봇 수·UPH·사이클타임·컨베어 타입·바디 피치·차종 수·컬러 수 + 납품물(운영 매뉴얼·알람리스트, 준공도면, PLC/HMI/Paint Studio/비전 백업, 체크리스트, 파라미터 리스트, 교육 확인서) | …\시운전결과보고서_광주1공장_중도외판_2022.10.pptx |
| 백업·보관 | EVO 고스트 백업·TIA Archive 교육, X:\STORAGE에 현장 SRS 백업(고객/라인/존/DLP별) | Q:\소프트웨어팀\EVO 백업 교육_230606, X:\STORAGE |

---

## 3) 프로젝트에 들어가는 SW·시스템 요소와 사양서 좌우 조건

| 요소 | 담당(주/부) | 사양서에서 좌우하는 문구·조건 | 근거 |
|---|---|---|---|
| 공정 PLC (메인 제어) | 소프트웨어팀 | **PLC 메이커·기종 지정**(Siemens/AB/Mitsubishi/LS), 고객 표준 PLC 프로그램 사용 여부, 리모트 I/O, **필드버스**(Profinet/EtherNet-IP/DeviceNet), 세이프티 PLC | 표준화 작업, 시운전 절차서, PCF 매뉴얼 |
| 트래킹·인터록 | 소프트웨어팀 | **UPH, 컨베어 타입(연동/정지)·속도, 바디 피치, 스키드 길이**, Work ID/차종 코드 수신 방식(MIP·IVDS), 부스·화재·믹싱룸·공조 인터록, 인체 감지, 디그레이드 운전 | 파라미터 용도 정리, Zone Tracking Method |
| SPC 도장 제어기 | 시스템SW팀 | **도장기 종류**(벨 ATOM/BSC300 터빈, EVO AccuBell, 2K, 듀얼), CCV 수, FGP 수(A~F), 셰이핑 에어 2계통(S/V), 고전압, **방폭 조건**, SPC 신·구형(대체품 치수) | SPC Alarm Code, 대체품 안내, Alarm Parameter List |
| EVO 연동 | 시스템SW팀+소프트웨어팀 | SAMES EVO 사용 여부, 컬러체인지·세정 시퀀스 사양, **컬러 수**, ET200SP Profinet | evo 폴더, HMI_Alarm_Ver2 |
| HMI(Smart View)·OPD | 시스템SW팀(디자인·화면)/소프트웨어팀(셋업) | **HMI 메이커 지정**(CIMON 등), 화면 해상도·와이드, **언어(다국어)**, 알람 조치 화면, OPD 버튼 구성, 고객 화면 표준 | HMI 디자인 자료, HMI 메뉴얼 |
| 알람 체계 | 시스템SW팀+소프트웨어팀 | 고객 알람 코드·문구 표준, 알람 조치 매뉴얼 제출 요구, 언어 | SPC Alarm Code, 시스템 알람 표준화 리스트 |
| 모니터링(SPS/Mate.D) | 스마트SW팀 | **"모니터링·예지보전·데이터 수집·생산 이력·VIN 추적"** 문구, 서버 수량(M.Server/DBMS), **FA망 연결·IP 대역**, PLC 주소 제공 범위, 라이선스, 보존 기간 | SPS V2.5, IP리스트, 지엠 PLC address |
| 데이터 로깅·MES/IoT | 스마트SW팀+소프트웨어팀 | MES 인터페이스(IP·프로토콜), 생산 데이터 항목, 고객 서버 연동 | 시운전 절차서(MES IP), SPS 생산 이력 |
| 원격 모니터링·지원 | 스마트SW팀 | 원격 접속 허용(SSL VPN 등), 구독 서비스(모니터링+파형+챗봇), 정기점검 | 자동경로생성 자료\SSLVPN, 파형측정 |
| 파형 측정 | 스마트SW팀 | 로봇 모델(GP/MPX), 정기점검·보증 조건 | 파형측정 수집 데이터 |
| 비전·자동경로(MAP) | 스마트SW팀(+외부 CPV/PICKIT/CAPMES) | **샌딩·와이핑·폴리싱 공정, 결점 검사(CPV), 3D 위치 보정, 사이클타임**, 결점 디스플레이, 툴 종류 | 자동경로생성 교육자료, 검증 리스트 |
| 비전(현대기아 Framework) | 스마트SW팀(**추정**) | 현대기아 지정 비전 프레임워크 사용 요구 | Z:\현대기아 비젼 Framework |
| 실러·하도 외부 장비 | 소프트웨어팀 | SCA(SYS6000/ASC5000), Graco PCF, CAPMES, Q-GAGE, BYK 측정 등 **지정 장비** | 매뉴얼 작성 일정표, HMGMA 하도 메뉴얼 |
| 시뮬레이션·Smart Cell | 스마트SW팀 | 오프라인 티칭·시뮬레이션 제출, 셀 캘리브레이션 요구 | SmartCell 공청회, MotoSim |
| Paint Studio / SRS 백업 | 소프트웨어팀(+스마트SW팀 SRS) | 잡·파라미터 백업 제출, 도장 레시피 관리 | X:\STORAGE, 시운전 결과보고서 |
| PC·IT 일반 | 소프트웨어팀 | 고객 백신·OS 업데이트·RAID·**고객 네트워크 정책**, 키락(라이선스) | 시운전 절차서 PC 항목 |

---

## 4) 팀별 "사양서에서 꼭 봐야 할 항목"

### 시스템SW팀
1. **도장기 종류·수량** — 벨(터빈 BSC300)/EVO/2K/듀얼, 로봇당 FGP·CCV 수, 셰이핑 에어 계통 → SPC 구성·알람 범위 결정 (SPC Alarm Code.xlsx, Alarm,System_Parameter_List.xlsx)
2. **방폭 등급·설치 공간** — SPC 대체품(OEM25-037)은 깊이 +5mm·높이 +15.4mm. 기존 라인 교체 사양이면 스토퍼 이동 필요 여부 (SPC 대체품 설치 안내.pptx)
3. **HMI 요구** — 메이커 지정, 해상도, 다국어, 알람 조치 화면 요구 (HMI(Smart View).pptx, HMI_Alarm_Ver2.pptx)
4. **EVO(SAMES) 사용 여부와 컬러체인지·세정 시퀀스 조건** (1. NEW SAMES HMI.pptx)
5. **필드버스·IPC 플랫폼** — Profinet/EtherNet-IP, CODESYS 기반 제어기 사용 가능 여부 (CodesysDYDevice — **추정**)

### 스마트SW팀
1. **모니터링·예지보전·데이터 수집 문구** 유무와 범위(라인/존/로봇, VIN 추적, 생산 이력 항목) → SPS 적용·라이선스 (SPSV2.5 소개자료)
2. **네트워크 조건** — FA망·서버렉망 분리, 서버(M.Server/DBMS) 설치 위치, IP 대역, 원격 접속 허용 (IP리스트 xlsx)
3. **고객 PLC 데이터 제공 범위** — Flow/Turbine/S/A/HV 명령·피드백, FGP 압력, Spray On 주소 (한국 지엠 Plc address 확인 요청.xlsx)
4. **샌딩·와이핑·폴리싱 자동화, 결점 검사(CPV)·3D 보정, 사이클타임** → MAP 적용 (자동경로생성 교육자료·검증 리스트)
5. **오프라인 시뮬레이션·셀 캘리브레이션 요구** → Smart Cell/MotoSim Launcher (smartcell 공청회 자료)
6. 정기점검·유지보수 구독, 로봇 모델 → 파형 측정 (오픈데이 파형측정시스템.pptx)

### 소프트웨어팀(시스템기술실)
1. **PLC 메이커·기종·고객 표준 프로그램 지정** (Siemens/AB/Mitsubishi/LS) (시스템 표준화 작업\2. PLC)
2. **컨베어·트래킹 조건** — UPH, 컨베어 타입·속도, 바디 피치, 스키드, 차종/Work ID 수신 방식(MIP·IVDS) (파라미터 용도 정리, Zone Tracking Method)
3. **인터록·안전** — 부스 화재·공조, 비상정지 위치, 인체 감지, 라이트 커튼·세이프티 PLC (시스템 알람 표준화 리스트, 시운전 절차서)
4. **지정 외부 장비·통신** — SCA·PCF·CAPMES·Q-GAGE·CPV·PICKIT·BYK, 필드버스(EDS/GSD) (매뉴얼 작성 일정표, PCF Setup 매뉴얼)
5. **MES·고객 네트워크·IT 정책** — MES 연동, 백신·OS·RAID (시운전 절차서)
6. **납품 문서 요구** — 운영 매뉴얼·알람리스트, 준공도면, 백업, 파라미터 리스트, 교육 확인서, 언어 (시운전 결과보고서)
7. 컬러 수·차종 수 → 컬러체인지·디그레이드 테이블 규모 (결과보고서 항목 — 영향 범위는 **추정**)

---

### 참고·한계
- Z:\SW카달로그 두 파일은 이미지 위주라 텍스트가 거의 없음(제목만). 제품 목록 확정에는 화면 확인 필요.
- Z:\DYONE_I 비어 있음, X:\FTP 비어 있음 또는 권한 없음.
- 시스템SW팀/스마트SW팀 경계: IREX 보고서는 "시스템SW팀" 작성인데 스마트SW팀 폴더에 있음 → 두 팀이 자료를 같이 쓰는 것으로 **추정**.
- SVN·소스 저장소, 대용량 zip(matlab, Src, Bin 등)은 열지 않음.
