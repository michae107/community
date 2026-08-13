import { spawn } from 'node:child_process';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { isDataFile, makePreview, searchMatchQuality, utf8ByteOffsetToStringIndex } from './searchCore';

const { rgPath } = require('@vscode/ripgrep') as { rgPath: string };

export interface SearchMatch {
    uri: vscode.Uri;
    line: number;
    column: number;
    matchLength: number;
    preview: string;
    previewMatchStart: number;
    previewMatchLength: number;
}

export interface RipgrepSearchOptions {
    query: string;
    roots: readonly vscode.WorkspaceFolder[];
    matchCase: boolean;
    wholeWord: boolean;
    regex: boolean;
    includeGlobs: readonly string[];
    excludeGlobs: readonly string[];
    includeHidden: boolean;
    useIgnoreFiles: boolean;
    maxResults: number;
    previewLength: number;
    dataFileExtensions: readonly string[];
}

interface RipgrepSubmatch {
    start: number;
    end: number;
}

interface RipgrepMatchEvent {
    type: 'match';
    data: {
        path: { text?: string };
        lines: { text?: string };
        line_number: number;
        submatches: RipgrepSubmatch[];
    };
}

function isMatchEvent(value: unknown): value is RipgrepMatchEvent {
    if (!value || typeof value !== 'object') {
        return false;
    }
    const candidate = value as Partial<RipgrepMatchEvent>;
    return candidate.type === 'match' && !!candidate.data;
}

function buildArguments(options: RipgrepSearchOptions): string[] {
    const args = [
        '--json',
        '--color=never',
        '--no-messages',
        '--max-columns=2000',
        '--max-columns-preview',
    ];

    if (!options.regex) {
        args.push('--fixed-strings');
    }
    if (!options.matchCase) {
        args.push('--ignore-case');
    }
    if (options.wholeWord) {
        args.push('--word-regexp');
    }
    if (options.includeHidden) {
        args.push('--hidden');
    }
    if (!options.useIgnoreFiles) {
        args.push('--no-ignore');
    }
    for (const glob of options.includeGlobs) {
        args.push('--glob', glob);
    }
    for (const glob of options.excludeGlobs) {
        args.push('--glob', `!${glob}`);
    }

    // `--` protects queries beginning with a hyphen. Searching each root from
    // its own cwd keeps multi-root paths unambiguous and honors local ignores.
    args.push('--', options.query, '.');
    return args;
}

async function searchRoot(
    root: vscode.WorkspaceFolder,
    options: RipgrepSearchOptions,
    remaining: number,
    token: vscode.CancellationToken
): Promise<{ matches: SearchMatch[]; limitReached: boolean }> {
    if (remaining <= 0) {
        return { matches: [], limitReached: true };
    }

    return new Promise((resolve, reject) => {
        const child = spawn(rgPath, buildArguments(options), {
            cwd: root.uri.fsPath,
            windowsHide: true,
            stdio: ['ignore', 'pipe', 'pipe'],
        });
        const matches: SearchMatch[] = [];
        let stdoutBuffer = '';
        let stderr = '';
        let limitReached = false;
        let cancelled = token.isCancellationRequested;

        const cancellation = token.onCancellationRequested(() => {
            cancelled = true;
            child.kill();
        });

        const consumeLine = (line: string): void => {
            if (!line || matches.length >= remaining) {
                return;
            }

            let parsed: unknown;
            try {
                parsed = JSON.parse(line);
            } catch {
                return;
            }
            if (!isMatchEvent(parsed)) {
                return;
            }

            const relativePath = parsed.data.path.text;
            const rawLine = parsed.data.lines.text;
            const submatch = parsed.data.submatches[0];
            if (!relativePath || rawLine === undefined || !submatch) {
                return;
            }

            const editorStart = utf8ByteOffsetToStringIndex(rawLine, submatch.start);
            const editorEnd = utf8ByteOffsetToStringIndex(rawLine, submatch.end);
            const preview = makePreview(
                rawLine,
                editorStart,
                Math.max(1, editorEnd - editorStart),
                options.previewLength
            );
            matches.push({
                uri: vscode.Uri.file(path.resolve(root.uri.fsPath, relativePath)),
                line: Math.max(0, parsed.data.line_number - 1),
                column: editorStart,
                matchLength: Math.max(1, editorEnd - editorStart),
                preview: preview.text,
                previewMatchStart: preview.matchStart,
                previewMatchLength: preview.matchLength,
            });

            if (matches.length >= remaining) {
                limitReached = true;
                child.kill();
            }
        };

        child.stdout.setEncoding('utf8');
        child.stdout.on('data', (chunk: string) => {
            stdoutBuffer += chunk;
            let newline = stdoutBuffer.indexOf('\n');
            while (newline >= 0) {
                consumeLine(stdoutBuffer.slice(0, newline));
                stdoutBuffer = stdoutBuffer.slice(newline + 1);
                newline = stdoutBuffer.indexOf('\n');
            }
        });
        child.stderr.setEncoding('utf8');
        child.stderr.on('data', (chunk: string) => {
            stderr += chunk;
        });
        child.on('error', error => {
            cancellation.dispose();
            reject(error);
        });
        child.on('close', code => {
            cancellation.dispose();
            if (stdoutBuffer) {
                consumeLine(stdoutBuffer);
            }
            if (cancelled) {
                reject(new vscode.CancellationError());
                return;
            }
            // ripgrep uses 1 for a successful search with no matches. A killed
            // process is also expected when the global result limit is reached.
            if (code !== 0 && code !== 1 && !limitReached) {
                reject(new Error(stderr.trim() || `ripgrep exited with code ${code}`));
                return;
            }
            resolve({ matches, limitReached });
        });
    });
}

export async function searchWorkspace(
    options: RipgrepSearchOptions,
    token: vscode.CancellationToken
): Promise<{ matches: SearchMatch[]; limitReached: boolean }> {
    const matches: SearchMatch[] = [];
    let limitReached = false;

    for (const root of options.roots) {
        const result = await searchRoot(
            root,
            options,
            options.maxResults - matches.length,
            token
        );
        matches.push(...result.matches);
        if (result.limitReached || matches.length >= options.maxResults) {
            limitReached = true;
            break;
        }
    }

    matches.sort((left, right) => {
        const leftQuality = searchMatchQuality(
            left.preview,
            left.previewMatchStart,
            left.previewMatchLength,
            options.query,
            options.regex
        );
        const rightQuality = searchMatchQuality(
            right.preview,
            right.previewMatchStart,
            right.previewMatchLength,
            options.query,
            options.regex
        );
        const byQuality = rightQuality - leftQuality;
        const byDataType = Number(isDataFile(left.uri.fsPath, options.dataFileExtensions))
            - Number(isDataFile(right.uri.fsPath, options.dataFileExtensions));
        const byPath = left.uri.fsPath.localeCompare(right.uri.fsPath);
        return byQuality || byDataType || byPath || left.line - right.line || left.column - right.column;
    });
    return { matches, limitReached };
}
