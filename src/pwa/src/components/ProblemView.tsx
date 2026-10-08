import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Button from '@mui/material/Button';
import { ApiError, type ApiFailure } from '../api/client';

interface Explanation {
  readonly title: string;
  readonly message: string;
  readonly retry: boolean;
}

function explain(failure: ApiFailure | null): Explanation {
  if (failure === null) {
    return { title: 'Something went wrong', message: 'The app hit a problem. Try again.', retry: true };
  }
  switch (failure.kind) {
    case 'unavailable':
      // The API's 503 when it has no sign-in set up (so no account), or a colony that changed mid-write.
      return { title: 'The server is not ready', message: failure.message, retry: true };
    case 'not-found':
      return { title: 'Not found', message: failure.message, retry: false };
    case 'unreachable':
      return {
        title: 'Cannot reach the server',
        message: 'Check the phone has signal or Wi-Fi, then try again.',
        retry: true,
      };
    case 'validation':
      return { title: 'Something went wrong', message: failure.message, retry: false };
    case 'unexpected':
      return { title: 'Something went wrong', message: 'The server could not do that. Try again.', retry: true };
    default: {
      const unreachable: never = failure;
      return unreachable;
    }
  }
}

/** Says why a request failed, in words for the family, with a retry when one can help. */
export function ProblemView({ error, onRetry }: { readonly error: unknown; readonly onRetry?: () => void }) {
  const { title, message, retry } = explain(error instanceof ApiError ? error.failure : null);
  return (
    <Alert
      severity="error"
      action={
        retry && onRetry ? (
          <Button color="inherit" onClick={onRetry} sx={{ whiteSpace: 'nowrap' }}>
            Try again
          </Button>
        ) : undefined
      }
    >
      <AlertTitle component="h2">{title}</AlertTitle>
      {message}
    </Alert>
  );
}
