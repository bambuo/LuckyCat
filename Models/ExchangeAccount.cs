namespace LuckyCat.Models;

public enum ExchangeType
{
    Binance,
    Bybit,
    OKX,
    Bitget,
    GateIo,
    Kucoin,
}

public class ExchangeAccount
{
    public int Id { get; set; }
    public required ExchangeType ExchangeType { get; set; }
    public required string AccountName { get; set; }
    public required string ApiKey { get; set; }
    public required string ApiSecret { get; set; }
    public string? Passphrase { get; set; }

    /// <summary>交易所用户 ID（UID）：Gate.io 的用户数据流 WS 订阅需要（数字）；其余交易所不用。</summary>
    public string? UserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
