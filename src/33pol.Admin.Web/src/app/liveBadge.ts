import type { ConnectionSnapshot } from '../realtime/types';

export interface LiveBadgeView {
  className: string;
  dotClass: string;
  text: string;
  title: string;
}

/** Connection mode label for `.live-badge` (perf harness + Overview header). */
export function liveBadgeView(conn: ConnectionSnapshot): LiveBadgeView {
  const mode = conn.mode;
  if (conn.status === 'fail') {
    return {
      className: 'live-badge is-fail',
      dotClass: '',
      text: 'Disconnected',
      title: 'Admin API key rejected',
    };
  }
  if (mode === 'stream') {
    return {
      className: 'live-badge is-live',
      dotClass: 'live',
      text: 'Live',
      title: 'Receiving SSE updates',
    };
  }
  if (mode === 'reconnecting' || mode === 'connecting') {
    return {
      className: 'live-badge is-warn',
      dotClass: '',
      text: 'Reconnecting',
      title: 'Reconnecting to live stream',
    };
  }
  if (mode === 'polling') {
    return {
      className: 'live-badge is-poll',
      dotClass: '',
      text: 'Polling',
      title: 'Polling every 2s',
    };
  }
  return {
    className: 'live-badge muted',
    dotClass: '',
    text: 'Idle',
    title: 'Not connected',
  };
}
