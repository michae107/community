/**
 * Parse a unified diff string and return 0-indexed line numbers
 * of added/modified lines in the new file.
 */
export function parseChangedLines(diff: string): number[] {
    if (!diff) {
        return [];
    }

    const lines = diff.split('\n');
    const changed: number[] = [];
    let currentLine = 0;

    for (const line of lines) {
        const hunkMatch = line.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
        if (hunkMatch) {
            currentLine = parseInt(hunkMatch[1], 10);
            continue;
        }

        if (line.startsWith('+++') || line.startsWith('---')) {
            continue;
        }

        if (line.startsWith('+')) {
            changed.push(currentLine - 1); // 0-indexed
            currentLine++;
        } else if (line.startsWith('-')) {
            // deleted line, don't increment currentLine
        } else if (!line.startsWith('\\')) {
            // context line
            currentLine++;
        }
    }

    return changed;
}
