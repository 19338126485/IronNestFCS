using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 测绘几何约束的公共接口：情报里的每条几何信息（方位线/距离圆）都实现它。
/// 所有计算在平面网格坐标上进行，1 单位 = 1 km（与地图格区、弹道距离同尺度，
/// 即 MapTable 里 localPosition * 3.8164 + offset 之后的坐标系）。
/// </summary>
public interface IGeoConstraint {
    /// <summary>给定点相对本约束的残差（km），0 = 完全满足。用于多约束投票消歧。</summary>
    float Residual(Vector2 p);
    string Describe();
}

/// <summary>
/// 方位线：从锚点出发、沿某方位角的射线。
/// 方位角约定：0° = 地图 +Y（北），顺时针为正（与 FCS 弹道方向角一致）。
/// 注意：游戏电报机 token 处理器 FormatBearing 内部用 atan2(dy,dx)（数学角，+X 起逆时针），
/// 文本打印出来的是哪种约定需要实测校准——若不符，在 IntelSystem.BuildConstraints 里统一换算。
/// </summary>
public sealed class BearingLine : IGeoConstraint {
    public Vector2 Origin;
    public float BearingDeg;
    public string AnchorName = "";
    /// <summary>模糊方位（罗盘方位词，±11°）：由它参与产生的候选一律标低置信度。</summary>
    public bool Fuzzy;

    public Vector2 Dir => new(Mathf.Sin(BearingDeg * Mathf.Deg2Rad), Mathf.Cos(BearingDeg * Mathf.Deg2Rad));

    public float Residual(Vector2 p) {
        var d = Dir;
        var rel = p - Origin;
        var t = rel.x * d.x + rel.y * d.y;               // 沿射线方向的投影
        var perp = Mathf.Abs(rel.x * d.y - rel.y * d.x); // 到直线（无限延伸）的垂直距离
        // 射线是单向的：点落在锚点后方（t<0）时，额外加上"离起点越来越远"的惩罚
        return t >= 0f ? perp : perp - t;
    }

    public string Describe() =>
        $"Bearing {BearingDeg:F1}deg from {AnchorName} ({Origin.x:F2},{Origin.y:F2})";
}

/// <summary>距离圆：以锚点为圆心、给定半径（km）的圆。</summary>
public sealed class DistanceCircle : IGeoConstraint {
    public Vector2 Center;
    public float RadiusKm;
    public string AnchorName = "";

    public float Residual(Vector2 p) => Mathf.Abs(Vector2.Distance(p, Center) - RadiusKm);

    public string Describe() =>
        $"Distance {RadiusKm:F2}km from {AnchorName} ({Center.x:F2},{Center.y:F2})";
}

/// <summary>一个候选解：交点 + 全部约束的总残差 + 由来（供日志与 UI）。</summary>
public sealed class SurveyCandidate {
    public Vector2 Point;
    public float Score;
    public string Basis = "";
    /// <summary>所属主题/条目的名字（如"敌方炮兵指挥中心#1"），供日志与 UI 显示。</summary>
    public string Name = "";
    /// <summary>解析情报时顺带识别出的棋子类型名（如 MapToken_Artillery）；null = 未识别。</summary>
    public string? TokenName;
    /// <summary>
    /// 低置信度标记：产生该交点的两条约束近乎相切（方位线几乎擦过距离圆等），
    /// 此时报文的整数度舍入会被几何放大成显著的定位漂移——真解可能在交点附近沿约束滑动很远。
    /// </summary>
    public bool LowConfidence;
}

/// <summary>
/// 几何解算器：把"方位线 / 距离圆"约束两两求交，得到候选目标点。
/// 双解歧义（线∩圆、圆∩圆都可能有两个交点）不靠几何本身裁决，
/// 而是把所有约束的总残差作为分数排序——真解在所有约束上残差都小，
/// 假解通常只满足产生它的那两条。分数接近时才交给用户/语义规则裁决。
/// </summary>
public static class GeoSolver {
    /// <summary>前两名候选的分数差小于该值（km）时视为歧义，需要用户裁决。</summary>
    public const float AmbiguityThresholdKm = 0.3f;

    /// <summary>候选点去重半径（km）：不同约束对产生的相同交点只保留一个。</summary>
    private const float DedupeKm = 0.05f;

    /// <summary>方位线 ∩ 距离圆：0~2 个交点，只保留落在射线前半部分（t ≥ 0）的。
    /// 擦边未交（报文舍入所致，实测曾差 2m 而丢目标）时用最接近点近似，标低置信度。</summary>
    public static List<Vector2> Intersect(BearingLine l, DistanceCircle c) {
        var res = new List<Vector2>();
        var d = l.Dir;
        var f = l.Origin - c.Center;
        // 解 |f + t·d|² = r²  →  t² + 2(f·d)t + (|f|² - r²) = 0
        var b = f.x * d.x + f.y * d.y;
        var cc = f.x * f.x + f.y * f.y - c.RadiusKm * c.RadiusKm;
        var disc = b * b - cc;
        if (disc < 0f) {
            // 相离：圆心到射线的垂直距离超出半径一点点 → 用最接近点（圆心在射线上的垂足）近似
            var rel = c.Center - l.Origin;
            var perp = Mathf.Abs(rel.x * d.y - rel.y * d.x);
            var tClosest = rel.x * d.x + rel.y * d.y;
            if (perp - c.RadiusKm <= NearMissToleranceKm && tClosest >= 0f) {
                res.Add(l.Origin + d * tClosest);
            }
            return res;
        }
        var sq = Mathf.Sqrt(disc);
        foreach (var t in new[] { -b + sq, -b - sq }) {
            if (t >= 0f) res.Add(l.Origin + d * t);
        }
        return res;
    }

    /// <summary>擦边未交容差（km）：输入舍入的误差预算（锚点 0.05×2 + 整数度 ~0.03@4km + 距离 0.005）。</summary>
    public const float NearMissToleranceKm = 0.2f;

    /// <summary>距离圆 ∩ 距离圆：0~2 个交点。擦边相离/内含时取间隙中点近似（标低置信度）。</summary>
    public static List<Vector2> Intersect(DistanceCircle a, DistanceCircle b) {
        var res = new List<Vector2>();
        var delta = b.Center - a.Center;
        var d = delta.magnitude;
        if (d < 1e-6f) return res;                          // 同心圆：无解或无数解，都不可用
        if (d > a.RadiusKm + b.RadiusKm) {
            // 相离一点点：取两圆间间隙的中点
            var gap = d - a.RadiusKm - b.RadiusKm;
            if (gap <= NearMissToleranceKm) res.Add(a.Center + delta / d * (a.RadiusKm + gap * 0.5f));
            return res;
        }
        if (d < Mathf.Abs(a.RadiusKm - b.RadiusKm)) {
            // 内含差一点点：取两圆最近边缘间的中点
            var gap = Mathf.Abs(a.RadiusKm - b.RadiusKm) - d;
            if (gap > NearMissToleranceKm) return res;
            var p = a.RadiusKm >= b.RadiusKm
                ? a.Center + delta / d * (a.RadiusKm - gap * 0.5f)
                : b.Center - delta / d * (b.RadiusKm - gap * 0.5f);
            res.Add(p);
            return res;
        }
        var t = (a.RadiusKm * a.RadiusKm - b.RadiusKm * b.RadiusKm + d * d) / (2f * d);
        var h2 = a.RadiusKm * a.RadiusKm - t * t;
        if (h2 < 0f) h2 = 0f; // 相切
        var mid = a.Center + delta * (t / d);
        var perp = new Vector2(-delta.y, delta.x) / d * Mathf.Sqrt(h2);
        res.Add(mid + perp);
        if (h2 > 1e-8f) res.Add(mid - perp); // 相切时两个点重合，只报一个
        return res;
    }

    /// <summary>方位线 ∩ 方位线：0~1 个交点（平行无解；交点必须在两条射线的前方）。</summary>
    public static List<Vector2> Intersect(BearingLine a, BearingLine b) {
        var res = new List<Vector2>();
        var d1 = a.Dir;
        var d2 = b.Dir;
        var cross = d1.x * d2.y - d1.y * d2.x;
        if (Mathf.Abs(cross) < 1e-6f) return res; // 平行/共线
        var w = b.Origin - a.Origin;
        var t = (w.x * d2.y - w.y * d2.x) / cross;
        var s = (w.x * d1.y - w.y * d1.x) / cross;
        if (t >= 0f && s >= 0f) res.Add(a.Origin + d1 * t);
        return res;
    }

    /// <summary>
    /// 对一组约束求解：两两求交生成候选点，去重后按"所有约束的总残差"升序排序。
    /// 约束少于两条时返回空（调用方应提示"情报不足"）。
    /// </summary>
    public static List<SurveyCandidate> Solve(IReadOnlyList<IGeoConstraint> constraints) {
        var res = new List<SurveyCandidate>();
        if (constraints.Count < 2) return res;

        for (var i = 0; i < constraints.Count; ++i) {
            for (var j = i + 1; j < constraints.Count; ++j) {
                var basis = $"{constraints[i].Describe()}  X  {constraints[j].Describe()}";
                foreach (var p in IntersectPair(constraints[i], constraints[j])) {
                    if (ExistsNear(res, p)) continue;
                    var score = 0f;
                    // 模糊约束（罗盘方位词）只产生交点、不参与投票：±11° 的误差在远距离
                    // 会贡献公里级残差，把精确约束的 0 残差淹没、把排序绑架（2026-08-13 实测）。
                    foreach (var c in constraints) {
                        if (c is BearingLine { Fuzzy: true }) continue;
                        score += c.Residual(p);
                    }
                    res.Add(new SurveyCandidate {
                        Point = p,
                        Score = score,
                        Basis = basis,
                        LowConfidence = PairConfidence(constraints[i], constraints[j], p) < LowConfidenceThreshold
                                        || IsFuzzy(constraints[i]) || IsFuzzy(constraints[j]),
                    });
                }
            }
        }
        res.Sort((x, y) => x.Score.CompareTo(y.Score));
        return res;
    }

    /// <summary>交点处两约束的夹角质量低于该值（|sin|）时标记低置信度（约小于 20° 的擦边相交）。</summary>
    private const float LowConfidenceThreshold = 0.35f;

    /// <summary>
    /// 交点处两条约束的"穿越程度"：|sin(夹角)|。1 = 正交（定位稳健），0 = 相切
    /// （报文的整数度/两位小数舍入会被几何放大，真解可沿约束滑出很远）。
    /// </summary>
    private static float PairConfidence(IGeoConstraint a, IGeoConstraint b, Vector2 p) {
        if (a is BearingLine la && b is BearingLine lb)
            return Mathf.Abs(Cross(la.Dir, lb.Dir));
        if (a is BearingLine la2 && b is DistanceCircle cb)
            return Mathf.Abs(Vector2.Dot(la2.Dir, (p - cb.Center).normalized));
        if (a is DistanceCircle ca && b is BearingLine lb2)
            return Mathf.Abs(Vector2.Dot(lb2.Dir, (p - ca.Center).normalized));
        if (a is DistanceCircle ca2 && b is DistanceCircle cb2)
            return Mathf.Abs(Cross((p - ca2.Center).normalized, (p - cb2.Center).normalized));
        return 1f;
    }

    private static bool IsFuzzy(IGeoConstraint c) => c is BearingLine { Fuzzy: true };

    private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    private static IEnumerable<Vector2> IntersectPair(IGeoConstraint a, IGeoConstraint b) {
        if (a is BearingLine la && b is BearingLine lb) return Intersect(la, lb);
        if (a is BearingLine la2 && b is DistanceCircle cb) return Intersect(la2, cb);
        if (a is DistanceCircle ca && b is BearingLine lb2) return Intersect(lb2, ca);
        if (a is DistanceCircle ca2 && b is DistanceCircle cb2) return Intersect(ca2, cb2);
        return new List<Vector2>();
    }

    private static bool ExistsNear(List<SurveyCandidate> list, Vector2 p) {
        foreach (var c in list) {
            if (Vector2.Distance(c.Point, p) < DedupeKm) return true;
        }
        return false;
    }
}
