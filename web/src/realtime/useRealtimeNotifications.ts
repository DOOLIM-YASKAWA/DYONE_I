import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { API_BASE, tokenStore } from '../api/client'
import type { AppNotification } from '../api/types'
import { useToast } from '../components/Toast'
import { platform } from '../platform/bridge'

/**
 * 서버 알림을 실시간으로 받는다(SignalR).
 * PC 웹: 화면 안 토스트 + 알림함에 추가 · Windows 앱: 추가로 Windows 토스트 알림
 * 접속 전에 온 알림은 알림함 목록(GET /api/notifications)으로 받고, 연결이 끊겼다 다시 붙으면 목록을 다시 받는다.
 */
export function useRealtimeNotifications(enabled: boolean) {
  const queryClient = useQueryClient()
  const toast = useToast()

  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE}/hubs/notifications`, { accessTokenFactory: () => tokenStore.get() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('notification', (n: AppNotification) => {
      queryClient.setQueryData<AppNotification[]>(['notifications'], (old = []) => [n, ...old.filter((o) => o.id !== n.id)])
      toast(`${n.title} · ${n.body}`)
      void platform.showSystemNotification(n.title, n.body, n.link ?? undefined).catch(() => {})
    })

    // 끊긴 동안 온 알림을 놓치지 않게 다시 붙으면 알림함을 새로 받는다
    connection.onreconnected(() => void queryClient.invalidateQueries({ queryKey: ['notifications'] }))

    let stopped = false
    connection.start().catch((err) => {
      if (!stopped) console.warn('실시간 알림 연결 실패', err)
    })
    return () => {
      stopped = true
      void connection.stop()
    }
  }, [enabled, queryClient, toast])
}
