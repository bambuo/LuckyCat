namespace LuckyCat.Contracts;

// 配置 JSON 契约（camelCase 出入，属性名 PascalCase，STJ web 默认大小写不敏感）
public class StrategyConfig
{
    public CoinSourceConfig CoinSource { get; set; } = new();
    public IndicatorSettings Indicators { get; set; } = new();
    public RiskControlConfig RiskControl { get; set; } = new();
    public PromptSectionConfig PromptSections { get; set; } = new();
    public string CustomPrompt { get; set; } = "";
    public bool DecisionMemoryEnabled { get; set; } = true;
    public bool NewsEnabled { get; set; } = true;
    public RoleInstructionConfig RoleInstructions { get; set; } = new();
    public AgentBudgetConfig AgentBudget { get; set; } = new();
    public ReviewConfig Review { get; set; } = new();
}

public class CoinSourceConfig
{
    public string SourceType { get; set; } = "static";          // static | score | oi_top | oi_low | mixed
    public string Exchange { get; set; } = "binance";           // binance | gate
    public List<string> StaticCoins { get; set; } = ["BTC", "ETH", "SOL"];
    public List<string> ExcludedCoins { get; set; } = [];
    public bool UseScore { get; set; }
    public int ScoreLimit { get; set; } = 10;
    public bool UseOiTop { get; set; }
    public int OiTopLimit { get; set; } = 10;
    public bool UseOiLow { get; set; }
    public int OiLowLimit { get; set; } = 10;
}

public class IndicatorSettings
{
    public KlineConfig Klines { get; set; } = new();
    public bool EnableRawKlines { get; set; } = true;
    public IndicatorConfig Ema { get; set; } = new() { Enabled = true, Periods = [20, 50] };
    public IndicatorConfig Macd { get; set; } = new() { Enabled = true, Periods = [12, 26, 9] };
    public IndicatorConfig Rsi { get; set; } = new() { Enabled = true, Periods = [7, 14] };
    public IndicatorConfig Atr { get; set; } = new() { Enabled = true, Periods = [14] };
    public IndicatorConfig Boll { get; set; } = new() { Enabled = false, Periods = [20] };
    public IndicatorConfig Vwap { get; set; } = new() { Enabled = false, Periods = [] };
    public SentimentConfig Volume { get; set; } = new() { Enabled = true };
    public SentimentConfig OpenInterest { get; set; } = new() { Enabled = true };
    public SentimentConfig FundingRate { get; set; } = new() { Enabled = true };
    public List<ExternalDataSource> ExternalDataSources { get; set; } = [];
}

public class KlineConfig
{
    public string PrimaryTimeframe { get; set; } = "5m";
    public int PrimaryCount { get; set; } = 30;
    public bool EnableMultiTimeframe { get; set; } = true;
    public List<string> SelectedTimeframes { get; set; } = ["5m", "15m", "1h"];
}

public class IndicatorConfig
{
    public bool Enabled { get; set; }
    public List<int> Periods { get; set; } = [];
}

public class SentimentConfig
{
    public bool Enabled { get; set; }
}

public class ExternalDataSource
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";                 // GET | POST
    public string Url { get; set; } = "";
    public string DataPath { get; set; } = "";
    public int RefreshSecs { get; set; } = 300;
    public List<ExternalHeader> Headers { get; set; } = [];
}

public class ExternalHeader
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class RiskControlConfig
{
    public int MaxPositions { get; set; } = 3;
    public int BtcEthLeverage { get; set; } = 5;
    public int AltcoinLeverage { get; set; } = 3;
    public decimal BtcEthPositionPct { get; set; } = 3m;
    public decimal AltcoinPositionPct { get; set; } = 1m;
    public decimal MinRiskRewardRatio { get; set; } = 1.5m;
    public int MaxMarginUsage { get; set; } = 60;
    public int MinPositionSize { get; set; } = 12;
    public int MinConfidence { get; set; } = 70;
    public decimal MaxRiskPerTradePct { get; set; } = 1.5m;
    /// <summary>往返手续费估算预算，百分点；不是交易所实际费率。</summary>
    public decimal EstimatedFeePct { get; set; } = 0.10m;
    /// <summary>往返滑点估算预算，百分点。</summary>
    public decimal EstimatedSlippagePct { get; set; } = 0.05m;
    /// <summary>预计持仓期间支付的资金费预算，百分点；不计预期收入。</summary>
    public decimal EstimatedFundingPct { get; set; } = 0.01m;
    /// <summary>执行价相对模型参考价的最大偏离，百分点。</summary>
    public decimal MaxEntryDeviationPct { get; set; } = 0.5m;
}

public class PromptSectionConfig
{
    public string Role { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Workflow { get; set; } = "";
}

// 五角色附加指令：拼接在对应角色提示词末尾。
public class RoleInstructionConfig
{
    public string Analyst { get; set; } = "";
    public string Trader { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Executor { get; set; } = "";
    public string Reviewer { get; set; } = "";
}

// 智能体预算：阶段超时、工具调用上限与单周期输入 token 预算。
public class AgentBudgetConfig
{
    public int AnalystTimeoutSeconds { get; set; } = 90;
    public int TraderTimeoutSeconds { get; set; } = 90;
    public int RiskTimeoutSeconds { get; set; } = 45;
    public int ExecutorTimeoutSeconds { get; set; } = 45;
    public int CycleTimeoutSeconds { get; set; } = 300;
    public int AnalystToolLimit { get; set; } = 20;
    public int TraderToolLimit { get; set; } = 14;
    public int RiskToolLimit { get; set; } = 4;
    public int ExecutorToolLimit { get; set; } = 6;
    public int CycleToolLimit { get; set; } = 45;
    public int InputTokenBudget { get; set; } = 150000;
}

// 每日复盘调度配置。
public class ReviewConfig
{
    public bool Enabled { get; set; } = true;
    public string LocalTime { get; set; } = "00:10";
    public int MaxLookbackDays { get; set; } = 30;
}
