import { HashRouter, Route } from '@solidjs/router';
import { AuthGate } from './app/AuthGate';
import { Shell } from './app/Shell';
import { routes } from './app/router';

export default function App() {
  return (
    <AuthGate>
      <HashRouter root={(props) => <Shell>{props.children}</Shell>}>
        {routes.map((r) => (
          <Route path={r.path} component={r.component} />
        ))}
      </HashRouter>
    </AuthGate>
  );
}
