import { describe, expect, it } from 'vitest';
import { noteContext, noteSource, plainText, skipReason, type BackfillNote } from '../app/utils/ankiEnglishBackfill';

function note(fields: Record<string, string>, tags: string[] = ['yomitan']): BackfillNote {
  return { noteId: 1, tags, fields: Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, { value: v }])) };
}

describe('plainText', () => {
  it('drops tags and decodes entities', () => {
    expect(plainText('<div>まあ けちな商人でございます&nbsp;</div>今日<b>読破</b><br>!')).toBe('まあ けちな商人でございます 今日読破 !');
  });
});

describe('noteSource', () => {
  it('names NHK News Web Easy', () => {
    expect(noteSource(note({ URL: 'https://news.web.nhk/news/easy/ne2026/ne123.html' }))).toBe('an NHK News Web Easy article');
  });

  it('reads the link out of an anchor', () => {
    expect(noteSource(note({ URL: '<a href="https://be.5ch.io/test">x</a>' }))).toBe('the website be.5ch.io');
  });

  it('treats a texthooker page as a game', () => {
    expect(noteSource(note({ URL: 'http://localhost:55000/texthooker' }))).toBe('a video game');
    expect(noteSource(note({ MiscInfo: 'Texthooker' }))).toBe('a video game');
  });

  it("uses a Jiten card's title", () => {
    expect(noteSource(note({ MiscInfo: 'おそ松さん 第2期 Episode 15' }, ['jiten']))).toBe('the show "おそ松さん 第2期 Episode 15"');
  });

  it('falls back for an empty link', () => {
    expect(noteSource(note({ URL: '<a href=""></a>' }))).toBe('an unknown source');
    expect(noteSource(note({ URL: 'file:///C:/Users/x/a.html' }))).toBe('a document');
  });
});

describe('skipReason', () => {
  it('skips Lapis samples and fragments', () => {
    expect(skipReason(note({ Sentence: '履歴書は<b>楷書</b>で書いてください', MiscInfo: 'Sample Card #16' }), 'Sentence')).toBe('Lapis sample card');
    expect(skipReason(note({ Sentence: '<span class="cloze_body">読め</span>' }), 'Sentence')).toBe('fragment');
    expect(skipReason(note({ Sentence: '' }), 'Sentence')).toBe('no sentence');
  });

  it('keeps a real sentence', () => {
    expect(skipReason(note({ Sentence: '9日、1頭の熊を<span class="cloze_body">捕まえました</span>。' }), 'Sentence')).toBeNull();
  });
});

describe('noteContext', () => {
  it('uses PreviousLine when filled', () => {
    expect(noteContext(note({ PreviousLine: '<b>前</b>の行' }))).toEqual(['前の行']);
    expect(noteContext(note({ PreviousLine: '' }))).toEqual([]);
  });
});
