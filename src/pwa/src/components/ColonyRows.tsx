import Chip from '@mui/material/Chip';
import List from '@mui/material/List';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemText from '@mui/material/ListItemText';
import Paper from '@mui/material/Paper';
import type { Colony } from '../api/colonies';
import { statusLabels } from '../format';
import { hrefFor } from '../routing/routes';

/**
 * Colonies as rows linking to each one's history: active ones first, each group in the order given. `detail` adds
 * to a row's second line, such as the yard it is in.
 */
export function ColonyRows({
  label,
  colonies,
  detail,
}: {
  readonly label: string;
  readonly colonies: readonly Colony[];
  readonly detail?: (colony: Colony) => string;
}) {
  const ordered = [
    ...colonies.filter((colony) => colony.status === 'active'),
    ...colonies.filter((colony) => colony.status !== 'active'),
  ];
  return (
    <Paper>
      <List disablePadding aria-label={label}>
        {ordered.map((colony) => (
          <li key={colony.id}>
            <ListItemButton component="a" href={hrefFor({ name: 'colony', id: colony.id })} divider>
              <ListItemText
                primary={colony.tagId}
                secondary={[detail?.(colony), `Hive ${colony.hiveNumber}`, colony.hiveType]
                  .filter((part) => part !== undefined)
                  .join(' · ')}
              />
              <Chip
                label={statusLabels[colony.status]}
                color={colony.status === 'active' ? 'success' : 'default'}
                size="small"
              />
            </ListItemButton>
          </li>
        ))}
      </List>
    </Paper>
  );
}
