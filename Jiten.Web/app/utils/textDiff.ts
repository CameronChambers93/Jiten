/** A run of characters in a diff: in both texts, only the old one, or only the new one. */
export interface DiffPart {
  text: string;
  kind: 'same' | 'del' | 'ins';
}

/**
 * Character diff of two short texts (sentences), by longest common subsequence: the old text is the `same` and `del`
 * parts in order, the new one the `same` and `ins` parts. Neighbouring parts of one kind are merged.
 */
export function textDiff(before: string, after: string): DiffPart[] {
  const a = [...before];
  const b = [...after];
  // lcs[i][j]: length of the common subsequence of a[i..] and b[j..]
  const lcs = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0));
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--) lcs[i]![j] = a[i] === b[j] ? lcs[i + 1]![j + 1]! + 1 : Math.max(lcs[i + 1]![j]!, lcs[i]![j + 1]!);

  const parts: DiffPart[] = [];
  const push = (text: string, kind: DiffPart['kind']) => {
    const last = parts[parts.length - 1];
    if (last?.kind === kind) last.text += text;
    else parts.push({ text, kind });
  };
  let i = 0;
  let j = 0;
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) {
      push(a[i]!, 'same');
      i++;
      j++;
    } else if (j < b.length && (i === a.length || lcs[i]![j + 1]! >= lcs[i + 1]![j]!)) push(b[j++]!, 'ins');
    else push(a[i++]!, 'del');
  }
  return parts;
}
