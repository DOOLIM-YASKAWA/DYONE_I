using DyOne.Worker;
using DyOne.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

// 일일 동기화 설정 (매일 08:00 등)
builder.Services.Configure<GwProjectSyncOptions>(builder.Configuration.GetSection("GwProjectSync"));
builder.Services.Configure<OrgSyncOptions>(builder.Configuration.GetSection("OrgSync"));
builder.Services.Configure<PartnerSyncOptions>(builder.Configuration.GetSection("PartnerSync"));
builder.Services.Configure<GwModularSyncOptions>(builder.Configuration.GetSection("GwModularSync"));
builder.Services.Configure<GwWorksSyncOptions>(builder.Configuration.GetSection("GwWorksSync"));
builder.Services.Configure<GwNotiMirrorOptions>(builder.Configuration.GetSection("GwNotiMirror"));
builder.Services.Configure<SpecIndexOptions>(builder.Configuration.GetSection("SpecIndex"));

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
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
