import { escapeFieldText } from '~/utils/miningCardFields';
import { fieldFor, fieldsFor, type FieldRole } from '~/utils/ankiSetups';

/** The word's main sentence for a note's main fields (`MainFieldsDto`). */
export interface MainFields {
  minedSentenceId: number;
  hash: string;
  sentence: string;
  sentencePlain: string;
  furigana: string | null;
  english: string | null;
  audioFilename: string | null;
  pictureFilename: string | null;
  source: string | null;
  /** Missing from main fields written before clips existed. */
  videoFilename?: string | null;
}

/** What Sync writes to a word's kept note (`NoteWriteDto`). */
export interface NoteWrite {
  ankiNoteId: number;
  modelName: string;
  sentencesHtml: string | null;
  main: MainFields | null;
}

/** File names are written into HTML attributes and sound tags, which the names can't break out of. */
function attr(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
}

/** The main sentence's value for each field holding a main-sentence role. */
export function mainFieldValues(main: MainFields, roles: Record<string, FieldRole | ''>): Record<string, string> {
  const values: Partial<Record<FieldRole, string>> = {
    sentence: main.sentence,
    sentencePlain: escapeFieldText(main.sentencePlain),
    // A sentence without readings still replaces the old sentence's furigana.
    sentenceFurigana: main.furigana ?? main.sentence,
    sentenceEnglish: main.english ?? '',
    sentenceAudio: main.audioFilename ? `[sound:${main.audioFilename}]` : '',
    picture: main.pictureFilename ? `<img src="${attr(main.pictureFilename)}">` : '',
    // A sound tag, so Anki plays it in its own player on templates without the rotation script.
    video: main.videoFilename ? `[sound:${main.videoFilename}]` : '',
  };
  // The source is only written when known, so the note keeps whatever it had (Yomitan's page title) otherwise.
  if (main.source) values.source = escapeFieldText(main.source);

  const fields: Record<string, string> = {};
  for (const [role, value] of Object.entries(values) as [FieldRole, string][]) {
    for (const field of fieldsFor(roles, role)) fields[field] = value;
  }
  return fields;
}

/**
 * The fields to update on a note, given what it holds now: the sentence list and the main fields, leaving out values
 * that are already there. `missingList` is set when the setup writes a list but the note has no such field.
 */
export function noteWriteFields(
  write: NoteWrite,
  roles: Record<string, FieldRole | ''>,
  current: Record<string, { value: string }>
): { fields: Record<string, string>; missingList: boolean } {
  const fields: Record<string, string> = {};
  let missingList = false;
  if (write.sentencesHtml != null) {
    const listField = fieldFor(roles, 'sentenceList');
    if (!listField || !(listField in current)) missingList = true;
    else fields[listField] = write.sentencesHtml;
  }
  if (write.main) Object.assign(fields, mainFieldValues(write.main, roles));

  const changed = Object.fromEntries(Object.entries(fields).filter(([name, value]) => name in current && current[name]!.value !== value));
  return { fields: changed, missingList };
}
