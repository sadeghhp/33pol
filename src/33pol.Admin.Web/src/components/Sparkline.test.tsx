/** @vitest-environment jsdom */
import { render } from '@solidjs/testing-library';
import { describe, expect, it } from 'vitest';
import { Sparkline } from './Sparkline';

describe('Sparkline', () => {
  it('Sparkline_rendersSvgWhenEnoughValues', () => {
    const { container } = render(() => <Sparkline values={[1, 2, 3, 4]} />);
    const svg = container.querySelector('svg');
    expect(svg).toBeTruthy();
    expect(container.querySelector('.spark-line')).toBeTruthy();
    expect(container.querySelector('.spark-fill')).toBeTruthy();
  });

  it('Sparkline_hidesWhenInsufficientValues', () => {
    const { container } = render(() => <Sparkline values={[1]} />);
    expect(container.querySelector('svg')).toBeNull();
  });
});
