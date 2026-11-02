import { describe, expect, it } from 'vitest';
import { mainFieldValues } from '../app/utils/ankiNoteWrite';
import { applyTargetTemplate, boldTarget } from '../app/utils/miningCardFields';
import { DEFAULT_EMPTY_FIELDS, emptyFieldWarnings, failureReason, fieldIsEmpty, rolesWith } from '../app/utils/miningWarnings';

const DEFAULT_WARN = rolesWith(DEFAULT_EMPTY_FIELDS, 'warn', 'deny');

const roles = {
  Expression: 'word',
  Sentence: 'sentence',
  SentenceAudio: 'sentenceAudio',
  Picture: 'picture',
  SentenceEnglish: 'sentenceEnglish',
  Sentences: 'sentenceList',
  Frequency: 'frequency',
} as const;

const filled = {
  Expression: { value: '運命' },
  Sentence: { value: '<b>運命</b>を感じた' },
  SentenceAudio: { value: '[sound:jiten_1.mp3]' },
  Picture: { value: '<img src="jiten_1.jpg">' },
  SentenceEnglish: { value: 'I felt it was destiny.' },
  Sentences: { value: '<div class="jiten-sentences">運命</div>' },
  Frequency: { value: '' },
};

describe('fieldIsEmpty', () => {
  it('counts pictures and sounds as content', () => {
    expect(fieldIsEmpty('<img src="a.jpg">')).toBe(false);
    expect(fieldIsEmpty('[sound:a.mp3]')).toBe(false);
    expect(fieldIsEmpty(' <div>&nbsp;</div> ')).toBe(true);
    expect(fieldIsEmpty(undefined)).toBe(true);
  });
});

describe('emptyFieldWarnings', () => {
  it('names each empty field that warns, with its reason', () => {
    const fields = { ...filled, SentenceEnglish: { value: '' }, Picture: { value: '' } };
    const lines = emptyFieldWarnings({
      fieldRoles: roles,
      fields,
      warn: DEFAULT_WARN,
      reasons: { sentenceEnglish: 'Translation failed (503)', picture: 'this title has no linked video' },
    });
    expect(lines).toEqual(['Picture: this title has no linked video', 'SentenceEnglish: Translation failed (503)']);
  });

  it('leaves out roles that do not warn, and falls back to a reason', () => {
    expect(emptyFieldWarnings({ fieldRoles: roles, fields: filled, warn: DEFAULT_WARN, reasons: {} })).toEqual([]);
    expect(emptyFieldWarnings({ fieldRoles: roles, fields: filled, warn: ['frequency'], reasons: {} })).toEqual([
      'Frequency: Jiten has no frequency rank for it',
    ]);
  });

  it('gives stored parts their own line when no written field shows them', () => {
    const lines = emptyFieldWarnings({
      fieldRoles: { Expression: 'word', SentenceAudio: 'sentenceAudio', Sentences: 'sentenceList' },
      // The note's own audio field is empty, but it wasn't written, so it says nothing about the added sentence.
      fields: { Expression: { value: '運命' }, SentenceAudio: { value: '' }, Sentences: { value: 'x' } },
      warn: DEFAULT_WARN,
      reasons: { sentenceEnglish: 'Translation failed (503)', frequency: 'ignored' },
      roles: ['sentenceList'],
      parts: ['sentenceEnglish', 'sentenceAudio', 'picture'],
    });
    expect(lines).toEqual(['English: Translation failed (503)']);
  });

  it('gives every reason when the note could not be read', () => {
    const lines = emptyFieldWarnings({ fieldRoles: roles, fields: null, warn: DEFAULT_WARN, reasons: { picture: 'this title has no linked video' } });
    expect(lines).toEqual(['Picture: this title has no linked video']);
  });

  it('reports a mapped field the note type lacks', () => {
    const { Picture: _, ...fields } = filled;
    expect(emptyFieldWarnings({ fieldRoles: roles, fields, warn: ['picture'], reasons: {} })).toEqual(['Picture: the note type has no such field']);
  });
});

describe('rolesWith', () => {
  it('picks the roles with the policies asked for', () => {
    expect(rolesWith({ sentence: 'warn', picture: 'deny', source: 'warn' }, 'deny')).toEqual(['picture']);
    expect(rolesWith({ sentence: 'warn', picture: 'deny' }, 'warn', 'deny')).toEqual(['sentence', 'picture']);
  });
});

describe('failureReason', () => {
  it('prefers the server message, then the status', () => {
    expect(failureReason('Translation', { statusCode: 503 })).toBe('Translation failed (503)');
    expect(failureReason('Translation', { data: { message: 'quota' }, statusCode: 429 })).toBe('Translation failed (quota)');
    expect(failureReason('Translation', null)).toBe('Translation failed');
  });
});

describe('new note sentence fields', () => {
  it('apply the target template once, to the bold sentence the store keeps', () => {
    const stored = boldTarget({ text: '運命を感じた', wordPosition: 0, wordLength: 2 });
    expect(stored).toBe('<b>運命</b>を感じた');
    const fields = mainFieldValues(
      {
        minedSentenceId: 0,
        hash: '',
        sentence: applyTargetTemplate(stored, '<span class="cloze_body">{word}</span>'),
        sentencePlain: '運命を感じた',
        furigana: null,
        english: null,
        audioFilename: 'jiten_1.mp3',
        pictureFilename: null,
        source: null,
      },
      { Sentence: 'sentence', SentenceFurigana: 'sentenceFurigana', SentenceAudio: 'sentenceAudio', MiscInfo: 'source' }
    );
    expect(fields).toEqual({
      Sentence: '<span class="cloze_body">運命</span>を感じた',
      SentenceFurigana: '<span class="cloze_body">運命</span>を感じた',
      SentenceAudio: '[sound:jiten_1.mp3]',
    });
  });
});
