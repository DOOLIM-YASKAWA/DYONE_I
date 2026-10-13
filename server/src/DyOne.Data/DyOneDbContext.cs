using DyOne.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Data;

/// <summary>
/// 기존 dyone DB 읽기 전용 컨텍스트.
/// - 테이블 구조는 다른 앱(SmartLab · SRS 등)과 함께 쓰는 정본이므로 마이그레이션을 만들지 않는다.
/// - 연결 문자열에 default_transaction_read_only=on을 넣어 DB 쪽에서도 쓰기를 막고, 여기서도 SaveChanges를 막는다.
/// </summary>
public class DyOneDbContext(DbContextOptions<DyOneDbContext> options) : DbContext(options)
{
    public DbSet<CodeItem> Codes => Set<CodeItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectAssignment> Assignments => Set<ProjectAssignment>();
    public DbSet<ProjectApp> ProjectApps => Set<ProjectApp>();
    public DbSet<ProjectStage> ProjectStages => Set<ProjectStage>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Factory> Factories => Set<Factory>();
    public DbSet<Booth> Booths => Set<Booth>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<RoleMapping> RoleMappings => Set<RoleMapping>();
    public DbSet<LabTest> LabTests => Set<LabTest>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<ModularOption> ModularOptions => Set<ModularOption>();
    public DbSet<ModularStatus> ModularStatuses => Set<ModularStatus>();
    public DbSet<ModularTransition> ModularTransitions => Set<ModularTransition>();
    public DbSet<ModularBuild> ModularBuilds => Set<ModularBuild>();
    public DbSet<ModularBuildOption> ModularBuildOptions => Set<ModularBuildOption>();
    public DbSet<ModularBuildMember> ModularBuildMembers => Set<ModularBuildMember>();
    public DbSet<ModularActivity> ModularActivities => Set<ModularActivity>();
    public DbSet<ModularStatusHistory> ModularStatusHistory => Set<ModularStatusHistory>();
    public DbSet<ModularHistory> ModularHistory => Set<ModularHistory>();
    public DbSet<ModularComment> ModularComments => Set<ModularComment>();
    public DbSet<ModularFile> ModularFiles => Set<ModularFile>();
    public DbSet<ModularBuildGw> ModularBuildGw => Set<ModularBuildGw>();
    public DbSet<ModularFieldChange> ModularFieldChanges => Set<ModularFieldChange>();
    public DbSet<WorksApp> WorksApps => Set<WorksApp>();
    public DbSet<WorksField> WorksFields => Set<WorksField>();
    public DbSet<WorksStatus> WorksStatuses => Set<WorksStatus>();
    public DbSet<WorksTransition> WorksTransitions => Set<WorksTransition>();
    public DbSet<WorksDoc> WorksDocs => Set<WorksDoc>();
    public DbSet<WorksDocGw> WorksDocGw => Set<WorksDocGw>();
    public DbSet<WorksFieldChange> WorksFieldChanges => Set<WorksFieldChange>();
    public DbSet<WorksStatusHistory> WorksStatusHistory => Set<WorksStatusHistory>();
    public DbSet<WorksActivity> WorksActivities => Set<WorksActivity>();
    public DbSet<WorksComment> WorksComments => Set<WorksComment>();
    public DbSet<WorksHistory> WorksHistory => Set<WorksHistory>();
    public DbSet<WorksFile> WorksFiles => Set<WorksFile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CodeItem>().ToTable("code", "code");
        b.Entity<Project>().ToTable("project", "project");
        b.Entity<ProjectAssignment>().ToTable("project_assignment", "project");
        b.Entity<ProjectApp>(e =>
        {
            e.ToTable("project_app", "project");
            e.HasKey(x => new { x.ProjectId, x.AppKey });
        });
        b.Entity<ProjectStage>(e =>
        {
            e.ToTable("project_stage", "project");
            e.HasKey(x => x.ProjectId);
        });
        b.Entity<ProjectFile>().ToTable("project_file", "project");
        b.Entity<Customer>().ToTable("customer", "plant");
        b.Entity<Factory>().ToTable("factory", "plant");
        b.Entity<Booth>().ToTable("booth", "plant");
        b.Entity<Zone>().ToTable("zone", "plant");
        b.Entity<Employee>().ToTable("employee", "org");
        b.Entity<Department>().ToTable("department", "org");
        b.Entity<Position>().ToTable("position", "org");
        b.Entity<RoleMapping>().ToTable("role_mapping", "org");
        b.Entity<LabTest>().ToTable("lab_test", "smartlab");
        b.Entity<SyncState>(e =>
        {
            e.ToTable("state", "sync");
            e.HasKey(x => new { x.Source, x.Entity });
        });

        // modular.* — 모듈라빌드 프로젝트 정보(그룹웨어 applet 376 이관). DDL은 server/db/modular_schema.sql
        b.Entity<ModularOption>(e =>
        {
            e.ToTable("option", "modular");
            e.HasKey(x => new { x.ListKey, x.Code });
        });
        b.Entity<ModularStatus>().ToTable("status", "modular");
        b.Entity<ModularTransition>().ToTable("transition", "modular");
        b.Entity<ModularBuild>().ToTable("build", "modular");
        b.Entity<ModularBuildOption>(e =>
        {
            e.ToTable("build_option", "modular");
            e.HasKey(x => new { x.BuildId, x.ListKey, x.Code });
        });
        b.Entity<ModularBuildMember>(e =>
        {
            e.ToTable("build_member", "modular");
            e.HasKey(x => new { x.BuildId, x.Role });
        });
        b.Entity<ModularActivity>().ToTable("activity", "modular");
        b.Entity<ModularStatusHistory>().ToTable("status_history", "modular");
        b.Entity<ModularHistory>().ToTable("history", "modular");
        b.Entity<ModularComment>().ToTable("comment", "modular");
        b.Entity<ModularFile>().ToTable("file", "modular");
        b.Entity<ModularBuildGw>(e =>
        {
            e.ToTable("build_gw", "modular");
            e.HasKey(x => x.BuildId);
            e.Property(x => x.Values).HasColumnType("jsonb");
        });
        b.Entity<ModularFieldChange>().ToTable("field_change", "modular");

        // works.* — 그룹웨어 앱 공통 저장소(281 ES 이슈 등). DDL은 server/db/works_schema.sql
        b.Entity<WorksApp>(e =>
        {
            e.ToTable("app", "works");
            e.HasKey(x => x.AppletId);
            e.Property(x => x.Layout).HasColumnType("jsonb");
            e.Property(x => x.ViewColumns).HasColumnType("jsonb");
        });
        b.Entity<WorksField>(e =>
        {
            e.ToTable("field", "works");
            e.HasKey(x => new { x.AppletId, x.Cid });
            e.Property(x => x.Options).HasColumnType("jsonb");
            e.Property(x => x.Props).HasColumnType("jsonb");
        });
        b.Entity<WorksStatus>().ToTable("status", "works");
        b.Entity<WorksTransition>().ToTable("transition", "works");
        b.Entity<WorksDoc>(e =>
        {
            e.ToTable("doc", "works");
            e.Property(x => x.Values).HasColumnType("jsonb");
            e.Property(x => x.Display).HasColumnType("jsonb");
        });
        b.Entity<WorksDocGw>(e =>
        {
            e.ToTable("doc_gw", "works");
            e.HasKey(x => x.DocId);
            e.Property(x => x.Values).HasColumnType("jsonb");
            e.Property(x => x.Display).HasColumnType("jsonb");
        });
        b.Entity<WorksFieldChange>().ToTable("field_change", "works");
        b.Entity<WorksStatusHistory>().ToTable("status_history", "works");
        b.Entity<WorksActivity>().ToTable("activity", "works");
        b.Entity<WorksComment>().ToTable("comment", "works");
        b.Entity<WorksHistory>().ToTable("history", "works");
        b.Entity<WorksFile>().ToTable("file", "works");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("DY ONE 내부용은 기존 dyone DB를 읽기 전용으로 사용합니다.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("DY ONE 내부용은 기존 dyone DB를 읽기 전용으로 사용합니다.");
}

public static class DyOneDb
{
    /// <summary>API·Worker 공통 등록. 추적 없이(읽기 전용) snake_case 매핑으로 연결한다.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder o, string? connectionString) => o
        .UseNpgsql(connectionString ?? throw new InvalidOperationException("ConnectionStrings:DyOne이 없습니다(개발: user-secrets)."))
        .UseSnakeCaseNamingConvention()
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
}
