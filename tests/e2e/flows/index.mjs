// The flows in run order. When a `required` flow stops on an error, the rest is skipped (they need its state).
import * as setup from './setup.mjs';
import * as home from './home.mjs';
import * as editor from './editor.mjs';
import * as datesEditor from './dates-editor.mjs';
import * as share from './share.mjs';
import * as vote from './vote.mjs';
import * as results from './results.mjs';
import * as edit from './edit.mjs';
import * as theme from './theme.mjs';
import * as signin from './signin.mjs';
import * as admin from './admin.mjs';
import * as mail from './mail.mjs';

// `when`: a flow that runs only in some setups (mail needs an SMTP server, scripts/npm-stack-test.sh).
export const flows = [setup, home, editor, datesEditor, share, vote, results, edit, theme, signin, admin, mail];
