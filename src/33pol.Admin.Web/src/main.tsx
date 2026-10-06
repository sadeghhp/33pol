import { render } from 'solid-js/web';
import './styles/app.css';
import { migrateLegacyHash } from './app/hashRedirect';
import { initTheme } from './app/theme';
import App from './App';

migrateLegacyHash();
initTheme();

const root = document.getElementById('root');

if (!root) {
  throw new Error('Root element #root not found');
}

render(() => <App />, root);
