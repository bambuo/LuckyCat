using System.Diagnostics;
using System.Runtime.CompilerServices;
using Binance.Net;
using Bitget.Net;
using Bybit.Net;
using CryptoClients.Net;
using CryptoClients.Net.Clients;
using CryptoClients.Net.Models;
using CryptoExchange.Net.SharedApis;
using GateIo.Net;
using Kucoin.Net;
using LuckyCat.Contracts;
using LuckyCat.Models;
using OKX.Net;

namespace LuckyCat.Services;

public class ExchangeVerificationRunner
{
    public const string CheckConnect = "connect";
    public const string CheckAuth = "auth";

    private static readonly string[] CheckIds = [CheckConnect, CheckAuth];
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly SharedSymbol BtcUsdt = new(TradingMode.Spot, "BTC", "USDT");

    public async IAsyncEnumerable<ExchangeVerifyEvent> RunAsync(
        ExchangeAccount account,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ExchangeSharedApiClient? client = null;
        string? createError = null;
        try
        {
            client = CreateClient(account);
        }
        catch (Exception ex)
        {
            createError = ex.Message;
        }

        // 客户端创建失败也产出完整事件序列，保证 SSE 契约成立
        if (client is null)
        {
            foreach (var checkId in CheckIds)
            {
                yield return new ExchangeVerifyEvent("start", Check: checkId);
                yield return new ExchangeVerifyEvent("result", Check: checkId, Status: "fail",
                    ElapsedMs: 0, Detail: Truncate(createError));
            }
            yield return new ExchangeVerifyEvent("done", Passed: 0, Failed: CheckIds.Length);
            yield break;
        }

        var passed = 0;
        var failed = 0;
        foreach (var checkId in CheckIds)
        {
            yield return new ExchangeVerifyEvent("start", Check: checkId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CheckTimeout);

            var evt = checkId == CheckConnect
                ? await RunConnectAsync(client, account.ExchangeType, timeoutCts.Token)
                : await RunAuthAsync(client, account, timeoutCts.Token);

            if (evt.Status == "pass") passed++;
            else failed++;
            yield return evt;
        }

        yield return new ExchangeVerifyEvent("done", Passed: passed, Failed: failed);
    }

    private static ExchangeSharedApiClient CreateClient(ExchangeAccount account)
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

    private static async Task<ExchangeVerifyEvent> RunConnectAsync(
        ExchangeSharedApiClient client, ExchangeType exchangeType, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ticker = client.GetCapability<IGetTicker>(exchangeType.ToString(), TradingMode.Spot);
            if (ticker is null)
            {
                return Fail(CheckConnect, sw, "该交易所不支持行情查询能力");
            }

            var result = await ticker.Capability
                .GetTickerAsync(new GetTickerRequest(BtcUsdt), ct)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return Fail(CheckConnect, sw, result.Error?.ToString() ?? "行情请求失败");
            }

            var price = result.Data.LastPrice;
            if (price <= 0)
            {
                return Fail(CheckConnect, sw, "行情返回价格异常");
            }

            return new ExchangeVerifyEvent("result", Check: CheckConnect, Status: "pass",
                ElapsedMs: sw.ElapsedMilliseconds,
                Detail: "公共行情接口连通",
                Evidence: Truncate($"BTC/USDT = {price}"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(CheckConnect, sw, "超时(30s)");
        }
        catch (Exception ex)
        {
            return Fail(CheckConnect, sw, ex.Message);
        }
    }

    private static async Task<ExchangeVerifyEvent> RunAuthAsync(
        ExchangeSharedApiClient client, ExchangeAccount account, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 与实盘口径一致：查 USDT 永续合约账户余额（本工具交易走合约，验证应要求「合约读取」权限而非现货）
            var exchangeType = account.ExchangeType;
            var parameters = ExchangeClientFactory.Parameters(account);
            var balances = client.GetCapability<IGetBalances>(exchangeType.ToString(), TradingMode.PerpetualLinear);
            if (balances is null)
            {
                return Fail(CheckAuth, sw, "该交易所不支持合约账户余额查询能力");
            }

            var result = await balances.Capability
                .GetBalancesAsync(new GetBalancesRequest(TradingMode.PerpetualLinear, parameters), ct)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return Fail(CheckAuth, sw, result.Error?.ToString() ?? "余额查询失败");
            }

            var all = result.Data?.ToList() ?? [];
            var nonZero = all.Where(b => b.Total > 0).ToList();
            var summary = string.Join(", ", nonZero.Take(5).Select(b => $"{b.Asset} {b.Total}"));
            var evidence = nonZero.Count == 0
                ? "账户余额为空"
                : Truncate(nonZero.Count > 5 ? $"{summary} …（共 {nonZero.Count} 个币种）" : summary);

            return new ExchangeVerifyEvent("result", Check: CheckAuth, Status: "pass",
                ElapsedMs: sw.ElapsedMilliseconds,
                Detail: $"凭证有效，合约账户共 {nonZero.Count} 个非零余额",
                Evidence: evidence);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(CheckAuth, sw, "超时(30s)");
        }
        catch (Exception ex)
        {
            return Fail(CheckAuth, sw, ex.Message);
        }
    }

    private static ExchangeVerifyEvent Fail(string check, Stopwatch sw, string message) =>
        new("result", Check: check, Status: "fail", ElapsedMs: sw.ElapsedMilliseconds, Detail: Truncate(message));

    private static string Truncate(string? value, int max = 160)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max] + "…";
    }
}
