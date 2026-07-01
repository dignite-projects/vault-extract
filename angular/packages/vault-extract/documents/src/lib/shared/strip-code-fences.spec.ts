import { describe, expect, it } from 'vitest';
import { marked } from 'marked';

import { stripMarkdownCodeFences } from './strip-code-fences';

// #448: a vision-LLM OCR transcription sometimes wraps its Markdown — wholly or partly — in a ```markdown code
// fence despite the prompt forbidding it, so marked renders the fenced table as literal <pre><code> (raw pipes)
// instead of a GFM table. stripMarkdownCodeFences removes the fence delimiters before parsing.
describe('stripMarkdownCodeFences', () => {
  const render = (md: string) => marked.parse(md, { gfm: true, async: false }) as string;
  const rendersTable = (md: string) => /<table/i.test(render(md));

  it('leaves plain Markdown untouched (no fence character)', () => {
    const md = '# 普通預金通帳\n\n| 年月日 | 摘要 |\n| --- | --- |\n| 7-1-31 | 繰越 |';
    expect(stripMarkdownCodeFences(md)).toBe(md);
  });

  it('returns empty / falsy input unchanged', () => {
    expect(stripMarkdownCodeFences('')).toBe('');
  });

  it('unwraps a partial ```markdown fence so the table renders (the #448 bankbook case)', () => {
    // The header sits outside the fence; the model fenced only the table + footnotes below it.
    const ocr =
      '普通預金通帳\n\n' +
      '```markdown\n' +
      '| 年月日 | 摘要 | 差引残高 |\n' +
      '|---|---|---|\n' +
      '| 7-1-31 | 繰越 | ¥4,181,159 |\n' +
      '| 7-2-10 | カード支払 | ¥4,254,365 |\n' +
      '```\n\n' +
      '※証券類のご入金の場合は…';

    // Before: the fenced block renders as a code block, NOT a table.
    expect(rendersTable(ocr)).toBe(false);
    // After stripping: a real GFM table.
    const stripped = stripMarkdownCodeFences(ocr);
    expect(stripped).not.toContain('```');
    expect(rendersTable(stripped)).toBe(true);
    // Content on both sides of the fence is preserved.
    expect(stripped).toContain('普通預金通帳');
    expect(stripped).toContain('※証券類のご入金の場合は…');
  });

  it('unwraps a whole-output fence', () => {
    const stripped = stripMarkdownCodeFences('```markdown\n# Title\n\nBody.\n```');
    expect(stripped).not.toContain('```');
    expect(stripped).toContain('# Title');
    expect(stripped).toContain('Body.');
  });

  it('unwraps an unmatched (never-closed) opening fence', () => {
    const stripped = stripMarkdownCodeFences('```\n| a | b |\n| --- | --- |\n| c | d |');
    expect(stripped).not.toContain('```');
    expect(rendersTable(stripped)).toBe(true);
  });

  it('strips tilde fences and a language info string', () => {
    expect(stripMarkdownCodeFences('~~~\ncontent\n~~~')).not.toContain('~~~');
    expect(stripMarkdownCodeFences('```md\ncontent\n```')).not.toContain('`');
  });

  it('does not strip a line carrying an inline code span', () => {
    const md = 'Run `dotnet build` then `nx serve`.';
    expect(stripMarkdownCodeFences(md)).toBe(md);
  });

  it('does not strip a ```code``` inline span on its own line', () => {
    const md = '```code```';
    expect(stripMarkdownCodeFences(md)).toBe(md);
  });
});
