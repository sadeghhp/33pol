/** @vitest-environment jsdom */
import { fireEvent, render, screen } from '@solidjs/testing-library';
import { describe, expect, it } from 'vitest';
import { Tabs } from './Tabs';

describe('Tabs', () => {
  it('Tabs_unmountsInactivePanel', async () => {
    render(() => (
      <Tabs
        tabs={[
          { id: 'a', label: 'A', content: () => <div data-testid="panel-a">Panel A</div> },
          { id: 'b', label: 'B', content: () => <div data-testid="panel-b">Panel B</div> },
        ]}
      />
    ));

    expect(screen.getByTestId('panel-a')).toBeTruthy();
    expect(screen.queryByTestId('panel-b')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'B' }));
    expect(screen.queryByTestId('panel-a')).toBeNull();
    expect(screen.getByTestId('panel-b')).toBeTruthy();
  });
});
