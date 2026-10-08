import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ChangeNoticeProvider, ChangeNoticeView } from './ChangeNotice';
import { useChangeNotice } from './useChangeNotice';

function Show({ colonyId, warnings }: { colonyId: string; warnings?: Record<string, string[]> }) {
  const notice = useChangeNotice();
  return (
    <button type="button" onClick={() => notice.show(colonyId, warnings)}>
      Show
    </button>
  );
}

function renderNotice(ui: React.ReactNode) {
  return render(<ChangeNoticeProvider>{ui}</ChangeNoticeProvider>);
}

describe('ChangeNoticeView', () => {
  it("should list every field's warnings without their category when a change carries some", () => {
    renderNotice(
      <>
        <Show
          colonyId="colony-1"
          warnings={{ introducedOn: ['Warning: first.', 'Warning: second.'], yardId: ['Warning: third.'] }}
        />
        <ChangeNoticeView colonyId="colony-1" />
      </>,
    );

    act(() => screen.getByRole('button', { name: 'Show' }).click());

    const notice = screen.getByRole('alert');
    expect(notice).toHaveTextContent('Recorded, with a warning');
    expect(notice).toHaveTextContent('first.second.third.');
  });

  it("should show nothing on another colony's screen when the warning is for a different colony", () => {
    renderNotice(
      <>
        <Show colonyId="colony-1" warnings={{ introducedOn: ['Warning: first.'] }} />
        <ChangeNoticeView colonyId="colony-2" />
      </>,
    );

    act(() => screen.getByRole('button', { name: 'Show' }).click());

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('should clear an earlier notice when a later change carries no warnings', () => {
    renderNotice(
      <>
        <Show colonyId="colony-1" warnings={{ introducedOn: ['Warning: first.'] }} />
        <Show colonyId="colony-1" />
        <ChangeNoticeView colonyId="colony-1" />
      </>,
    );
    const [withWarning, without] = screen.getAllByRole('button', { name: 'Show' });

    act(() => withWarning?.click());
    expect(screen.getByRole('alert')).toBeInTheDocument();
    act(() => without?.click());

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('should fail loudly when it is rendered outside the provider', () => {
    expect(() => render(<ChangeNoticeView colonyId="colony-1" />)).toThrow(/ChangeNoticeProvider/);
  });
});
