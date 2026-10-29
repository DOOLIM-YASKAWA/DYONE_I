namespace DyOne.Spec.Library;

/// <summary>점수에 기여한 이유 한 가지(화면에 "같은 공정: 중도·내판"처럼 보여 준다)</summary>
public sealed record MatchReason(string Label, string Detail);

/// <summary>
/// 고객 사양서 특징 ↔ 과거 사양정리 특징 비교 점수(0~100). 항목별 가중치 × 겹치는 정도.
/// 공정이 가장 중요하고(같은 공정이어야 사양정리 항목이 비슷하다), 로봇·도장기·고객·공장 순.
/// 고객 사양서는 로봇 모델을 안 쓰는 경우가 많아(HMC 구매요구사양서는 '도장 로봇'만 씀) 한쪽에 값이 없으면 그 항목은 빼고 계산한다.
/// </summary>
public static class SpecMatch
{
    // 도료 방식·PLC는 대부분 프로젝트가 같아서(유성·미쓰비시) 가중치를 낮게 둔다
    private const double WProcess = 6, WRobot = 2.5, WAtomizer = 1.5, WCustomer = 2, WPlant = 1.5, WPlc = 0.5, WPaint = 0.5, WUph = 1;

    public static (double score, List<MatchReason> reasons) Score(SpecFeatures a, SpecFeatures b)
    {
        double got = 0, max = 0;
        var reasons = new List<MatchReason>();

        void Set(string label, SortedSet<string> x, SortedSet<string> y, double w)
        {
            if (x.Count == 0 || y.Count == 0) return;
            max += w;
            var common = x.Intersect(y).ToList();
            if (common.Count == 0) return;
            // 겹침 비율(Dice): 2×공통 / (양쪽 크기 합). '중도'만 같은 프로젝트가 '중도·도어·내판·교체'가 모두 같은 프로젝트와 같은 점수를 받지 않게 한다
            got += w * 2.0 * common.Count / (x.Count + y.Count);
            reasons.Add(new MatchReason(label, string.Join("·", common)));
        }

        Set("같은 공정", a.Processes, b.Processes, WProcess);
        Set("같은 로봇", a.Robots, b.Robots, WRobot);
        Set("같은 도장기", a.Atomizers, b.Atomizers, WAtomizer);
        Set("같은 고객", a.Customers, b.Customers, WCustomer);
        Set("같은 공장", a.Plants, b.Plants, WPlant);
        Set("같은 PLC", a.Plcs, b.Plcs, WPlc);
        Set("같은 도료 방식", a.Paints, b.Paints, WPaint);
        if (a.Uph is { } u1 && b.Uph is { } u2)
        {
            max += WUph;
            var near = 1 - Math.Min(1, Math.Abs(u1 - u2) / (double)Math.Max(u1, u2));
            got += WUph * near;
            if (near >= 0.8) reasons.Add(new MatchReason("비슷한 UPH", u1 == u2 ? $"{u1}" : $"{u1} / {u2}"));
        }
        if (max == 0) return (0, reasons);
        // 비교할 수 있는 항목이 적으면(정보가 거의 없으면) 점수를 낮춰 과신을 막는다
        var coverage = Math.Min(1, max / (WProcess + WCustomer + WPlant));
        return (Math.Round(100 * got / max * (0.6 + 0.4 * coverage), 1), reasons);
    }
}
