using DyOne.Worker;
using DyOne.Worker.Jobs;
using DyOne.Worker.Jobs.ProjectMain;

var builder = Host.CreateApplicationBuilder(args);

// 일일 동기화 설정 (매일 08:00 등)
builder.Services.Configure<GwProjectSyncOptions>(builder.Configuration.GetSection("GwProjectSync"));
builder.Services.Configure<OrgSyncOptions>(builder.Configuration.GetSection("OrgSync"));
builder.Services.Configure<PartnerSyncOptions>(builder.Configuration.GetSection("PartnerSync"));
builder.Services.Configure<GwModularSyncOptions>(builder.Configuration.GetSection("GwModularSync"));
builder.Services.Configure<GwWorksSyncOptions>(builder.Configuration.GetSection("GwWorksSync"));
builder.Services.Configure<GwNotiMirrorOptions>(builder.Configuration.GetSection("GwNotiMirror"));
builder.Services.Configure<SpecIndexOptions>(builder.Configuration.GetSection("SpecIndex"));
builder.Services.Configure<SudamMenuOptions>(builder.Configuration.GetSection("SudamMenu"));
builder.Services.Configure<ProjectMainOptions>(builder.Configuration.GetSection("ProjectMain"));

// 주기 작업 등록. GW(tims) 읽기 + dyone 쓰기는 Job이 ConnectionStrings:Gw / DyOneWrite로 직접 연결한다.
builder.Services.AddSingleton<IWorkerJob, GwProjectSyncJob>();
builder.Services.AddSingleton<IWorkerJob, OrgSyncJob>();
// 협력사 명단: 그룹웨어 회사 공용 주소록 → partner.company·person(매일 08:10). 비밀번호는 건드리지 않는다.
builder.Services.AddSingleton<IWorkerJob, PartnerSyncJob>();
builder.Services.AddSingleton<IWorkerJob, GwModularSyncJob>();
builder.Services.AddSingleton<IWorkerJob, GwWorksSyncJob>();
builder.Services.AddSingleton<IWorkerJob, GwNotiMirrorJob>();
// 사양서 분석용 NAS 색인(라이브러리 → 설비 이력, 매일 03:00). DB는 쓰지 않고 API와 같은 캐시 파일을 새로 쓴다.
builder.Services.AddSingleton<IWorkerJob, SpecIndexJob>();
// 수담푸드(구내식당) 식단: 네이버 QR 메뉴판 이미지 → 사내 AI 글자 읽기 → 캐시 파일(_meals\sudam.json). DB는 쓰지 않는다.
builder.Services.AddSingleton<IWorkerJob, SudamMenuJob>();
// 프로젝트 메인(매일 06:30): ERP 미러(읽기 전용) · NAS·발주·모듈라 흔적 · 회사 주요 후보(규칙 점수 + 사내 AI 이유) · 오늘의 브리핑(사내 AI)
builder.Services.AddSingleton<IWorkerJob, ProjectMainJob>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// 한 번만 실행: dotnet DyOne.Worker.dll --run-once project-main  (스케줄을 기다리지 않고 작업 하나만 돌리고 끝낸다. 배포 직후 확인·수동 재계산용)
if (args.Length >= 2 && args[0] == "--run-once")
{
    var job = host.Services.GetServices<IWorkerJob>().FirstOrDefault(j => j.Name == args[1])
        ?? throw new InvalidOperationException($"작업 이름 없음: {args[1]}");
    await job.RunAsync(CancellationToken.None);
    return;
}

host.Run();
