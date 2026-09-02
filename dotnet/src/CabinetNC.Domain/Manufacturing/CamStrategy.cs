namespace CabinetNC.Domain.Manufacturing;

/// <summary>
/// Shop-facing CAM taxonomy. Feature ops (contour/pocket/drill/groove)
/// collapse to these three strategies for UI and recipes.
/// </summary>
public enum CamStrategyKind
{
    Drilling,
    AreaClearance,
    Profile,
    Guillotine,
}

/// <summary>Troy shop cutting order shown on stage 4.</summary>
public enum TroyPassKind
{
    TongueGroove,
    Clearance,
    ProfileFirst,
    ProfileLast,
    Drilling,
    UnclassifiedGroove,
}

public static class TroyRecipe
{
    public const string TongueToolId = "T1";
    public const string WorkToolId = "T2";
    public const double TongueDiameterMm = 6.35;
    public const double WorkDiameterMm = 10;
    public const double PlungeFeedMmMin = 1000;
    public const double TongueFeedMmMin = 9000;
    public const double WorkFirstFeedMmMin = 12000;
    public const double WorkLastFeedMmMin = 20000;
    public const double SpindleRpm = 14500;
    public const double SafeZMm = 30;
    public const double LastPassLeaveMm = 0.5;
    public const double BridgeLeaveMm = 1.45;
    public const double ThroughZMm = -0.55;
    public const double GuillotineFeedMmMin = 9000;
    public const double GuillotinePlungeMmMin = 1000;
    public const double GuillotineThroughZMm = -0.55;
}

public static class CamStrategy
{
    /// <summary>Groove wider than this × tool Ø is cleared, not a single profile pass.</summary>
    public const double WideGrooveFactor = 1.15;

    public static bool NeedsGrooveClear(double widthMm, double toolDiameterMm) =>
        widthMm > 1e-9 && toolDiameterMm > 1e-9
        && widthMm > toolDiameterMm * WideGrooveFactor;

    public static CamStrategyKind Classify(CutOp op, double toolDiameterMm = 0)
    {
        var kind = op.Op ?? "";
        if (kind.Equals("drill", StringComparison.OrdinalIgnoreCase))
            return CamStrategyKind.Drilling;
        if (kind.Equals("remnant", StringComparison.OrdinalIgnoreCase))
            return CamStrategyKind.Guillotine;
        if (kind.Equals("pocket", StringComparison.OrdinalIgnoreCase))
            return CamStrategyKind.AreaClearance;
        if (kind.Equals("groove", StringComparison.OrdinalIgnoreCase))
            return op.IsTongue ? CamStrategyKind.Profile : CamStrategyKind.AreaClearance;
        return CamStrategyKind.Profile;
    }

    public static string Title(CamStrategyKind kind) => kind switch
    {
        CamStrategyKind.Drilling => "Drilling",
        CamStrategyKind.AreaClearance => "Area Clearance",
        CamStrategyKind.Guillotine => "Guillotine cut",
        _ => "Profile",
    };

    public static string Hint(CamStrategyKind kind) => kind switch
    {
        CamStrategyKind.Drilling => "孔",
        CamStrategyKind.AreaClearance => "口袋 / 宽槽清底",
        CamStrategyKind.Guillotine => "余料分割线",
        _ => "外轮廓 / 开窗 / 半槽",
    };
}

public static class TroyPass
{
    public static TroyPassKind Classify(CutOp op)
    {
        var kind = op.Op ?? "";
        if (kind.Equals("drill", StringComparison.OrdinalIgnoreCase))
            return TroyPassKind.Drilling;
        if (kind.Equals("groove", StringComparison.OrdinalIgnoreCase))
            return op.IsTongue ? TroyPassKind.TongueGroove : TroyPassKind.UnclassifiedGroove;
        if (kind.Equals("pocket", StringComparison.OrdinalIgnoreCase))
            return TroyPassKind.Clearance;
        return TroyPassKind.ProfileFirst;
    }

    public static bool InPass(CutOp op, TroyPassKind? pass)
    {
        if (pass is null) return true;
        var kind = op.Op ?? "";
        return pass switch
        {
            TroyPassKind.TongueGroove => kind.Equals("groove", StringComparison.OrdinalIgnoreCase) && op.IsTongue,
            TroyPassKind.UnclassifiedGroove => kind.Equals("groove", StringComparison.OrdinalIgnoreCase) && !op.IsTongue,
            TroyPassKind.Clearance => kind.Equals("pocket", StringComparison.OrdinalIgnoreCase)
                || (kind.Equals("groove", StringComparison.OrdinalIgnoreCase) && !op.IsTongue),
            TroyPassKind.ProfileFirst or TroyPassKind.ProfileLast =>
                kind.Equals("contour", StringComparison.OrdinalIgnoreCase),
            TroyPassKind.Drilling => kind.Equals("drill", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    public static string Title(TroyPassKind kind) => kind switch
    {
        TroyPassKind.TongueGroove => "T1 半槽",
        TroyPassKind.UnclassifiedGroove => "未分类槽",
        TroyPassKind.Clearance => "T2 清底",
        TroyPassKind.ProfileFirst => "T2 外形第一刀",
        TroyPassKind.ProfileLast => "T2 外形最后一刀",
        TroyPassKind.Drilling => "钻孔",
        _ => "刀路",
    };

    public static string Hint(TroyPassKind kind) => kind switch
    {
        TroyPassKind.TongueGroove => "插 tongue · Ø6.35 · F9000 · 按槽深；宽于刀则回转清满",
        TroyPassKind.UnclassifiedGroove => "未标 tongue，按槽宽选刀。点开可改成半槽",
        TroyPassKind.Clearance => "口袋 / 铰杯 / 其它槽 · 按短边选刀 · F12000",
        TroyPassKind.ProfileFirst => "留皮 0.5mm · F12000",
        TroyPassKind.ProfileLast => "切穿 −0.55 · F20000 · 过桥留 0.5",
        TroyPassKind.Drilling => "孔",
        _ => "",
    };

    public static string RecipeCard() =>
        "T1 Ø6.35  半槽 F9000  S14500\n" +
        "T2 Ø10  清底/第一刀 F12000  最后一刀 F20000  下刀 F1000  S14500\n" +
        "安全高 30  ·  外形留皮 0.5 → 切穿 −0.55  ·  深度跟板件特征";

    public static bool InToolpath(CutOp op, OpsToolpathKind? kind) => kind switch
    {
        null => true,
        OpsToolpathKind.Tongue => InPass(op, TroyPassKind.TongueGroove)
            || InPass(op, TroyPassKind.UnclassifiedGroove),
        OpsToolpathKind.FirstRound => InPass(op, TroyPassKind.Clearance)
            || InPass(op, TroyPassKind.ProfileFirst),
        OpsToolpathKind.LastRound => InPass(op, TroyPassKind.ProfileLast),
        _ => true,
    };
}

/// <summary>The three shop toolpaths shown as stage-4 icons.</summary>
public enum OpsToolpathKind
{
    Tongue,
    FirstRound,
    LastRound,
}
