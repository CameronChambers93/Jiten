import { describe, expect, it } from 'vitest';
import { DEFAULT_CARD_TARGETS, matchPreset, miningOf, newSetup, partsOffRoles, pickTarget, type CardTargets, type NewCardTarget } from '../app/utils/ankiSetups';

// Core 2000's fields on Cameron's collection, with the Picture, SentenceAudio and Sentences fields added for Jiten.
const CORE_2000_FIELDS = [
  'Expression',
  'Optimized-Voc-Index',
  'Vocabulary-Furigana',
  'vocabularyKana',
  'vocabularyEnglish',
  'vocabularyAudio',
  'Vocabulary-Pos',
  'Caution',
  'SentenceKanji',
  'SentenceFurigana',
  'sentenceKana',
  'SentenceEnglish',
  'sentenceClozed',
  'Sentence-Audio',
  'Notes',
  'Core-Index',
  'Optimized-Sent-Index',
  'Frequency',
  'Glossary',
  'Picture',
  'SentenceAudio',
  'Sentences',
];

const target = (deckName: string, when: NewCardTarget['when'] = null): NewCardTarget => ({ deckName, modelName: 'Lapis+', tags: 'jiten', when });
const line = (deckId: number, mediaType: number, parent?: { deckId: number; mediaType: number }) => ({
  sourceDeck: { deckId, originalTitle: 'Episode', mediaType },
  sourceDeckParent: parent ? { ...parent, originalTitle: 'Series' } : undefined,
});

describe('new-card targets', () => {
  const targets: CardTargets = {
    targets: [
      target('Anime Mining', { mediaTypes: [1, 2], titles: [] }),
      target('Camp', { mediaTypes: [], titles: [{ deckId: 50, title: 'ゆるキャン△' }] }),
      target('Jiten test'),
    ],
    padStartMs: 250,
    padEndMs: 250,
  };

  it('takes the first target whose rule matches, else the last', () => {
    expect(pickTarget(targets, line(10, 1)).deckName).toBe('Anime Mining');
    expect(pickTarget(targets, line(10, 7)).deckName).toBe('Jiten test');
    expect(pickTarget(targets, line(50, 4)).deckName).toBe('Camp');
  });

  it("matches an episode by its series' title and media type", () => {
    expect(pickTarget(targets, line(11, 4, { deckId: 50, mediaType: 4 })).deckName).toBe('Camp');
    expect(pickTarget(targets, line(11, 4, { deckId: 60, mediaType: 1 })).deckName).toBe('Anime Mining');
  });

  it('ignores a rule left on the last target', () => {
    const only: CardTargets = { ...targets, targets: [target('Only', { mediaTypes: [1], titles: [] })] };
    expect(pickTarget(only, line(10, 7)).deckName).toBe('Only');
    expect(pickTarget(DEFAULT_CARD_TARGETS, line(1, 1)).tags).toBe('jiten');
  });
});

describe('mining settings per note type', () => {
  it('fills the defaults', () => {
    expect(miningOf(null)).toEqual({ existing: 'ask', parts: { audio: true, picture: true, english: true, video: true }, emptyFields: null });
    // Settings saved before clips existed have no video part, which then defaults to on
    expect(miningOf({ mining: { existing: 'auto', parts: { audio: true, picture: false, english: true }, emptyFields: { sentenceAudio: 'deny' } } })).toEqual({
      existing: 'auto',
      parts: { audio: true, picture: false, english: true, video: true },
      emptyFields: { sentenceAudio: 'deny' },
    });
  });

  it('names the roles of parts that are off', () => {
    expect(partsOffRoles({ audio: true, picture: false, english: false, video: true })).toEqual(['picture', 'sentenceEnglish']);
    expect(partsOffRoles({ audio: true, picture: true, english: true, video: false })).toEqual(['video']);
  });

  it('fills Core 2000 from its preset, so its own sentence can be imported', () => {
    expect(matchPreset('Core 2000', CORE_2000_FIELDS)?.name).toBe('Core 2000');
    const setup = newSetup('Core 2000', CORE_2000_FIELDS);
    expect(setup.fieldRoles).toMatchObject({ Expression: 'word', SentenceKanji: 'sentence', 'Sentence-Audio': 'sentenceAudio', Sentences: 'sentenceList' });
    expect(setup.fieldRoles.Picture).toBeUndefined();
    expect(setup.importSentences).toBe(true);
    expect(setup.sentenceList).toBe('script');
  });
});
