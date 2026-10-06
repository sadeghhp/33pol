import { lazy } from 'solid-js';
import type { RouteDefinition } from '@solidjs/router';
import { Navigate } from '@solidjs/router';

const OverviewPage = lazy(() => import('../pages/overview/OverviewPage'));
const UsagePage = lazy(() => import('../pages/usage/UsagePage'));
const RoutingPage = lazy(() => import('../pages/routing/RoutingPage'));
const KeysPage = lazy(() => import('../pages/keys/KeysPage'));
const LogsPage = lazy(() => import('../pages/logs/LogsPage'));
const ErrorsPage = lazy(() => import('../pages/errors/ErrorsPage'));
const SettingsPage = lazy(() => import('../pages/settings/SettingsPage'));

export const routes: RouteDefinition[] = [
  { path: '/', component: () => <Navigate href="/dashboard" /> },
  { path: '/dashboard', component: OverviewPage },
  { path: '/usage', component: UsagePage },
  { path: '/routing', component: RoutingPage },
  { path: '/keys', component: KeysPage },
  { path: '/logs', component: LogsPage },
  { path: '/errors', component: ErrorsPage },
  { path: '/settings', component: SettingsPage },
  { path: '/models', component: () => <Navigate href="/routing?sub=models" /> },
  { path: '/backends', component: () => <Navigate href="/routing?sub=backends" /> },
  { path: '/*', component: () => <Navigate href="/dashboard" /> },
];

export function tabFromPath(path: string): string {
  const p = path.replace(/^\//, '') || 'dashboard';
  if (p === 'models' || p === 'backends') return 'routing';
  return p;
}
