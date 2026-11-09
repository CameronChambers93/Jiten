import { describe, expect, it } from 'vitest';
import { textDiff } from '~/utils/textDiff';

const side = (parts: ReturnType<typeof textDiff>, keep: 'del' | 'ins') =>
  parts
    .filter((p) => p.kind === 'same' || p.kind === keep)
    .map((p) => p.text)
    .join('');

describe('textDiff', () => {
  it('marks a replaced character', () => {
    expect(textDiff('そん時は驚いたよ　ほんとに', 'そん時は驚いたよ。ほんとに')).toEqual([
      { text: 'そん時は驚いたよ', kind: 'same' },
      { text: '。', kind: 'ins' },
      { text: '　', kind: 'del' },
      { text: 'ほんとに', kind: 'same' },
    ]);
  });

  it('keeps both texts recoverable', () => {
    const parts = textDiff('皆さん、こんばんは！です！', '皆さん、こんばんは！ケンです！');
    expect(side(parts, 'del')).toBe('皆さん、こんばんは！です！');
    expect(side(parts, 'ins')).toBe('皆さん、こんばんは！ケンです！');
    expect(parts.filter((p) => p.kind !== 'same')).toEqual([{ text: 'ケン', kind: 'ins' }]);
  });

  it('handles empty texts', () => {
    expect(textDiff('', 'はい')).toEqual([{ text: 'はい', kind: 'ins' }]);
    expect(textDiff('はい', '')).toEqual([{ text: 'はい', kind: 'del' }]);
    expect(textDiff('', '')).toEqual([]);
  });
});
