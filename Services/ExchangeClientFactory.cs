using Binance.Net;
using Bitget.Net;
using Bybit.Net;
using CryptoClients.Net;
using CryptoClients.Net.Clients;
using CryptoClients.Net.Models;
using CryptoExchange.Net.SharedApis;
using GateIo.Net;
using Kucoin.Net;
using OKX.Net;
using System.Collections.Concurrent;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>交易所共享客户端工厂：凭证装配（含 Gate 结算资产参数）与按账户缓存（官方指引：客户端应复用，不要每请求新建）。</summary>
public static class ExchangeClientFactory
{
    private static readonly ConcurrentDictionary<string, ExchangeSharedApiClient> Cache = new();

    /// <summary>按账户缓存客户端；账户凭证/类型变化（UpdatedAt 变化）时自动重建。</summary>
    public static ExchangeSharedApiClient Get(ExchangeAccount account)
    {
        var key = $"{account.Id}:{account.UpdatedAt.Ticks}";
        return Cache.GetOrAdd(key, _ => Create(account));
    }

    public static ExchangeSharedApiClient Create(ExchangeAccount account)
    {
        var credentials = account.ExchangeType switch
        {
            ExchangeType.Binance => new ExchangeCredentials
            {
                Binance = new BinanceCredentials(account.ApiKey, account.ApiSecret),
            },
            ExchangeType.Bybit => new ExchangeCredentials
            {
                Bybit = new BybitCredentials(account.ApiKey, account.ApiSecret),
            },
            ExchangeType.OKX => new ExchangeCredentials
            {
                OKX = new OKXCredentials(account.ApiKey, account.ApiSecret, account.Passphrase!),
            },
            ExchangeType.Bitget => new ExchangeCredentials
            {
                Bitget = new BitgetCredentials(account.ApiKey, account.ApiSecret, account.Passphrase!),
            },
            ExchangeType.GateIo => new ExchangeCredentials
            {
                GateIo = new GateIoCredentials(account.ApiKey, account.ApiSecret),
            },
            ExchangeType.Kucoin => new ExchangeCredentials
            {
                Kucoin = new KucoinCredentials(account.ApiKey, account.ApiSecret, account.Passphrase!),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(account), account.ExchangeType, "未知交易所类型"),
        };

        var configuration = new CryptoClientsConfiguration(builder => builder
            .ConfigureGlobal(options =>
            {
                options.ApiCredentials = credentials;
                options.RequestTimeout = TimeSpan.FromSeconds(20);
            }));

        return new ExchangeSharedApiClient(configuration);
    }

    /// <summary>交易所名称（共享能力查询用）：与 ExchangeType 枚举名一致。</summary>
    public static string Name(ExchangeType type) => type.ToString();

    /// <summary>
    /// 交易所参数：Gate.io 线性永续需要显式指定结算资产（库侧强制）；用户数据流 WS 的签名需要账户 UID（UserId 参数）。
    /// 其余交易所无需。
    /// </summary>
    public static ExchangeParameters? Parameters(ExchangeAccount account)
    {
        if (account.ExchangeType != ExchangeType.GateIo) return null;

        List<ExchangeParameter> list = [new("GateIo", "SettleAsset", "usdt")];
        if (!string.IsNullOrWhiteSpace(account.UserId))
        {
            list.Add(new ExchangeParameter("GateIo", "UserId", account.UserId.Trim()));
        }
        return new ExchangeParameters([.. list]);
    }
}
