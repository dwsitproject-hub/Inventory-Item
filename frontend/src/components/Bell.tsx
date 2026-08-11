import { useEffect, useRef, useState } from 'react'
import { Notification, markAllRead, notifications } from '../api'

const badgeClass = (t: string) =>
  t === 'upload' ? 'b-info' : t === 'quarantine' ? 'b-warn' : 'b-err'

export default function Bell() {
  const [unread, setUnread] = useState(0)
  const [items, setItems] = useState<Notification[]>([])
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  async function refresh() {
    try {
      const d = await notifications()
      setUnread(d.unread)
      setItems(d.items)
    } catch { /* ignore while logged out */ }
  }

  useEffect(() => {
    refresh()
    const t = setInterval(refresh, 30000)     // poll (SignalR push at build-out)
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('click', close)
    return () => { clearInterval(t); document.removeEventListener('click', close) }
  }, [])

  async function toggle() {
    const next = !open
    setOpen(next)
    if (next && unread > 0) {
      await markAllRead()
      setUnread(0)
      setItems(list => list.map(i => ({ ...i, read: true })))
    }
  }

  return (
    <div ref={ref} style={{ position: 'relative' }}>
      <div style={{ position: 'relative', cursor: 'pointer', fontSize: 18, color: 'var(--navy)' }} onClick={toggle}>
        🔔
        {unread > 0 && (
          <span style={{
            position: 'absolute', top: -4, right: -8, background: 'var(--red)', color: '#fff',
            fontSize: 9, fontWeight: 700, borderRadius: 9, padding: '1px 5px'
          }}>{unread}</span>
        )}
      </div>
      {open && (
        <div className="colpanel" style={{ right: -10, top: 30, width: 360 }}>
          <h4>Notifications</h4>
          {items.map(n => (
            <div key={n.id} style={{ padding: '7px 6px', borderBottom: '1px solid var(--line)', fontSize: 12.5, opacity: n.read ? .75 : 1 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                <b style={{ color: 'var(--navy)' }}>{n.title}</b>
                <span className={'badge ' + badgeClass(n.eventType)}>{n.eventType}</span>
              </div>
              {n.body && <div style={{ color: 'var(--muted)', marginTop: 2 }}>{n.body}</div>}
              <div style={{ color: 'var(--muted)', fontSize: 10.5, marginTop: 2 }}>{new Date(n.createdAt).toLocaleString()}</div>
            </div>
          ))}
          {items.length === 0 && <div className="loading">no notifications yet</div>}
        </div>
      )}
    </div>
  )
}
