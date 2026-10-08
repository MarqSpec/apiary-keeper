import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import { ApiError } from '../api/client';
import { ProblemView } from './ProblemView';

/**
 * Why a save failed, above the form. Field errors show on their fields; this shows the rest — those for a field the
 * form has no input for (`landowner`, `id`) — or the whole failure when it was not a validation one.
 */
export function FormProblem({ error, fields }: { readonly error: unknown; readonly fields: readonly string[] }) {
  if (error === null || error === undefined) return null;
  if (!(error instanceof ApiError) || error.failure.kind !== 'validation') return <ProblemView error={error} />;

  const others = Object.entries(error.failure.errors)
    .filter(([field]) => !fields.includes(field))
    .flatMap(([, messages]) => messages);
  return (
    <Alert severity="error">
      <AlertTitle component="h2">Check the highlighted fields</AlertTitle>
      {others.map((message) => (
        <div key={message}>{message}</div>
      ))}
    </Alert>
  );
}
