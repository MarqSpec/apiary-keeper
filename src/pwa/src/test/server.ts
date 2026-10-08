import { setupServer } from 'msw/node';

/** The network every unit test sees: msw at `fetch`, with no handlers until a test adds its own. */
export const server = setupServer();
