/* ============================================================================
 * RioEditor — code highlighter
 * ---------------------------------------------------------------------------
 * Shared deliberately: the editor injects this alongside the engine, and HTML
 * export inlines the same file. Keeping one implementation is what stops an
 * export from looking different to the editor it came from.
 * ========================================================================== */
(function () {
  'use strict';

  var KEYWORDS = 'function|return|const|let|var|if|else|for|while|class|new|import|export|from|' +
    'async|await|try|catch|finally|throw|public|private|protected|static|void|int|string|bool|double|' +
    'using|namespace|def|end|struct|enum|interface|switch|case|break|continue|this|null|true|false|nil|None|True|False';

  /*
   * One combined pattern, scanned in a single pass. Doing this as a chain of
   * .replace() calls is subtly wrong: the second pass matches the markup the
   * first pass just inserted (a class="tok-comment" attribute looks exactly
   * like a string literal).
   */
  var TOKEN_PATTERN = new RegExp(
    '(\\/\\/[^\\n]*|\\/\\*[\\s\\S]*?\\*\\/|(?:^|\\n)[ \\t]*#[^\\n]*)' +   // 1: comments
    '|("(?:[^"\\\\\\n]|\\\\.)*"|\'(?:[^\'\\\\\\n]|\\\\.)*\')' +            // 2: strings
    '|\\b(\\d+(?:\\.\\d+)?)\\b' +                                        // 3: numbers
    '|\\b(' + KEYWORDS + ')\\b',                                          // 4: keywords
    'g');

  function escapeHtml(text) {
    var div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
  }

  function apply(code) {
    if (!code || code.dataset.rioHighlighted === '1') return;
    var text = code.textContent;
    if (!text || text.length > 20000) return;            // don't choke on huge blocks

    var out = '';
    var last = 0;
    var match;
    TOKEN_PATTERN.lastIndex = 0;
    while ((match = TOKEN_PATTERN.exec(text)) !== null) {
      out += escapeHtml(text.slice(last, match.index));
      var cls = match[1] ? 'tok-comment'
              : match[2] ? 'tok-string'
              : match[3] ? 'tok-number'
              : 'tok-keyword';
      out += '<span class="' + cls + '">' + escapeHtml(match[0]) + '</span>';
      last = match.index + match[0].length;
      if (TOKEN_PATTERN.lastIndex === match.index) TOKEN_PATTERN.lastIndex++;  // zero-width guard
    }
    out += escapeHtml(text.slice(last));

    code.innerHTML = out;
    code.dataset.rioHighlighted = '1';
  }

  /** Highlights every code block under root, optionally skipping one subtree. */
  function applyAll(root, skip) {
    var blocks = (root || document).querySelectorAll('pre > code');
    for (var i = 0; i < blocks.length; i++) {
      if (skip && skip.contains(blocks[i])) continue;
      apply(blocks[i]);
    }
  }

  window.rioHighlight = { apply: apply, applyAll: applyAll, escapeHtml: escapeHtml };
})();
