import { createContext, use, useCallback, useMemo, useRef, useState, type ReactNode } from 'react'

const ToastContext = createContext<(text: string) => void>(() => {})

export function ToastProvider({ children }: { children: ReactNode }) {
  const [text, setText] = useState<string | null>(null)
  const timer = useRef<number | undefined>(undefined)

  const notify = useCallback((message: string) => {
    window.clearTimeout(timer.current)
    setText(message)
    timer.current = window.setTimeout(() => setText(null), 3200)
  }, [])

  const value = useMemo(() => notify, [notify])
  return (
    <ToastContext value={value}>
      {children}
      <div id="toast" role="status" hidden={text === null}>
        {text}
      </div>
    </ToastContext>
  )
}

export const useToast = () => use(ToastContext)
