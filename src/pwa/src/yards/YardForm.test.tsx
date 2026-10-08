import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { noAccount, pecanBottom, problem, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { YardForm } from './YardForm';

async function choose(user: ReturnType<typeof renderScreen>['user'], label: string, option: string) {
  await user.click(screen.getByRole('combobox', { name: label }));
  await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: option }));
}

describe('YardForm', () => {
  it('should create the yard with its landowner and open it when a new yard is saved', async () => {
    let sent: unknown;
    server.use(
      http.post('/api/yards', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({ ...pecanBottom, id: 'yard-9' }, { status: 201 });
      }),
    );
    const { user } = renderScreen(<YardForm yardId={null} />, '#/yards/new');

    await user.type(screen.getByLabelText(/^Name/), 'Pecan Bottom');
    await user.type(screen.getByLabelText(/^Latitude/), '30.1234');
    await user.type(screen.getByLabelText(/^Longitude/), '-97.5678');
    await user.type(screen.getByLabelText(/^County/), 'Example County');
    await user.type(screen.getByLabelText(/^Access notes/), 'Gate code is on the family chat');
    await user.type(screen.getByLabelText(/^Landowner contact/), 'Sample Landowner, 555-0100');
    await choose(user, 'Terms', 'Honey');
    await user.type(screen.getByLabelText(/^Nearby crops/), 'Cotton');
    await user.click(screen.getByRole('button', { name: 'Save yard' }));

    await waitFor(() => expect(window.location.hash).toBe('#/yards/yard-9'));
    expect(sent).toEqual({
      name: 'Pecan Bottom',
      location: { latitude: 30.1234, longitude: -97.5678 },
      state: 'TX',
      county: 'Example County',
      accessNotes: 'Gate code is on the family chat',
      forageNotes: '',
      landowner: { contact: 'Sample Landowner, 555-0100', terms: 'honey', nearbyCrops: 'Cotton', sprayContact: '' },
    });
  });

  it('should send an empty position as missing when the coordinates are left blank', async () => {
    let sent: { location?: unknown } = {};
    server.use(
      http.post('/api/yards', async ({ request }) => {
        sent = (await request.json()) as { location?: unknown };
        return problem(400, 'Validation: one or more fields are invalid; correct them and retry.', {
          'location.latitude': ['Validation: location.latitude is required, from -90 to 90.'],
        });
      }),
    );
    const { user } = renderScreen(<YardForm yardId={null} />, '#/yards/new');

    await user.click(screen.getByRole('button', { name: 'Save yard' }));

    await screen.findByText('location.latitude is required, from -90 to 90.');
    expect(sent.location).toEqual({ latitude: null, longitude: null });
  });

  it('should show the API message on its field when the server rejects one', async () => {
    server.use(
      http.post('/api/yards', () =>
        problem(400, 'Validation: one or more fields are invalid; correct them and retry.', {
          'landowner.contact': ['Validation: landowner.contact is required.'],
        }),
      ),
    );
    const { user } = renderScreen(<YardForm yardId={null} />, '#/yards/new');

    await user.click(screen.getByRole('button', { name: 'Save yard' }));

    expect(await screen.findByLabelText(/^Landowner contact/)).toHaveAccessibleDescription(
      'landowner.contact is required.',
    );
    expect(screen.getByRole('heading', { name: 'Check the highlighted fields' })).toBeInTheDocument();
  });

  it('should show a rule for a field it has no input for above the form when the server rejects one', async () => {
    server.use(
      http.post('/api/yards', () =>
        problem(400, 'Validation: one or more fields are invalid; correct them and retry.', {
          landowner: ['Validation: landowner is required, with contact and terms.'],
        }),
      ),
    );
    const { user } = renderScreen(<YardForm yardId={null} />, '#/yards/new');

    await user.click(screen.getByRole('button', { name: 'Save yard' }));

    expect(await screen.findByText('landowner is required, with contact and terms.')).toBeInTheDocument();
  });

  it('should explain the server is not ready and stay on the form when the server has no sign-in (so no account)', async () => {
    server.use(http.post('/api/yards', noAccount));
    const { user } = renderScreen(<YardForm yardId={null} />, '#/yards/new');

    await user.type(screen.getByLabelText(/^Name/), 'Pecan Bottom');
    await user.click(screen.getByRole('button', { name: 'Save yard' }));

    expect(await screen.findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
    expect(screen.getByLabelText(/^Name/)).toHaveValue('Pecan Bottom');
  });

  it('should fill the form with the yard when an existing yard is edited', async () => {
    stubApi({ yards: [pecanBottom] });
    renderScreen(<YardForm yardId="yard-1" />, '#/yards/yard-1/edit');

    expect(await screen.findByLabelText(/^Name/)).toHaveValue('Pecan Bottom');
    expect(screen.getByLabelText(/^Latitude/)).toHaveValue(30.1234);
    expect(screen.getByLabelText(/^Landowner contact/)).toHaveValue('Sample Landowner, 555-0100');
    expect(screen.getByRole('combobox', { name: 'Terms' })).toHaveTextContent('Honey');
  });

  it('should replace the yard and open it when an edit is saved', async () => {
    stubApi({ yards: [pecanBottom] });
    let sent: { name?: string; landowner?: { terms?: string } } = {};
    server.use(
      http.put('/api/yards/yard-1', async ({ request }) => {
        sent = (await request.json()) as typeof sent;
        return HttpResponse.json({ ...pecanBottom, name: 'Pecan Bottom North' });
      }),
    );
    const { user } = renderScreen(<YardForm yardId="yard-1" />, '#/yards/yard-1/edit');

    const name = await screen.findByLabelText(/^Name/);
    await user.clear(name);
    await user.type(name, 'Pecan Bottom North');
    await choose(user, 'Terms', 'Fee');
    await user.click(screen.getByRole('button', { name: 'Save yard' }));

    await waitFor(() => expect(window.location.hash).toBe('#/yards/yard-1'));
    expect(sent).toMatchObject({ name: 'Pecan Bottom North', landowner: { terms: 'fee' } });
  });

  it('should say the yard is not there when an unknown yard is edited', async () => {
    stubApi({ yards: [] });
    renderScreen(<YardForm yardId="yard-404" />, '#/yards/yard-404/edit');

    expect(await screen.findByRole('heading', { name: 'Not found' })).toBeInTheDocument();
  });
});
