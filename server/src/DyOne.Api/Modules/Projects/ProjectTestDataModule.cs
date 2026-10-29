using System.Text.Json;
using Npgsql;

namespace DyOne.Api.Modules.Projects;

/// <param name="ConditionCount">조건 수</param>
/// <param name="MeasuredCount">측정값이 들어간 조건 수</param>
public record ProjectLabTestDto(long Id, string Name, string? Type, string? Operator, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    string? Description, int ConditionCount, int MeasuredCount, int ApprovedCount);

/// <summary>랩 테스트 조건 한 줄. Params = DYCTM 9파라미터 순서
/// [로봇 속도, 건 거리, 피치, 터빈 속도, 셰이핑 에어(와류), 셰이핑 에어(직진), 전압, 유량1, 유량2]. 결과는 data(작업용 사본) 기준으로
/// SmartLab 결과 화면(src/lib/calc.js)과 같은 식으로 계산한다 — Pattern: Max·Pattern(mm) / DFT: DFT(평균)·표준편차·TE.</summary>
public record LabConditionDto(int Number, double?[] Params, int Points, bool Approved,
    double? Max, double? PatternMm, double? Dft, double? Stdev, double? Te);

public record SrsMeasurementDto(long Id, DateTimeOffset? CapturedAt, string? Customer, string? Factory, string? Booth, string? Zone,
    int? RobotCount, int? ModelCount, int? ColorCount, decimal? MaxRobotVelocity, decimal? AvgFlowRate, string? HistoryRange);

/// <summary>
/// 프로젝트 시운전 화면용: 이 프로젝트에 연결된 SmartLab 랩 테스트(smartlab.*)와 SRS 측정(srs.*)을 읽기만 한다.
/// 두 표 모두 project_id로 프로젝트에 붙어 있다. 편집은 각 프로그램(SmartLab·SRS Analyzer)에서.
/// </summary>
public static class ProjectTestDataModule
{
    public static void MapProjectTestData(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{id:long}").WithTags("ProjectTestData").RequireAuthorization();

        // 랩 테스트 목록(최근 시작순)
        g.MapGet("/lab-tests", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, ct);
            await using var cmd = new NpgsqlCommand(@"
                select t.id, t.name, ty.name, u.name, t.started_at, t.ended_at, t.description,
                       count(c.*)::int, count(c.*) filter (where c.data is not null and cardinality(c.data) > 0)::int, count(c.*) filter (where c.approved)::int
                from smartlab.lab_test t
                left join code.code ty on ty.id = t.test_type_id
                left join iam.""user"" u on u.id = t.operator_id
                left join smartlab.lab_test_condition c on c.test_id = t.id
                where t.project_id = @pid
                group by t.id, ty.name, u.name
                order by t.started_at desc nulls last, t.id desc", conn);
            cmd.Parameters.AddWithValue("pid", id);
            var list = new List<ProjectLabTestDto>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
            while (await r.ReadAsync(ct))
                list.Add(new ProjectLabTestDto(r.GetInt64(0), r.GetString(1), S(2), S(3), T(4), T(5), S(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9)));
            return Results.Ok(list);
        });

        // 랩 테스트 상세: 조건별 파라미터 + 측정값 통계
        g.MapGet("/lab-tests/{testId:long}", async (long id, long testId, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, ct);
            await using var cmd = new NpgsqlCommand(@"
                select c.condition_number, c.condition, c.data, c.approved
                from smartlab.lab_test_condition c
                join smartlab.lab_test t on t.id = c.test_id and t.project_id = @pid
                where c.test_id = @tid
                order by c.condition_number", conn);
            cmd.Parameters.AddWithValue("pid", id);
            cmd.Parameters.AddWithValue("tid", testId);
            var list = new List<LabConditionDto>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var ps = r.IsDBNull(1) ? [] : r.GetFieldValue<double?[]>(1);
                var data = r.IsDBNull(2) ? [] : r.GetFieldValue<double?[]>(2);
                var (max, pattern) = LabCalc.Pattern(data);
                var (dft, stdev, te) = LabCalc.Dft(ps, data);
                list.Add(new LabConditionDto(r.GetInt32(0), ps, data.Length, r.GetBoolean(3), max, pattern, dft, stdev, te));
            }
            return Results.Ok(list);
        });

        // 랩 테스트 엑셀 내보내기: [결과] SmartLab 결과 화면과 같은 칸(파라미터 + Pattern: Max·Pattern(mm) / DFT: DFT·Stdev·TE) + [측정값] 조건별 원본 측정점
        g.MapGet("/lab-tests/{testId:long}/export", async (long id, long testId, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, ct);
            string? project = null, name = null, type = null;
            await using (var head = new NpgsqlCommand(@"
                select p.code || ' ' || p.name, t.name, ty.name
                from smartlab.lab_test t
                join project.project p on p.id = t.project_id
                left join code.code ty on ty.id = t.test_type_id
                where t.id = @tid and t.project_id = @pid", conn))
            {
                head.Parameters.AddWithValue("pid", id);
                head.Parameters.AddWithValue("tid", testId);
                await using var hr = await head.ExecuteReaderAsync(ct);
                if (!await hr.ReadAsync(ct)) return Results.NotFound();
                string? S(int i) => hr.IsDBNull(i) ? null : hr.GetString(i);
                (project, name, type) = (S(0), S(1), S(2));
            }
            var rows = new List<(int No, double?[] Ps, double?[] Data, bool Approved)>();
            await using (var cmd = new NpgsqlCommand(
                "select condition_number, condition, data, approved from smartlab.lab_test_condition where test_id = @tid order by condition_number", conn))
            {
                cmd.Parameters.AddWithValue("tid", testId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    rows.Add((r.GetInt32(0), r.IsDBNull(1) ? [] : r.GetFieldValue<double?[]>(1), r.IsDBNull(2) ? [] : r.GetFieldValue<double?[]>(2), r.GetBoolean(3)));
            }
            var bytes = LabExcel.Build(project, name ?? "", type, rows);
            var file = string.Concat((name ?? "랩테스트").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{file}_결과.xlsx");
        });

        // SRS 측정 목록(최근 측정순): 공정 위치(고객사 > 공장 > 부스 > Zone) + 요약 수치
        g.MapGet("/srs-measurements", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, ct);
            await using var cmd = new NpgsqlCommand(@"
                select m.id, m.captured_at, cu.name, f.name, b.name, z.name, m.robot_count, m.model_count, m.color_count,
                       m.max_robot_velocity, m.avg_flow_rate, s.history_date_range
                from srs.measurement m
                left join plant.zone z on z.id = m.zone_id
                left join plant.booth b on b.id = z.booth_id
                left join plant.factory f on f.id = b.factory_id
                left join plant.customer cu on cu.id = f.customer_id
                left join srs.measurement_summary s on s.measurement_id = m.id
                where m.project_id = @pid
                order by m.captured_at desc nulls last, m.id desc", conn);
            cmd.Parameters.AddWithValue("pid", id);
            var list = new List<SrsMeasurementDto>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            decimal? N(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
            while (await r.ReadAsync(ct))
                list.Add(new SrsMeasurementDto(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1),
                    S(2), S(3), S(4), S(5), I(6), I(7), I(8), N(9), N(10), S(11)));
            return Results.Ok(list);
        });

        // SRS 측정 상세: 로봇별 요약(summary.robots + statistics.history.RobotData를 로봇 순서로 합침), 모델·색상 이름
        g.MapGet("/srs-measurements/{mid:long}", async (long id, long mid, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, ct);
            await using var cmd = new NpgsqlCommand(@"
                select s.robots::text, s.models::text, s.colors::text, s.brush_count, st.history::text
                from srs.measurement m
                left join srs.measurement_summary s on s.measurement_id = m.id
                left join srs.measurement_statistics st on st.measurement_id = m.id
                where m.id = @mid and m.project_id = @pid", conn);
            cmd.Parameters.AddWithValue("pid", id);
            cmd.Parameters.AddWithValue("mid", mid);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            JsonElement J(int i, string fallback) => JsonDocument.Parse(r.IsDBNull(i) ? fallback : r.GetString(i)).RootElement.Clone();
            var robots = J(0, "[]");
            var history = J(4, "{}");
            var robotData = history.ValueKind == JsonValueKind.Object && history.TryGetProperty("RobotData", out var rd) && rd.ValueKind == JsonValueKind.Array
                ? rd.EnumerateArray().ToList() : [];
            string? P(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ToString() : null;
            var merged = robots.ValueKind == JsonValueKind.Array
                ? robots.EnumerateArray().Select((e, i) =>
                {
                    var h = robotData.FirstOrDefault(x => P(x, "RobotIdx") == i.ToString());
                    return new
                    {
                        name = P(e, "RobotName"), type = P(e, "RobotType"), tool = P(e, "ToolName"),
                        avgVelocity = P(e, "AvgVelocity"), gunDistance = P(e, "GunDistance"), avgGunOnTime = P(e, "AvgGunOnTime"),
                        hvAvg = P(h, "HvAvg"), paintVolAvg = P(h, "PaintVolAvg"),
                    };
                }).ToList<object>()
                : [];
            IEnumerable<string?> Names(JsonElement a) => a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => P(x, "name")) : [];
            return Results.Ok(new
            {
                robots = merged,
                models = Names(J(1, "[]")).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                colors = Names(J(2, "[]")).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                brushCount = r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
            });
        });
    }

    /// <summary>SmartLab 결과 계산(E:\dy-one-smartlab src/lib/calc.js patternResult·dftResult)과 같은 식. 바꾸면 SmartLab과 같이 바꿔야 한다.</summary>
    private static class LabCalc
    {
        /// <summary>SmartLab 서버의 기본 SVR(고형분/페인트) — 실제 값 소스가 정해지기 전 임시(server/index.js DEFAULT_SVR)</summary>
        private const double DefaultSvr = 45;
        /// <summary>Pattern 측정점 간격(mm)</summary>
        private const double PointPitchMm = 20;

        /// <summary>빈 값·NaN이 없고 전부 0이 아닐 때만 결과를 낸다</summary>
        private static bool Valid(double?[] d) => d.Length > 0 && d.All(v => v is { } x && !double.IsNaN(x)) && !d.All(v => v == 0);

        /// <summary>Max = 최댓값. Pattern(mm) = (상위 3개 서로 다른 값 평균 ÷ 4) 이상인 첫 점~마지막 점 개수 × 20mm</summary>
        public static (double? Max, double? PatternMm) Pattern(double?[] d)
        {
            if (!Valid(d)) return (null, null);
            var nums = d.Select(v => v!.Value).ToArray();
            var top = nums.Distinct().OrderByDescending(x => x).Take(3).ToArray();
            var criterion = top.Average() / 4;
            int first = Array.FindIndex(nums, x => x >= criterion), last = Array.FindLastIndex(nums, x => x >= criterion);
            return (nums.Max(), first < 0 ? 0 : (last - first + 1) * PointPitchMm);
        }

        /// <summary>DFT = 평균, 표준편차(표본), TE = 100 × (속도/1000 × 피치/1000 × DFT × 60) ÷ (SVR × (유량1+유량2))</summary>
        public static (double? Dft, double? Stdev, double? Te) Dft(double?[] ps, double?[] d)
        {
            var nums = d.Where(v => v is { } x && !double.IsNaN(x)).Select(v => v!.Value).ToArray();
            if (nums.Length == 0) return (null, null, null);
            var mean = nums.Average();
            var sd = nums.Length < 2 ? 0 : Math.Sqrt(nums.Sum(x => (x - mean) * (x - mean)) / (nums.Length - 1));
            double P(int i) => i < ps.Length && ps[i] is { } v ? v : 0;
            var denom = DefaultSvr * (P(7) + P(8));
            var te = denom == 0 ? 0 : 100 * (P(0) / 1000 * (P(2) / 1000) * mean * 60) / denom;
            return (mean, sd, te);
        }
    }

    /// <summary>랩 테스트 결과 엑셀. 머리 줄 색·★ 표시는 SmartLab 내보내기(src/lib/xlsx.js)와 비슷하게.</summary>
    private static class LabExcel
    {
        // SmartLab 결과 화면과 같은 이름·단위(src/lib/calc.js PARAM_COLS)
        private static readonly string[] ParamCols = ["Speed(mm/s)", "Distance(mm)", "Pitch(mm)", "RPM(Krpm)", "V SA(Lpm)", "S SA(Lpm)", "HV(㎂)", "Flow1(cc/m)", "Flow2(cc/m)"];

        public static byte[] Build(string? project, string name, string? type,
            List<(int No, double?[] Ps, double?[] Data, bool Approved)> rows)
        {
            var isPattern = type != "DFT";
            var isDft = type != "Pattern";
            using var wb = new ClosedXML.Excel.XLWorkbook();

            // [결과]
            var ws = wb.AddWorksheet("결과");
            // 머리말은 프로젝트·테스트·종류만(담당·기간·참고 문구는 넣지 않음)
            var info = new (string, string)[] { ("프로젝트", project ?? "-"), ("테스트", name), ("종류", type ?? "-") };
            var row = 1;
            foreach (var (k, v) in info)
            {
                ws.Cell(row, 1).Value = k;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = v;
                row++;
            }
            row++;

            var header = new List<string> { "No", "채택" };
            header.AddRange(ParamCols);
            if (isPattern) header.AddRange(["Max", "Pattern(mm)"]);
            if (isDft) header.AddRange(["DFT(µm)", "Stdev", "TE(%)"]);
            var headRow = row;
            for (var i = 0; i < header.Count; i++) ws.Cell(row, i + 1).Value = header[i];
            StyleHeader(ws.Range(row, 1, row, header.Count));
            foreach (var c in rows)
            {
                row++;
                var col = 1;
                ws.Cell(row, col++).Value = c.No;
                ws.Cell(row, col++).Value = c.Approved ? "★" : "";
                for (var i = 0; i < ParamCols.Length; i++) Num(ws.Cell(row, col++), i < c.Ps.Length ? c.Ps[i] : null);
                if (isPattern)
                {
                    var (max, pattern) = LabCalc.Pattern(c.Data);
                    Num(ws.Cell(row, col++), max);
                    Num(ws.Cell(row, col++), pattern);
                }
                if (isDft)
                {
                    var (dft, sd, te) = LabCalc.Dft(c.Ps, c.Data);
                    Num(ws.Cell(row, col++), dft, "0.00");
                    Num(ws.Cell(row, col++), sd, "0.00");
                    Num(ws.Cell(row, col++), te, "0.00");
                }
                if (c.Approved) ws.Range(row, 1, row, header.Count).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#FFF8E1");
            }
            ws.SheetView.FreezeRows(headRow);
            ws.Columns().AdjustToContents();

            // [측정값] 조건별 원본 측정점(µm) — 행 = 측정점, 열 = 조건
            var raw = wb.AddWorksheet("측정값");
            raw.Cell(1, 1).Value = "Point";
            for (var j = 0; j < rows.Count; j++) raw.Cell(1, j + 2).Value = $"조건 {rows[j].No}";
            StyleHeader(raw.Range(1, 1, 1, rows.Count + 1));
            var points = rows.Count == 0 ? 0 : rows.Max(x => x.Data.Length);
            for (var p = 0; p < points; p++)
            {
                raw.Cell(p + 2, 1).Value = p + 1;
                for (var j = 0; j < rows.Count; j++) Num(raw.Cell(p + 2, j + 2), p < rows[j].Data.Length ? rows[j].Data[p] : null, "0.00");
            }
            raw.SheetView.FreezeRows(1);
            raw.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private static void Num(ClosedXML.Excel.IXLCell cell, double? v, string? format = null)
        {
            if (v is not { } x || double.IsNaN(x)) return;
            cell.Value = x;
            if (format is not null) cell.Style.NumberFormat.Format = format;
        }

        private static void StyleHeader(ClosedXML.Excel.IXLRange r)
        {
            r.Style.Font.Bold = true;
            r.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
            r.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#2DA873");
            r.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(IConfiguration config, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
        await conn.OpenAsync(ct);
        return conn;
    }
}
