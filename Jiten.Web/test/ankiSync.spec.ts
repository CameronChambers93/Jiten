import { describe, expect, it } from 'vitest';
import { allowedActions, fieldFor, matchPreset, newSetup, PRESETS, setupQuery, suggestRoles, withRoles, type AnkiNoteTypeSetup } from '../app/utils/ankiSetups';
import { mainFieldValues, noteWriteFields, type MainFields } from '../app/utils/ankiNoteWrite';
import { findSlots, handMadeSlots, insertRotation, installedVersion, removeRotation, SCRIPT_VERSION } from '../app/utils/ankiRotationScript';
import { cardMeetsState, cardStrength, pickKnownCards, type AnkiCardInfo } from '../app/utils/ankiCardImport';

const LAPIS_PLUS_FIELDS = [
  'Expression',
  'ExpressionFurigana',
  'ExpressionReading',
  'ExpressionAudio',
  'SelectionText',
  'MainDefinition',
  'DefinitionPicture',
  'Sentence',
  'SentenceFurigana',
  'SentenceAudio',
  'Picture',
  'Glossary',
  'Hint',
  'IsWordAndSentenceCard',
  'IsClickCard',
  'IsSentenceCard',
  'PitchPosition',
  'PitchCategories',
  'Frequency',
  'FreqSort',
  'MiscInfo',
  'SentenceEnglish',
  'URL',
  'GlossaryJapanese',
  'PreviousLine',
  'Sentences',
];
const LAPIS_FIELDS = LAPIS_PLUS_FIELDS.slice(0, 22);
const JPMN_FIELDS = [
  'Key',
  'Word',
  'WordReading',
  'PrimaryDefinition',
  'Sentence',
  'SentenceReading',
  'Picture',
  'WordAudio',
  'SentenceAudio',
  'PAPositions',
  'WordReadingHiragana',
  'FrequencySort',
];

describe('presets', () => {
  it('picks the most specific preset whose fields all exist', () => {
    expect(matchPreset('Lapis+', LAPIS_PLUS_FIELDS)?.name).toBe('Lapis+');
    expect(matchPreset('Lapis', LAPIS_FIELDS)?.name).toBe('Lapis');
    expect(matchPreset('JP Mining Note', JPMN_FIELDS)?.name).toBe('JP Mining Note');
    expect(
      matchPreset(
        'Lapis (Jiten)',
        LAPIS_FIELDS.filter((f) => f !== 'SentenceEnglish')
      )?.name
    ).toBe('Kiku');
    expect(matchPreset('Core 2000', ['Expression', 'SentenceKanji'])).toBeNull();
  });

  it('guesses roles without a preset, each role once', () => {
    const { preset, roles } = suggestRoles('Japanese sentences', ['Word', 'Expression', 'Sentence', 'Sentences']);
    expect(preset).toBeNull();
    expect(roles).toEqual({ Word: 'word', Sentence: 'sentence', Sentences: 'sentenceList' });
  });

  it('turns on the actions a new setup can have', () => {
    const lapis = newSetup('Lapis+', LAPIS_PLUS_FIELDS);
    expect(lapis).toMatchObject({ preset: 'Lapis+', importSentences: true, takeFlags: true, foldDuplicates: true, sentenceList: 'script', knownWords: null });
    const course = newSetup('Simple Model', ['japanese_kana', 'english']);
    expect(course).toMatchObject({ importSentences: false, sentenceList: 'none' });
    expect(allowedActions(course.fieldRoles).knownWords).toBe(false);
  });

  it('turns off the actions new roles no longer allow', () => {
    const lapis = { ...newSetup('Lapis+', LAPIS_PLUS_FIELDS), knownWords: 'seen', updateMainFields: true } as AnkiNoteTypeSetup;
    expect(withRoles(lapis, { Expression: 'word' })).toMatchObject({
      knownWords: 'seen',
      importSentences: false,
      updateMainFields: false,
      sentenceList: 'none',
    });
    expect(withRoles(lapis, { Sentence: 'sentence' })).toMatchObject({ knownWords: null, takeFlags: true });
  });

  it('builds the search from the note type and filter', () => {
    expect(setupQuery({ modelName: 'Lapis+', filter: null })).toBe('"note:Lapis+"');
    expect(setupQuery({ modelName: 'JP "Mining"', filter: '-deck:JPMN-Examples' }, 'tag:x')).toBe('"note:JP \\"Mining\\"" (-deck:JPMN-Examples) tag:x');
  });

  it('reads the first field with a role in field order', () => {
    expect(fieldFor({ B: 'word', A: 'word' }, 'word', ['A', 'B'])).toBe('A');
    expect(fieldFor({ B: 'word' }, 'sentence')).toBeNull();
  });
});

const MAIN: MainFields = {
  minedSentenceId: 9,
  hash: 'h',
  sentence: '本を<b>読む</b>',
  sentencePlain: '本を読む & more',
  furigana: null,
  english: 'Read a book',
  audioFilename: 'line 1.ogg',
  pictureFilename: null,
  source: null,
};

describe('note writes', () => {
  const roles = PRESETS.find((p) => p.name === 'Lapis+')!.roles;

  it('maps the main sentence to every field of its roles', () => {
    expect(mainFieldValues(MAIN, roles)).toEqual({
      Sentence: '本を<b>読む</b>',
      SentenceFurigana: '本を<b>読む</b>',
      SentenceEnglish: 'Read a book',
      SentenceAudio: '[sound:line 1.ogg]',
      Picture: '',
    });
    expect(mainFieldValues({ ...MAIN, source: 'Odd Taxi 3' }, roles).MiscInfo).toBe('Odd Taxi 3');
    expect(mainFieldValues(MAIN, { Plain: 'sentencePlain' }).Plain).toBe('本を読む &amp; more');
  });

  it('writes only what changed, and reports a missing list field', () => {
    const current = { Sentences: { value: 'old' }, Sentence: { value: '本を<b>読む</b>' }, SentenceEnglish: { value: '' } };
    const { fields, missingList } = noteWriteFields({ ankiNoteId: 1, modelName: 'Lapis+', sentencesHtml: 'new', main: MAIN }, roles, current);
    expect(fields).toEqual({ Sentences: 'new', SentenceEnglish: 'Read a book' });
    expect(missingList).toBe(false);
    expect(noteWriteFields({ ankiNoteId: 1, modelName: 'X', sentencesHtml: 'new', main: null }, roles, {}).missingList).toBe(true);
  });
});

describe('rotation script', () => {
  const roles = {
    Sentence: 'sentence',
    SentenceFurigana: 'sentenceFurigana',
    SentenceAudio: 'sentenceAudio',
    Picture: 'picture',
    Sentences: 'sentenceList',
  } as const;
  const front = '<div class="s">{{furigana:SentenceFurigana}}</div>\n{{#Picture}}{{Picture}}{{/Picture}}';
  const back = '{{FrontSide}}<hr id=answer>{{Sentence}} {{SentenceAudio}}';

  it('finds the field references to wrap, not section tags', () => {
    expect(findSlots(front, roles).map((s) => [s.ref, s.slot])).toEqual([
      ['{{furigana:SentenceFurigana}}', 'sentence'],
      ['{{Picture}}', 'picture'],
    ]);
  });

  it('wraps slots so notes without a list look as before, and removes cleanly', () => {
    const inserted = insertRotation(front, roles, 'Sentences', 'front');
    expect(inserted).toContain(
      '{{^Sentences}}{{furigana:SentenceFurigana}}{{/Sentences}}{{#Sentences}}<span data-jiten="sentence" data-jiten-slot></span>{{/Sentences}}'
    );
    expect(inserted).toContain(`<!-- jiten:rotation v${SCRIPT_VERSION} -->`);
    expect(inserted).toContain('jitenRotation(false);');
    expect(installedVersion(inserted)).toBe(SCRIPT_VERSION);
    expect(removeRotation(inserted)).toBe(front);
    // Inserting again replaces the block rather than adding a second, and doesn't wrap twice.
    expect(insertRotation(inserted, roles, 'Sentences', 'front')).toBe(inserted);
  });

  it('adds the flag line and back marker on the back', () => {
    const inserted = insertRotation(back, roles, 'Sentences', 'back');
    expect(inserted).toContain('<div id="jiten-back" hidden></div><div class="jiten-sentence-meta" data-jiten="meta"></div>');
    expect(inserted).toContain('jitenRotation(true);');
    expect(removeRotation(inserted)).toBe(back);
  });

  it('keeps a template with its own slots as it is', () => {
    const lapis = '<span data-jiten="sentence">{{furigana:SentenceFurigana}}</span><div data-jiten="meta"></div>';
    expect(handMadeSlots(lapis)).toEqual(['sentence', 'meta']);
    expect(findSlots(lapis, roles)).toEqual([]);
    const inserted = insertRotation(lapis, roles, 'Sentences', 'back');
    expect(inserted.endsWith(lapis)).toBe(true);
    expect(inserted.startsWith('<!-- jiten:rotation')).toBe(true);
    expect(inserted).not.toContain('class="jiten-sentence-meta"');
  });

  it('never writes what Anki rewrites in a script', () => {
    const block = insertRotation('', roles, 'Sentences', 'back');
    expect(block).not.toMatch(/src="/);
    expect(block).not.toContain('[sound:');
    // Anki fills field references even inside scripts: the only ones are the block's own data div.
    expect(block.replace(/<script>[\s\S]*<\/script>/, '')).toContain('{{Sentences}}');
    expect(block.match(/<script>[\s\S]*<\/script>/)![0]).not.toContain('{{');
    expect(block).not.toMatch(/^\s*(const|let) /m);
  });
});

function card(over: Partial<AnkiCardInfo>): AnkiCardInfo {
  return {
    cardId: 1,
    due: 0,
    queue: 2,
    type: 2,
    interval: 30,
    factor: 2500,
    reps: 5,
    lapses: 0,
    mod: 1_800_000_000,
    fields: { Expression: { value: '読む', order: 0 }, ExpressionReading: { value: 'よむ', order: 1 } },
    modelName: 'Lapis+',
    deckName: 'Mining',
    ...over,
  };
}

describe('known words', () => {
  it('counts a card by the chosen state', () => {
    expect(cardMeetsState(card({ queue: 0, type: 0, reps: 0 }), 'seen')).toBe(false);
    expect(cardMeetsState(card({ queue: -1 }), 'seen')).toBe(false);
    expect(cardMeetsState(card({ queue: 1, type: 1, reps: 1, interval: 0 }), 'seen')).toBe(true);
    expect(cardMeetsState(card({ queue: 1, type: 1, reps: 1, interval: 0 }), 'review')).toBe(false);
    expect(cardMeetsState(card({ interval: 10 }), 'mature')).toBe(false);
    expect(cardMeetsState(card({ interval: 21 }), 'mature')).toBe(true);
    expect(cardStrength(card({ type: 2, interval: 3 }))).toBeGreaterThan(cardStrength(card({ type: 1, interval: 0 })));
  });

  const lapis = { ...newSetup('Lapis+', LAPIS_PLUS_FIELDS), knownWords: 'seen' } as AnkiNoteTypeSetup;
  const core = {
    ...newSetup('Core 2000', ['Expression', 'vocabularyKana']),
    fieldRoles: { Expression: 'word', vocabularyKana: 'wordReading' },
    knownWords: 'seen',
  } as AnkiNoteTypeSetup;

  it('keeps the strongest card of a word across note types', () => {
    const weak = card({ cardId: 1, queue: 1, type: 1, interval: 0 });
    const strong = card({
      cardId: 2,
      modelName: 'Core 2000',
      fields: { Expression: { value: '<b>読む</b>' }, vocabularyKana: { value: 'よむ' } },
      interval: 90,
    });
    const picked = pickKnownCards(
      [
        { card: weak, setup: lapis },
        { card: strong, setup: core },
      ],
      null
    );
    expect(picked).toHaveLength(1);
    expect(picked[0]).toMatchObject({ word: '読む', reading: 'よむ', modelName: 'Core 2000' });
    expect(picked[0]!.card.cardId).toBe(2);
  });

  it('sends only cards changed since the last Sync', () => {
    const since = new Date(1_800_000_000 * 1000);
    expect(pickKnownCards([{ card: card({ mod: 1_799_000_000 }), setup: lapis }], since)).toHaveLength(0);
    expect(pickKnownCards([{ card: card({ mod: 1_800_000_100 }), setup: lapis }], since)).toHaveLength(1);
  });

  it('reads the reading from ruby furigana', () => {
    const jpmn = {
      ...newSetup('JP Mining Note', JPMN_FIELDS),
      fieldRoles: { Word: 'word', WordReading: 'wordFurigana' },
      knownWords: 'seen',
    } as AnkiNoteTypeSetup;
    const c = card({ fields: { Word: { value: '怪我' }, WordReading: { value: '<ruby>怪我<rt>けが</rt></ruby>' } } });
    expect(pickKnownCards([{ card: c, setup: jpmn }], null)[0]).toMatchObject({ word: '怪我', reading: 'けが' });
  });
});
