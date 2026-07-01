/**
 * Removes Markdown code-fence delimiter lines from LLM-produced Markdown, keeping the fenced content in place.
 *
 * A vision-LLM OCR transcription (and, defensively, any LLM Markdown rendered in the operator UI) sometimes
 * arrives wrapped — wholly, or only partly — in a ```` ```markdown ```` fence despite the OCR prompt forbidding
 * it (#448). `marked` then renders the fenced block (typically a table) as a literal `<pre><code>` block instead
 * of Markdown, so the table shows as raw pipes. In this channel the payload is digitized DOCUMENT text
 * (headings / tables / lists), never source code, so a triple-backtick / triple-tilde fence is always such an
 * artifact: drop the fence delimiter lines (keeping their content) before parsing — this handles a whole-output
 * fence, a partial fence, and an unmatched (never-closed) fence uniformly.
 *
 * The backend `VisionLlmOutputGuard.StripCodeFences` strips this at the source for newly extracted documents;
 * this frontend twin also rescues documents already stored with the fence baked in (`Document.Markdown` is
 * write-once, so they are never re-extracted).
 *
 * A fence delimiter is a line whose trimmed text is a run of >= 3 back-ticks or >= 3 tildes, optionally
 * followed by an info string that contains no further back-tick — so an inline `` `code` `` / ```` ```code``` ````
 * span sitting on its own line is not mistaken for a fence.
 */
export function stripMarkdownCodeFences(markdown: string): string {
  // Fast path: no fence character at all (the overwhelming majority of documents) — return untouched.
  if (!markdown || (markdown.indexOf('`') < 0 && markdown.indexOf('~') < 0)) {
    return markdown;
  }
  return markdown
    .split('\n')
    .filter(line => !isCodeFenceLine(line))
    .join('\n');
}

function isCodeFenceLine(line: string): boolean {
  const s = line.trim();
  const marker = s[0];
  if (marker !== '`' && marker !== '~') {
    return false;
  }
  let run = 0;
  while (run < s.length && s[run] === marker) {
    run++;
  }
  // >= 3 markers, and no back-tick after the run (a back-tick would make it an inline span, not a fence).
  return run >= 3 && s.indexOf('`', run) < 0;
}
