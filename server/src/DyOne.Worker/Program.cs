using DyOne.Data;
using DyOne.Worker;

var builder = Host.CreateApplicationBuilder(args);

// 기존 dyone DB(읽기 전용). 개발 연결 정보는 user-secrets
builder.Services.AddDbContext<DyOneDbContext>(o => DyOneDb.Configure(o, builder.Configuration.GetConnectionString("DyOne")));

builder.Services.AddSingleton<IScheduledJob, SourceSyncJob>();
builder.Services.AddSingleton<IScheduledJob, PushDispatchJob>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
