export interface Preview {
    text: string;
    matchStart: number;
    matchLength: number;
}

export function hintForIndex(index: number): string {
    if (!Number.isInteger(index) || index < 0) {
        throw new RangeError('Hint index must be a non-negative integer.');
    }

    // Spreadsheet-style letters: a..z, aa..az, ba..zz, aaa...
    let value = index + 1;
    let hint = '';
    while (value > 0) {
        value -= 1;
        hint = String.fromCharCode(97 + (value % 26)) + hint;
        value = Math.floor(value / 26);
    }
    return hint;
}

export function spokenHint(hint: string): string {
    return hint.toUpperCase().split('').join(' ');
}

export function utf8ByteOffsetToStringIndex(text: string, byteOffset: number): number {
    return Buffer.from(text, 'utf8').subarray(0, Math.max(0, byteOffset)).toString('utf8').length;
}

export function makePreview(
    rawLine: string,
    rawMatchStart: number,
    rawMatchLength: number,
    maxLength: number
): Preview {
    const source = rawLine.replace(/[\r\n]+$/u, '');
    const expandTabs = (value: string): string => value.replace(/\t/gu, '    ');
    const oneLine = expandTabs(source);
    const expandedMatchStart = expandTabs(source.slice(0, rawMatchStart)).length;
    const expandedMatchEnd = expandTabs(source.slice(0, rawMatchStart + rawMatchLength)).length;
    const leading = oneLine.length - oneLine.trimStart().length;
    const trimmed = oneLine.trim();
    let matchStart = Math.max(0, expandedMatchStart - leading);
    const matchLength = Math.max(1, expandedMatchEnd - expandedMatchStart);

    if (trimmed.length <= maxLength) {
        return { text: trimmed, matchStart: Math.min(matchStart, trimmed.length), matchLength };
    }

    const roomBefore = Math.max(8, Math.floor((maxLength - matchLength) / 2));
    let sliceStart = Math.max(0, matchStart - roomBefore);
    let sliceEnd = Math.min(trimmed.length, sliceStart + maxLength);
    if (sliceEnd - sliceStart < maxLength) {
        sliceStart = Math.max(0, sliceEnd - maxLength);
    }

    const prefix = sliceStart > 0 ? '…' : '';
    const suffix = sliceEnd < trimmed.length ? '…' : '';
    const bodyBudget = maxLength - prefix.length - suffix.length;
    sliceEnd = Math.min(trimmed.length, sliceStart + bodyBudget);
    matchStart = Math.max(0, matchStart - sliceStart + prefix.length);

    return {
        text: `${prefix}${trimmed.slice(sliceStart, sliceEnd)}${suffix}`,
        matchStart,
        matchLength: Math.min(matchLength, Math.max(1, maxLength - matchStart)),
    };
}

export function splitGlobList(value: string): string[] {
    return value
        .split(',')
        .map(item => item.trim())
        .filter(Boolean);
}

export function isDataFile(filePath: string, dataExtensions: readonly string[]): boolean {
    const basename = filePath.replace(/\\/gu, '/').split('/').pop()?.toLowerCase() ?? '';
    const dot = basename.lastIndexOf('.');
    const extension = dot >= 0 ? basename.slice(dot + 1) : '';
    const normalizedData = new Set(dataExtensions.map(item => item.replace(/^\./u, '').toLowerCase()));
    return normalizedData.has(extension);
}

export function searchMatchQuality(
    preview: string,
    matchStart: number,
    matchLength: number,
    query: string,
    regex: boolean
): number {
    const matched = preview.slice(matchStart, matchStart + matchLength);
    const trimmed = preview.trim();
    let score = 0;

    // These signals describe the textual match only. File type is deliberately
    // excluded so it can be applied strictly as a tie-breaker afterward.
    if (!regex && matched === query) {
        score += 8;
    }
    if (matchStart === 0 || /^\s*$/u.test(preview.slice(0, matchStart))) {
        score += 4;
    }
    const before = matchStart > 0 ? preview[matchStart - 1] : '';
    const after = preview[matchStart + matchLength] ?? '';
    if (!/[\p{L}\p{N}_]/u.test(before) && !/[\p{L}\p{N}_]/u.test(after)) {
        score += 2;
    }
    if (trimmed.length === matched.length) {
        score += 16;
    }
    // Prefer focused lines over lines with a large amount of unrelated text.
    score += Math.max(0, 3 - Math.floor(Math.max(0, trimmed.length - matchLength) / 40));
    return score;
}
