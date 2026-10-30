namespace DyOne.Api.Modules.Meals;

/// <summary>
/// 수담푸드(구내식당) 주간 식단. Worker(SudamMenuJob)가 쓴 캐시 파일(FileStorage:Root\_meals\sudam.json)을 그대로 돌려준다(읽기 전용, DB 없음).
/// 아직 수집 전이면 빈 모양을 돌려주고 화면이 "아직 없음"을 보여 준다.
/// </summary>
public static class MealsModule
{
    public static void MapMeals(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/meals/sudam", async (IConfiguration config, CancellationToken ct) =>
        {
            var path = config["Meals:SudamCache"] is { Length: > 0 } p ? p
                : Path.Combine(config["FileStorage:Root"] ?? AppContext.BaseDirectory, "_meals", "sudam.json");
            if (!File.Exists(path))
                return Results.Ok(new { sourceUrl = (string?)null, checkedAt = (DateTimeOffset?)null, images = Array.Empty<string>(), extracted = false, days = Array.Empty<object>() });
            return Results.Text(await File.ReadAllTextAsync(path, ct), "application/json; charset=utf-8");
        });
    }
}
