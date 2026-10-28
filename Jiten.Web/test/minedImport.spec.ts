import { describe, expect, it } from 'vitest';
import { noteMedia, toImportNote, type AnkiNoteInfo } from '../app/utils/minedImport';

function note(fields: Record<string, string>, tags: string[] = ['yomitan']): AnkiNoteInfo {
  return { noteId: 7, modelName: 'Lapis+', tags, cards: [70], fields: Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, { value: v }])) };
}

describe('noteMedia', () => {
  it('fetches GSM media', () => {
    const n = note({ SentenceAudio: '[sound:line_08m44s.ogg]', Picture: '<img alt="snapshot" src="line_08m46s.webp">' });
    expect(noteMedia(n)).toEqual({ audio: 'line_08m44s.ogg', picture: 'line_08m46s.webp', fetch: ['line_08m44s.ogg', 'line_08m46s.webp'] });
  });

  it("names but doesn't fetch Jiten's media", () => {
    const n = note({ SentenceAudio: '[sound:jiten_123.mp3]', Picture: '<img src="jiten_123.jpg">' });
    expect(noteMedia(n)).toEqual({ audio: 'jiten_123.mp3', picture: 'jiten_123.jpg', fetch: [] });
  });

  it('handles a note without media', () => {
    expect(noteMedia(note({ SentenceAudio: '', Picture: '' }))).toEqual({ audio: null, picture: null, fetch: [] });
  });
});

describe('toImportNote', () => {
  it('maps the Lapis+ fields and attaches fetched media', () => {
    const n = note({ Expression: '読む', ExpressionReading: 'よむ', Sentence: '本を<b>読む</b>', SentenceEnglish: '', SentenceAudio: '[sound:a.ogg]' }, [
      'Game::X',
    ]);
    expect(toImportNote(n, 'Mining', { 'a.ogg': 'QUJD' })).toMatchObject({
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
