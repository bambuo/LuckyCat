import type {
  CoinSourceExchange,
  CoinSourceType,
} from './types'

// 周期分类（对齐设计稿：超短/日内/波段/趋势；3D 无 CryptoClients 对应粒度，不提供）
export const TIMEFRAME_GROUPS: { key: string; label: string; color: string; options: string[] }[] = [
  { key: 'ultra', label: '超短', color: 'rgb(var(--danger-6))', options: ['1m', '3m', '5m'] },
  { key: 'intraday', label: '日内', color: 'rgb(var(--primary-6))', options: ['15m', '30m', '1h'] },
  { key: 'swing', label: '波段', color: 'rgb(var(--success-6))', options: ['2h', '4h', '6h', '8h', '12h'] },
  { key: 'trend', label: '趋势', color: '#f0b90b', options: ['1D', '1W'] },
]

export const COIN_SOURCE_CATALOG: { key: CoinSourceType; label: string; desc: string }[] = [
  { key: 'static', label: '静态列表', desc: '手动指定候选币种' },
  { key: 'score', label: '综合评分榜', desc: '24h 成交额与波动综合评分前 N' },
  { key: 'oi_top', label: 'OI 持仓增加', desc: '1h 持仓量增幅榜前 N' },
  { key: 'oi_low', label: 'OI 持仓减少', desc: '1h 持仓量减幅榜前 N' },
  { key: 'mixed', label: '混合模式', desc: '多来源组合，去重合并' },
]

export function coinSourceLabel(key: CoinSourceType): string {
  return COIN_SOURCE_CATALOG.find((s) => s.key === key)?.label ?? key
}

export const COIN_SOURCE_EXCHANGES: { key: CoinSourceExchange; label: string; dot: string; desc: string }[] = [
  { key: 'binance', label: 'Binance', dot: '#F0B90B', desc: 'Binance USDT 永续公开行情与 OI 历史' },
  { key: 'gate', label: 'Gate.io', dot: '#2F8FFF', desc: 'Gate.io USDT 永续公开行情与 OI 历史' },
]

export type IndicatorKey = 'ema' | 'macd' | 'rsi' | 'atr' | 'boll' | 'vwap'
export type SentimentKey = 'volume' | 'openInterest' | 'fundingRate'

export const INDICATOR_CATALOG: { key: IndicatorKey; label: string; color: string; desc: string }[] = [
  { key: 'ema', label: 'EMA', color: '#3B82F6', desc: '指数移动平均，判断趋势方向' },
  { key: 'macd', label: 'MACD', color: '#a855f7', desc: '动能与背离，柱状图为主信号' },
  { key: 'rsi', label: 'RSI', color: '#EF4444', desc: '超买超卖与动能强弱' },
  { key: 'atr', label: 'ATR', color: '#f0b90b', desc: '波动率，用于止损距离计算' },
  { key: 'boll', label: 'BOLL 布林带', color: '#ec4899', desc: '波动通道与突破判断' },
  { key: 'vwap', label: 'VWAP', color: '#14b8a6', desc: '成交量加权均价，日内情绪基准' },
]

export const SENTIMENT_CATALOG: { key: SentimentKey; label: string; color: string; desc: string }[] = [
  { key: 'volume', label: '成交量', color: '#c084fc', desc: '放量 / 缩量确认' },
  { key: 'openInterest', label: '持仓量', color: '#34d399', desc: 'OI 变化反映资金进出' },
  { key: 'fundingRate', label: '资金费率', color: '#fbbf24', desc: '多空拥挤度' },
]

export const LIMIT_OPTIONS = [5, 10, 15, 20, 30, 50]
