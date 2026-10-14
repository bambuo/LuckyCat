namespace LuckyCat.Contracts;

/// <summary>布尔判据的 null 表示未知，不允许默认入场。参考目标不证明未来可达。</summary>
public sealed record SignalSideAssessment(bool? Direction, bool? Momentum, bool? Position, bool? Reward,
    decimal? EntryReference, decimal? StopDistance, decimal? TargetReference, decimal? NetRiskRewardRatio,
    IReadOnlyList<string> FailedConditions)
{
    public bool Ready => Direction == true && Momentum == true && Position == true && Reward == true;
}

public sealed record SignalAssessment(string Symbol, string? DirectionTimeframe, bool DefaultEntry,
    SignalSideAssessment Long, SignalSideAssessment Short);
