import { describe, expect, it } from 'vitest';
import type { Definition } from '../app/types';
import { ankiSentenceFurigana, ankiWordFurigana, boldTarget, definitionsHtml, firstSense, guessFieldSource, kanaReading } from '../app/utils/miningCardFields';

describe('guessFieldSource', () => {
  it('maps Lapis and GSM field names', () => {
    expect(guessFieldSource('Expression')).toBe('word');
    expect(guessFieldSource('ExpressionFurigana')).toBe('wordFurigana');
    expect(guessFieldSource('ExpressionReading')).toBe('wordReading');
    expect(guessFieldSource('MainDefinition')).toBe('mainDefinition');
    expect(guessFieldSource('Glossary')).toBe('definitions');
    expect(guessFieldSource('Sentence')).toBe('sentence');
    expect(guessFieldSource('SentenceFurigana')).toBe('sentenceFurigana');
    expect(guessFieldSource('SentenceAudio')).toBe('sentenceAudio');
    expect(guessFieldSource('Picture')).toBe('picture');
    expect(guessFieldSource('MiscInfo')).toBe('source');
    expect(guessFieldSource('SentenceEnglish')).toBe('sentenceEnglish');
    expect(guessFieldSource('Translation')).toBe('sentenceEnglish');
    expect(guessFieldSource('PitchPosition')).toBe('pitchPosition');
    expect(guessFieldSource('PitchCategories')).toBe('pitchCategories');
  });

  it('leaves unknown fields empty', () => {
    expect(guessFieldSource('ExpressionAudio')).toBe('');
  });
});

describe('word readings', () => {
  it('turns ruby markup into kana', () => {
    expect(kanaReading('去年[きょねん]')).toBe('きょねん');
    expect(kanaReading('お茶[ちゃ]')).toBe('おちゃ');
  });

  it('turns ruby markup into Anki furigana, spacing off the kana before a kanji block', () => {
    expect(ankiWordFurigana('漢[かん]字[じ]')).toBe('漢[かん] 字[じ]');
    expect(ankiWordFurigana('お茶[ちゃ]')).toBe('お 茶[ちゃ]');
    expect(ankiWordFurigana('ひらがな')).toBe('ひらがな');
  });
});

describe('ankiSentenceFurigana', () => {
  it('adds readings to kanji spans and leaves kana spans bare', () => {
    const text = '去年の３月に終わっちゃった。';
    const spans = [
      { position: 0, length: 2, reading: 'きょねん' },
      { position: 2, length: 1, reading: 'の' },
      { position: 3, length: 2, reading: 'さんがつ' },
      { position: 6, length: 3, reading: 'おわっ' },
    ];
    expect(ankiSentenceFurigana(text, spans)).toBe('去年[きょねん]の 3月[さんがつ]に 終わっ[おわっ]ちゃった。'.replace('3', '３'));
  });

  it('escapes HTML in the text', () => {
    expect(ankiSentenceFurigana('<b>', [])).toBe('&lt;b&gt;');
  });
});

describe('boldTarget', () => {
  it('bolds the target word', () => {
    expect(boldTarget({ text: '僕たちのアニメ', wordPosition: 4, wordLength: 3 })).toBe('僕たちの<b>アニメ</b>');
  });
});

describe('definitionsHtml', () => {
  const sense = (meanings: string[], restrictedToReadingIndices?: number[]): Definition =>
    ({ index: 0, meanings, partsOfSpeech: ['n'], restrictedToReadingIndices }) as Definition;

  it('lists the senses for the reading', () => {
    expect(definitionsHtml([sense(['last year']), sense(['other'], [3])], 0)).toBe('<ol><li><i>(n)</i> last year</li></ol>');
  });

  it('takes the first applicable sense as the main definition', () => {
    expect(firstSense([sense(['other'], [3]), sense(['you', 'thou'])], 0)).toBe('you; thou');
  });
});
