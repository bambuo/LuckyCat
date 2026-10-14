import type { ExchangeType } from './types'

export interface ExchangePreset {
  key: ExchangeType
  label: string
  needsPassphrase: boolean
  /** 用户数据流 WS 签名需要账户 UID（目前仅 Gate.io） */
  needsUserId: boolean
  icon: string
}

export const EXCHANGE_PRESETS: ExchangePreset[] = [
  { key: 'Binance', label: 'Binance', needsPassphrase: false, needsUserId: false, icon: '/icons/binance.svg' },
  { key: 'Bybit', label: 'Bybit', needsPassphrase: false, needsUserId: false, icon: '/icons/bybit.ico' },
  { key: 'OKX', label: 'OKX', needsPassphrase: true, needsUserId: false, icon: '/icons/okx.svg' },
  { key: 'Bitget', label: 'Bitget', needsPassphrase: true, needsUserId: false, icon: '/icons/bitget.ico' },
  { key: 'GateIo', label: 'Gate.io', needsPassphrase: false, needsUserId: true, icon: '/icons/gateio.ico' },
  { key: 'Kucoin', label: 'KuCoin', needsPassphrase: true, needsUserId: false, icon: '/icons/kucoin.svg' },
]

export function getExchangePreset(key: ExchangeType): ExchangePreset | undefined {
  return EXCHANGE_PRESETS.find((preset) => preset.key === key)
}

export function exchangeLabel(key: ExchangeType): string {
  return getExchangePreset(key)?.label ?? key
}

export function exchangeLetter(key: ExchangeType): string {
  return key.charAt(0).toUpperCase()
}
