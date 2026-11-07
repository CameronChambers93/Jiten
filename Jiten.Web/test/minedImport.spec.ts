import { describe, expect, it } from 'vitest';
import { furiganaToKana, noteFlags, noteMedia, noteText, toImportNote, type AnkiNoteInfo } from '../app/utils/minedImport';
import { PRESETS } from '../app/utils/ankiSetups';

const LAPIS_PLUS = PRESETS.find((p) => p.name === 'Lapis+')!.roles;
const JPMN = PRESETS.find((p) => p.name === 'JP Mining Note')!.roles;

function note(fields: Record<string, string>, tags: string[] = ['yomitan']): AnkiNoteInfo {
  return { noteId: 7, modelName: 'Lapis+', tags, cards: [70], fields: Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, { value: v }])) };
}

describe('noteMedia', () => {
  it('fetches GSM media', () => {
    const n = note({ SentenceAudio: '[sound:line_08m44s.ogg]', Picture: '<img alt="snapshot" src="line_08m46s.webp">' });
    expect(noteMedia(n, LAPIS_PLUS)).toEqual({ audio: 'line_08m44s.ogg', picture: 'line_08m46s.webp', fetch: ['line_08m44s.ogg', 'line_08m46s.webp'] });
  });

  it("names but doesn't fetch Jiten's media", () => {
    const n = note({ SentenceAudio: '[sound:jiten_123.mp3]', Picture: '<img src="jiten_123.jpg">' });
    expect(noteMedia(n, LAPIS_PLUS)).toEqual({ audio: 'jiten_123.mp3', picture: 'jiten_123.jpg', fetch: [] });
  });

  it('handles a note without media', () => {
    expect(noteMedia(note({ SentenceAudio: '', Picture: '' }), LAPIS_PLUS)).toEqual({ audio: null, picture: null, fetch: [] });
  });
});

describe('toImportNote', () => {
  it('maps the Lapis+ fields and attaches fetched media', () => {
    const n = note({ Expression: '読む', ExpressionReading: 'よむ', Sentence: '本を<b>読む</b>', SentenceEnglish: '', SentenceAudio: '[sound:a.ogg]' }, [
      'Game::X',
    ]);
    expect(toImportNote(n, 'Mining', { 'a.ogg': 'QUJD' }, LAPIS_PLUS)).toMatchObject({
      ankiNoteId: 7,
      deckName: 'Mining',
      tags: ['Game::X'],
      expression: '読む',
      reading: 'よむ',
      sentence: '本を<b>読む</b>',
      english: null,
      audioFilename: 'a.ogg',
      audioBase64: 'QUJD',
      pictureFilename: null,
      pictureBase64: null,
    });
  });
});

describe('toImportNote with JP Mining Note roles', () => {
  it('reads the word, its reading from the hiragana field, and the sentence furigana', () => {
    const n = note({
      Word: '読む',
      WordReading: '読[よ]む',
      WordReadingHiragana: 'よむ',
      Sentence: '本を<b>読む</b>',
      SentenceReading: '本[ほん]を<b>読[よ]む</b>',
    });
    expect(toImportNote(n, 'Mining', {}, JPMN)).toMatchObject({
      expression: '読む',
      reading: 'よむ',
      sentence: '本を<b>読む</b>',
      furigana: '本[ほん]を<b>読[よ]む</b>',
      english: null,
    });
  });

  it('falls back to the furigana field for the reading', () => {
    const n = note({ Word: '読む', WordReading: '読[よ]む', Sentence: 's' });
    expect(toImportNote(n, 'Mining', {}, { Word: 'word', WordReading: 'wordFurigana', Sentence: 'sentence' }).reading).toBe('よむ');
  });

  it("reads a Video field's clip by name, MP4 included, so Sync writes it back", () => {
    const roles = { Word: 'word', Sentence: 'sentence', Clip: 'video' } as const;
    expect(toImportNote(note({ Word: '読む', Sentence: 's', Clip: '[sound:gsm_line.mp4]' }), 'Mining', {}, roles).videoFilename).toBe('gsm_line.mp4');
    expect(toImportNote(note({ Word: '読む', Sentence: 's', Clip: '' }), 'Mining', {}, roles).videoFilename).toBeNull();
  });
});

describe('furiganaToKana', () => {
  it('reads bracket and ruby furigana', () => {
    expect(furiganaToKana('お 茶[ちゃ]')).toBe('おちゃ');
    expect(furiganaToKana('<ruby>怪我<rt>けが</rt></ruby>する')).toBe('けがする');
  });
});

describe('noteFlags', () => {
  it('reads the plain tag as a flag on the main sentence', () => {
    expect(noteFlags([{ noteId: 5, tags: ['jiten-flag'] }])).toEqual([{ ankiNoteId: 5, minedSentenceId: null, tag: 'jiten-flag' }]);
  });

  it('reads sentence ids from flag tags', () => {
    expect(noteFlags([{ noteId: 5, tags: ['yomitan', 'sentence-fix::42', 'sentence-fix::x'] }])).toEqual([
      { ankiNoteId: 5, minedSentenceId: 42, tag: 'sentence-fix::42' },
    ]);
  });
});

describe('noteText', () => {
  it('joins every field without markup or sound tags', () => {
    expect(noteText({ A: { value: '<b>コート</b>' }, B: { value: 'court[sound:x.mp3]' }, C: { value: 'I played <i>tennis</i>.' } })).toBe(
      ' コート \ncourt \nI played tennis .'
    );
  });

  it('leaves out the sentence list Jiten writes', () => {
    expect(noteText({ A: { value: 'coat' }, S: { value: '<div class="jiten-sentences" data-note="1"><div>court</div></div>' } })).toBe('coat');
  });
});
