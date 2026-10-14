using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LuckyCat.Services;

/// <summary>SSE 事件：Name 为 SSE 事件名；Payload 为域对象（可空），由 SSE 端点格式化为帧。</summary>
public sealed record SseEvent(string Name, object? Payload);

/// <summary>SSE 频道键：按「交易员」与「账户」两个维度路由——模块用手头已有的主键发布即可。</summary>
public static class SseChannels
{
    public static string Trader(int traderId) => $"trader:{traderId}";
    public static string Account(int accountId) => $"account:{accountId}";
}

/// <summary>
/// SSE 事件总线（进程内）：模块（决策记忆/交易执行/净值采样/仓位同步等）发布事件即可，
/// SSE 端点订阅后转发到各自的连接——发布方与连接管理解耦，不需要知道有没有订阅者；无订阅者时发布为空操作。
/// 每个订阅者一条无界通道：事件为分钟级、量小无需背压；订阅对象释放即退订。
/// </summary>
public sealed class SseEventHub
{
    private readonly ConcurrentDictionary<string, List<Channel<SseEvent>>> _subscribers = new(StringComparer.Ordinal);

    /// <summary>订阅某频道的事件流；返回的订阅对象释放即退订（SSE 连接断开时调用）。</summary>
    public (ChannelReader<SseEvent> Reader, IDisposable Subscription) Subscribe(string key)
    {
        var queue = Channel.CreateUnbounded<SseEvent>();
        var list = _subscribers.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.Add(queue);
        }
        return (queue.Reader, new Subscription(this, key, queue));
    }

    /// <summary>发布事件到当前订阅该频道的全部连接。</summary>
    public void Publish(string key, SseEvent evt)
    {
        if (!_subscribers.TryGetValue(key, out var list)) return;
        lock (list)
        {
            foreach (var queue in list)
            {
                queue.Writer.TryWrite(evt);
            }
        }
    }

    private sealed class Subscription(SseEventHub hub, string key, Channel<SseEvent> queue) : IDisposable
    {
        public void Dispose()
        {
            queue.Writer.TryComplete();
            if (hub._subscribers.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    list.Remove(queue);
                }
            }
        }
    }
}
