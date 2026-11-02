import { ROLE_LABELS, type EmptyFieldPolicies, type EmptyFieldPolicy, type FieldRole } from '~/utils/ankiSetups';

/** What mining does with empty fields unless the card settings choose otherwise: warn for these, deny nothing. */
export const DEFAULT_EMPTY_FIELDS: EmptyFieldPolicies = {
  sentence: 'warn',
  sentenceAudio: 'warn',
  picture: 'warn',
  sentenceEnglish: 'warn',
  sentenceList: 'warn',
};

/** The roles with one of the policies. */
export function rolesWith(policies: EmptyFieldPolicies, ...levels: EmptyFieldPolicy[]): FieldRole[] {
  return (Object.entries(policies) as [FieldRole, EmptyFieldPolicy][]).filter(([, p]) => levels.includes(p)).map(([r]) => r);
}

/** Mining refused: a field whose policy is deny would be empty. `lines` says which, and why. */
export class MineDenied extends Error {
  constructor(public readonly lines: string[]) {
    super(lines.join('\n'));
    this.name = 'MineDenied';
  }
}

/** Roles that only import reads, so mining never fills them and has no policy for them. */
export const UNWRITTEN_ROLES: FieldRole[] = ['url', 'previousLine'];

/** Why a role was left empty, when mining knows. */
export type EmptyReasons = Partial<Record<FieldRole, string>>;

/** What to say when a field is empty and nothing recorded why. */
const FALLBACK: Partial<Record<FieldRole, string>> = {
  frequency: 'Jiten has no frequency rank for it',
  definitions: 'no definitions',
  mainDefinition: 'no definitions',
  pitchPosition: 'no pitch accent data',
  pitchCategories: 'no pitch accent data',
  source: 'no source title',
  sentenceEnglish: 'no English for this line',
};

/** Names for a stored sentence's parts, when the note type has no field to name them by. */
const PART_LABELS: Partial<Record<FieldRole, string>> = { sentenceEnglish: 'English', sentenceAudio: 'Audio', picture: 'Picture' };

/** A field is empty when it has no text, picture or sound. */
export function fieldIsEmpty(html: string | undefined): boolean {
  if (!html) return true;
  if (/<img\b|\[sound:/i.test(html)) return false;
  return (
    html
      .replace(/<[^>]*>/g, '')
      .replace(/&nbsp;/g, ' ')
      .trim().length === 0
  );
}

/**
 * One line per empty field among the `warn` roles: "<field>: <reason>". `roles` limits the field check to the roles written (all
 * mapped roles when missing). A reason is also given its own line when no field shows it: for `parts` (an added
 * sentence's English, audio and picture, stored with the sentence rather than in the note's fields), or for every
 * role when the note's fields couldn't be read (`fields` null).
 */
export function emptyFieldWarnings(opts: {
  fieldRoles: Record<string, FieldRole | ''>;
  fields: Record<string, { value: string }> | null;
  warn: readonly FieldRole[];
  reasons: EmptyReasons;
  roles?: readonly FieldRole[];
  parts?: readonly FieldRole[];
}): string[] {
  const warn = new Set(opts.warn);
  const checked = opts.roles ? new Set(opts.roles) : null;
  const lines: string[] = [];
  const shown = new Set<FieldRole>();
  if (opts.fields) {
    for (const [field, role] of Object.entries(opts.fieldRoles)) {
      if (!role || !warn.has(role) || UNWRITTEN_ROLES.includes(role) || (checked && !checked.has(role))) continue;
      shown.add(role);
      if (!(field in opts.fields)) lines.push(`${field}: the note type has no such field`);
      else if (fieldIsEmpty(opts.fields[field]?.value)) lines.push(`${field}: ${opts.reasons[role] ?? FALLBACK[role] ?? 'nothing to write'}`);
    }
  }
  for (const [role, reason] of Object.entries(opts.reasons) as [FieldRole, string][]) {
    if (shown.has(role) || !warn.has(role)) continue;
    if (opts.fields && !opts.parts?.includes(role)) continue;
    lines.push(`${PART_LABELS[role] ?? ROLE_LABELS[role]}: ${reason}`);
  }
  return lines;
}

/** The reason a request failed, short enough for a toast: the status code when there is one. */
export function failureReason(what: string, e: unknown): string {
  const err = e as { statusCode?: number; response?: { status?: number }; data?: { message?: string }; message?: string } | null;
  const status = err?.statusCode ?? err?.response?.status;
  const detail = err?.data?.message ?? (status ? String(status) : (err?.message ?? ''));
  return detail ? `${what} failed (${detail})` : `${what} failed`;
}
