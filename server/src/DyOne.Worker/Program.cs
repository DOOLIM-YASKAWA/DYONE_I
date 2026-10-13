using DyOne.Worker;
using DyOne.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

// 일일 동기화 설정 (매일 08:00 등)
builder.Services.Configure<GwProjectSyncOptions>(builder.Configuration.GetSection("GwProjectSync"));
builder.Services.Configure<OrgSyncOptions>(builder.Configuration.GetSection("OrgSync"));
builder.Services.Configure<GwModularSyncOptions>(builder.Configuration.GetSection("GwModularSync"));
builder.Services.Configure<GwWorksSyncOptions>(builder.Configuration.GetSection("GwWorksSync"));

// 주기 작업 등록. GW(tims) 읽기 + dyone 쓰기는 Job이 ConnectionStrings:Gw / DyOneWrite로 직접 연결한다.
builder.Services.AddSingleton<IWorkerJob, GwProjectSyncJob>();
builder.Services.AddSingleton<IWorkerJob, OrgSyncJob>();
builder.Services.AddSingleton<IWorkerJob, GwModularSyncJob>();
builder.Services.AddSingleton<IWorkerJob, GwWorksSyncJob>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
