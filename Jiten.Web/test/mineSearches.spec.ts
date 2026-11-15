import { describe, expect, it } from 'vitest';
import { emptyTally, nameProblem, pipelineCanAsk, runPick, runSteps, sameQuery, searchQuery, tallyLine } from '../app/utils/mineSearches';

describe('runPick', () => {
  it('sends a word this run already carded to its card, whatever the step says (one note per word)', () => {
    for (const hasCard of ['add', 'ask', 'new', 'skip'] as const) expect(runPick(hasCard, true, 'new', true)).toBe('existing');
  });

  it("follows the step's hasCard for a word the run hasn't touched", () => {
    expect(runPick('add', true, 'new', false)).toBe('existing');
    expect(runPick('new', true, 'existing', false)).toBe('new');
    expect(runPick('skip', true, 'existing', false)).toBe('skip');
  });

  it('answers ask with the choice made before the run', () => {
    expect(runPick('ask', true, 'existing', false)).toBe('existing');
    expect(runPick('ask', true, 'new', false)).toBe('new');
    expect(runPick('ask', true, 'skip', false)).toBe('skip');
  });

  it('adds to the card when ask has no target for a new note', () => {
    expect(runPick('ask', false, 'new', false)).toBe('existing');
  });
});

describe('runSteps', () => {
  it("walks each word's sentences in order", () => {
    const steps = [...runSteps([{ word: 'a', sentences: [1, 2] }, { word: 'b', sentences: [3] }])];
    expect(steps.map((s) => `${s.word}${s.sentence}:${s.index}`)).toEqual(['a1:0', 'a2:1', 'b3:2']);
  });
});

describe('saved search queries', () => {
  it('keeps only the Mine page keys with values', () => {
    expect(searchQuery({ titles: '1,2', page: '3', deck: '', sort: 'length' })).toEqual({ titles: '1,2', sort: 'length' });
  });

  it('compares queries ignoring key order and unknown keys', () => {
    expect(sameQuery({ titles: '1', deck: 'Core' }, { deck: 'Core', titles: '1', page: '2' })).toBe(true);
    expect(sameQuery({ titles: '1' }, { titles: '1', lmin: '16' })).toBe(false);
  });

  it('refuses blank, long and taken names, ignoring case', () => {
    const searches = [{ id: 'a', name: 'Core', query: {} }];
    expect(nameProblem(' ', searches)).toMatch(/name/);
    expect(nameProblem('x'.repeat(41), searches)).toMatch(/40/);
    expect(nameProblem('core', searches)).toMatch(/Another/);
    expect(nameProblem('core', searches, 'a')).toBeNull();
    expect(nameProblem('Bunny Drop', searches)).toBeNull();
  });
});

describe('pipelineCanAsk', () => {
  it('is true only with an Anki step that asks', () => {
    expect(pipelineCanAsk([{ kind: 'build' }, { kind: 'anki', hasCard: 'ask' }])).toBe(true);
    expect(pipelineCanAsk([{ kind: 'anki', hasCard: 'add' }])).toBe(false);
  });
});

describe('tallyLine', () => {
  it('lists the non-zero counts', () => {
    const t = emptyTally();
    t.created = 2;
    t.added = 1;
    t.failed.push({ word: 'x', wordId: 1, readingIndex: 0, reason: 'r' });
    expect(tallyLine(t)).toBe('2 new notes · 1 added to a card · 1 failed');
    expect(tallyLine(emptyTally())).toBe('Nothing to do');
  });
});
