import assert from 'node:assert/strict';
import test from 'node:test';
import {
    hintForIndex,
    isDataFile,
    makePreview,
    splitGlobList,
    spokenHint,
    searchMatchQuality,
    utf8ByteOffsetToStringIndex,
} from '../searchCore';

test('generates short, deterministic letter hints', () => {
    assert.equal(hintForIndex(0), 'a');
    assert.equal(hintForIndex(25), 'z');
    assert.equal(hintForIndex(26), 'aa');
    assert.equal(hintForIndex(701), 'zz');
    assert.equal(hintForIndex(702), 'aaa');
    assert.equal(spokenHint('az'), 'A Z');
});

test('converts ripgrep UTF-8 byte offsets to editor string offsets', () => {
    const text = 'αβ hello';
    assert.equal(utf8ByteOffsetToStringIndex(text, 4), 2);
    assert.equal(utf8ByteOffsetToStringIndex(text, 5), 3);
});

test('builds a trimmed preview and keeps the match position', () => {
    assert.deepEqual(makePreview('    hello world\n', 10, 5, 80), {
        text: 'hello world',
        matchStart: 6,
        matchLength: 5,
    });

    const preview = makePreview(`${'x'.repeat(80)}needle${'y'.repeat(80)}`, 80, 6, 50);
    assert.ok(preview.text.length <= 50);
    assert.equal(preview.text.slice(preview.matchStart, preview.matchStart + 6), 'needle');

    assert.deepEqual(makePreview('\t\thello\tworld', 8, 5, 80), {
        text: 'hello    world',
        matchStart: 9,
        matchLength: 5,
    });
});

test('splits comma-delimited glob lists', () => {
    assert.deepEqual(splitGlobList('**/*.ts, **/*.tsx,,'), ['**/*.ts', '**/*.tsx']);
});

test('classifies only configured data extensions as data', () => {
    const data = ['json', 'csv'];
    assert.equal(isDataFile('/work/main.ts', data), false);
    assert.equal(isDataFile('/work/README.md', data), false);
    assert.equal(isDataFile('/work/dump.JSON', data), true);
    assert.equal(isDataFile('/work/Makefile', data), false);
});

test('scores stronger textual matches above weaker ones', () => {
    const exactLine = searchMatchQuality('needle', 0, 6, 'needle', false);
    const exactCase = searchMatchQuality('call needle here', 5, 6, 'needle', false);
    const caseFolded = searchMatchQuality('call Needle here', 5, 6, 'needle', false);
    assert.ok(exactLine > exactCase);
    assert.ok(exactCase > caseFolded);
});
