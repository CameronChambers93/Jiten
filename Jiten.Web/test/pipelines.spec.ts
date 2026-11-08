import { describe, expect, it } from 'vitest';
import {
  buildChoice,
  conditionMatches,
  firstStep,
  hasAnkiStep,
  newStep,
  normalizePipelines,
  pipelineTags,
  stepSummary,
  stepTarget,
  TEXT_ONLY,
  type AnkiStep,
  type MiningPipelines,
  type Pipeline,
  type PipelineContext,
} from '../app/utils/pipelines';

const anime = { deckId: 10, originalTitle: 'オッドタクシー', romajiTitle: 'Odd Taxi', mediaType: 1 };
const novel = { deckId: 20, originalTitle: '本', mediaType: 4 };
const ctx = (cardNoteType: string | null, deck = anime): PipelineContext => ({ sentence: { sourceDeck: deck } as PipelineContext['sentence'], cardNoteType });

/** The "Mine" the server builds from Cameron's settings on 2026-11-07. */
const mine: Pipeline = normalizePipelines({
  saved: false,
  defaults: { word: 'mine', mine: 'mine', deckSentences: 'mine', sentence: 'mine' },
  pipelines: [
    {
      id: 'mine',
      name: 'Mine',
      steps: [
        { kind: 'build', audio: true, picture: true, video: true, english: 'subtitlesThenAi', padStartMs: 250, padEndMs: 250 },
        { kind: 'anki', when: { cardNoteTypes: ['Core 2000'] }, hasCard: 'add', noCard: 'skip' },
        { kind: 'anki', hasCard: 'ask', noCard: 'targets', targets: [{ deckName: 'Jiten test', modelName: 'Lapis+' }] },
        { kind: 'tags', tags: 'jiten', titleTag: false, mediaTypeTag: false },
      ],
    },
  ],
} as unknown as MiningPipelines).pipelines[0]!;

describe('normalizePipelines', () => {
  it('fills options and condition lists the server leaves out', () => {
    const anki = mine.steps[1] as AnkiStep;
    expect(anki.when).toEqual({ mediaTypes: [], titles: [], cardNoteTypes: ['Core 2000'] });
    expect(anki.targets).toEqual([]);
    expect(mine.steps[0]!.when).toBeNull();
    expect((mine.steps[2] as AnkiStep).targets[0]).toEqual({ deckName: 'Jiten test', modelName: 'Lapis+', when: null });
  });
});

describe('conditionMatches', () => {
  it('needs the media type or a title, and the card note type, when given', () => {
    const when = { mediaTypes: [1], titles: [], cardNoteTypes: [] };
    expect(conditionMatches(when, ctx(null))).toBe(true);
    expect(conditionMatches(when, ctx(null, novel))).toBe(false);
    expect(conditionMatches({ mediaTypes: [], titles: [{ deckId: 20, title: '本' }], cardNoteTypes: [] }, ctx(null, novel))).toBe(true);
    expect(conditionMatches({ mediaTypes: [1], titles: [], cardNoteTypes: ['Lapis+'] }, ctx('Core 2000'))).toBe(false);
    expect(conditionMatches({ mediaTypes: [], titles: [], cardNoteTypes: ['Lapis+'] }, ctx(null))).toBe(false);
    expect(conditionMatches(null, ctx(null))).toBe(true);
  });
});

describe('firstStep', () => {
  it('runs the auto Anki step only for a Core 2000 card', () => {
    expect(firstStep(mine, 'anki', ctx('Core 2000'))?.hasCard).toBe('add');
    expect(firstStep(mine, 'anki', ctx('Lapis+'))?.hasCard).toBe('ask');
    expect(firstStep(mine, 'anki', ctx(null))?.noCard).toBe('targets');
  });

  it('finds no Anki step in Keep for Jiten', () => {
    const keep: Pipeline = { id: 'keep', name: 'Keep for Jiten', steps: [] };
    expect(firstStep(keep, 'anki', ctx(null))).toBeNull();
    expect(hasAnkiStep(keep)).toBe(false);
  });
});

describe('buildChoice', () => {
  it('turns English off with "none" and builds text only without a Build step', () => {
    expect(buildChoice(null)).toBe(TEXT_ONLY);
    const step = { ...newStep('build'), english: 'none' as const, picture: false };
    if (step.kind !== 'build') throw new Error();
    expect(buildChoice(step).parts).toEqual({ audio: true, picture: false, video: true, english: false });
  });
});

describe('stepTarget', () => {
  it('picks the first target whose rule matches, else the last, with the tags', () => {
    const step: AnkiStep = {
      kind: 'anki',
      when: null,
      hasCard: 'ask',
      noCard: 'targets',
      targets: [
        { deckName: 'Anime', modelName: 'Lapis+', when: { mediaTypes: [1], titles: [] } },
        { deckName: 'Mining', modelName: 'Lapis+', when: null },
      ],
    };
    expect(stepTarget(step, ctx(null).sentence, 'jiten')).toEqual({ ...step.targets[0], tags: 'jiten' });
    expect(stepTarget(step, ctx(null, novel).sentence, '')?.deckName).toBe('Mining');
    expect(stepTarget({ ...step, targets: [] }, ctx(null).sentence, '')).toBeNull();
  });
});

describe('pipelineTags', () => {
  it('adds every matching Tags step, the title and media type when asked, once each', () => {
    const p: Pipeline = {
      ...mine,
      steps: [
        ...mine.steps,
        { kind: 'tags', when: { mediaTypes: [1], titles: [], cardNoteTypes: [] }, tags: 'anime jiten', titleTag: true, mediaTypeTag: true },
      ],
    };
    expect(pipelineTags(p, ctx(null), () => 'Anime')).toBe('jiten anime Odd_Taxi Anime');
    expect(pipelineTags(p, ctx(null, novel), () => 'Novel')).toBe('jiten');
  });
});

describe('stepSummary', () => {
  it('says what each step does', () => {
    expect(stepSummary(mine.steps[0]!)).toBe('Audio, Screenshot, Video, English (subtitles then AI) · padding 250 / 250 ms');
    expect(stepSummary(mine.steps[2]!)).toBe('Has a card: ask · No card: Jiten test, Lapis+');
    expect(stepSummary(mine.steps[3]!)).toBe('jiten · new cards only');
  });
});
