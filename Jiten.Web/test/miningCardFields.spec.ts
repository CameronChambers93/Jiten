import { describe, expect, it } from 'vitest';
import type { Definition } from '../app/types';
import { ankiSentenceFurigana, applyTargetTemplate, ankiWordFurigana, boldTarget, definitionsHtml, escapeFieldText, firstSense, guessFieldSource, kanaReading } from '../app/utils/miningCardFields';

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

  describe('with a target word', () => {
    const text = 'その部分を見る。';
    const spans = [
      { position: 2, length: 2, reading: 'ぶぶん' },
      { position: 5, length: 1, reading: 'み' },
    ];
    const target = (wordPosition: number, wordLength: number) => ({ wordPosition, wordLength });

    it('bolds a word with a reading', () => {
      expect(ankiSentenceFurigana(text, spans, target(2, 2))).toBe('その<b>部分[ぶぶん]</b>を 見[み]る。');
    });

    it('bolds a word in plain text', () => {
      expect(ankiSentenceFurigana(text, spans, target(0, 2))).toBe('<b>その</b> 部分[ぶぶん]を 見[み]る。');
    });

    it('bolds a word that starts in a span and ends in plain text', () => {
      expect(ankiSentenceFurigana(text, spans, target(5, 2))).toBe('その 部分[ぶぶん]を<b>見[み]る</b>。');
    });

    it('bolds a word that crosses plain text and spans', () => {
      expect(ankiSentenceFurigana(text, spans, target(4, 3))).toBe('その 部分[ぶぶん]<b>を 見[み]る</b>。');
    });

    it('bolds the whole span when the word covers part of it', () => {
      expect(ankiSentenceFurigana('部分を', [{ position: 0, length: 2, reading: 'ぶぶん' }], target(1, 1))).toBe('<b>部分[ぶぶん]</b>を');
    });

    it('stays unbolded with no target length', () => {
      expect(ankiSentenceFurigana(text, spans, target(2, 0))).toBe('その 部分[ぶぶん]を 見[み]る。');
    });
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

describe('escapeFieldText', () => {
  it('escapes markup but leaves quotes as they are', () => {
    expect(escapeFieldText(`You're "sure" it's <here> & now?`)).toBe(`You're "sure" it's &lt;here&gt; &amp; now?`);
  });
});

describe('applyTargetTemplate', () => {
  it('wraps the word in the template', () => {
    expect(applyTargetTemplate('その<b>部分[ぶぶん]</b>を', '<span class="cloze_body">{word}</span>')).toBe('その<span class="cloze_body">部分[ぶぶん]</span>を');
  });

  it('leaves the word bare with a bare template', () => {
    expect(applyTargetTemplate('<b>猫</b>だ', '{word}')).toBe('猫だ');
  });

  it('keeps $ in the template literal', () => {
    expect(applyTargetTemplate('<b>猫</b>', '$&{word}')).toBe('$&猫');
  });
});
