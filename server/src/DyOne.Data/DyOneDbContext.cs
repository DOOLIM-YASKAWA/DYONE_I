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
