import { render } from 'solid-js/web';
import './styles/app.css';
import { initTheme } from './app/theme';
import App from './App';

initTheme();

const root = document.getElementById('root');

if (!root) {
  throw new Error('Root element #root not found');
}

render(() => <App />, root);
