/* ============================================================================
 * RioEditor — WYSIWYG Markdown engine
 * ---------------------------------------------------------------------------
 * Runs inside the WebView. Owns the contenteditable surface, the caret, and the
 * *instant* half of the Markdown pipeline; the C# host owns the canonical half
 * (Markdig for Markdown->HTML, HtmlAgilityPack for HTML->Markdown).
 *
 * Why the split: re-rendering the whole document on every keystroke is what
 * makes naive WYSIWYG editors lose the caret and feel laggy. Here:
 *   - inline rules (**bold**, `code`, [x](y) ...) are applied in the DOM the
 *     moment the closing delimiter is typed — zero round trips, caret intact;
 *   - block rules (# , > , - , 1. , ```) fire on space/Enter, locally;
 *   - when the caret *leaves* a block, that one block is round-tripped through
 *     the host so it ends up in canonical Markdig form.
 * Full-document renders only happen on open/setMarkdown.
 * ========================================================================== */
(function () {
  'use strict';

  var editor = document.getElementById('editor');
  var ZWSP = '\u200B';   // parks the caret outside freshly created inline nodes

  var pendingHostRequests = {};   // requestId -> resolve
  var suppressChangeEvents = false;
  var changeTimer = null;
  var activeBlock = null;
  var blockRenderQueue = {};      // requestId -> block element awaiting host HTML

  /* ======================================================================
   * 1. Host transport
   * The four shims below cover WebView2 (Windows), WKWebView (macOS/iOS),
   * WebKitGTK (Linux) and the Avalonia WASM head respectively. The first one
   * that exists wins; if none do, the engine still works standalone.
   * ==================================================================== */

  function postToHost(message) {
    var json = typeof message === 'string' ? message : JSON.stringify(message);
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(json);              // WebView2
        return true;
      }
      if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.rio) {
        window.webkit.messageHandlers.rio.postMessage(json);  // WKWebView
        return true;
      }
      if (window.rioAndroid && typeof window.rioAndroid.postMessage === 'function') {
        window.rioAndroid.postMessage(json);                  // Android @JavascriptInterface
        return true;
      }
      if (typeof window.rioHostChannel === 'function') {
        window.rioHostChannel(json);                          // Avalonia WASM / WebKitGTK shim
        return true;
      }
      if (window.external && typeof window.external.SendMessage === 'function') {
        window.external.SendMessage(json);                    // legacy host
        return true;
      }
      if (window.parent && window.parent !== window) {
        window.parent.postMessage({ rio: json }, '*');        // iframe hosting (WASM)
        return true;
      }
    } catch (e) {
      console.warn('[rio] host post failed', e);
    }
    return false;
  }

  function hostRequest(request, payload) {
    return new Promise(function (resolve) {
      var requestId = 'r' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
      pendingHostRequests[requestId] = resolve;
      var message = { type: 'hostRequest', request: request, requestId: requestId };
      for (var key in payload) {
        if (Object.prototype.hasOwnProperty.call(payload, key)) message[key] = payload[key];
      }
      if (!postToHost(message)) {
        delete pendingHostRequests[requestId];
        resolve('');   // standalone mode: nothing to talk to
      }
      // Never leave a promise dangling if the host goes away mid-flight.
      setTimeout(function () {
        if (pendingHostRequests[requestId]) {
          delete pendingHostRequests[requestId];
          resolve('');
        }
      }, 10000);
    });
  }

  /* ======================================================================
   * 2. Caret preservation
   * Two strategies, used for different jobs:
   *   - path+offset: exact, for local DOM surgery (inline rules);
   *   - text offset within a block: survives a full re-render of that block,
   *     which is what the incremental host round trip does to it.
   * ==================================================================== */

  function nodePath(node) {
    var path = [];
    while (node && node !== editor) {
      var parent = node.parentNode;
      if (!parent) return path;
      path.unshift(Array.prototype.indexOf.call(parent.childNodes, node));
      node = parent;
    }
    return path;
  }

  function nodeFromPath(path) {
    var node = editor;
    for (var i = 0; i < path.length; i++) {
      var next = node.childNodes[path[i]];
      if (!next) return node;
      node = next;
    }
    return node;
  }

  function saveCaret() {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return null;
    var range = selection.getRangeAt(0);
    if (!editor.contains(range.startContainer)) return null;
    return {
      startPath: nodePath(range.startContainer),
      startOffset: range.startOffset,
      endPath: nodePath(range.endContainer),
      endOffset: range.endOffset
    };
  }

  function restoreCaret(saved) {
    if (!saved) return;
    try {
      var startNode = nodeFromPath(saved.startPath);
      var endNode = nodeFromPath(saved.endPath);
      var range = document.createRange();
      range.setStart(startNode, clampOffset(startNode, saved.startOffset));
      range.setEnd(endNode, clampOffset(endNode, saved.endOffset));
      applyRange(range);
    } catch (e) {
      /* A DOM shape change can invalidate the path; losing the caret beats throwing. */
    }
  }

  function clampOffset(node, offset) {
    var max = node.nodeType === Node.TEXT_NODE ? node.data.length : node.childNodes.length;
    return Math.max(0, Math.min(offset, max));
  }

  function applyRange(range) {
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  /** Caret position expressed as a character offset inside a block element. */
  function textOffsetIn(block) {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !block) return 0;
    var range = selection.getRangeAt(0);
    if (!block.contains(range.startContainer)) return 0;
    var probe = document.createRange();
    probe.selectNodeContents(block);
    probe.setEnd(range.startContainer, range.startOffset);
    return probe.toString().length;
  }

  /** Inverse of textOffsetIn: walks text nodes until the offset is consumed. */
  function setTextOffsetIn(block, offset) {
    if (!block) return;
    var walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT, null);
    var remaining = offset;
    var node;
    var last = null;
    while ((node = walker.nextNode())) {
      last = node;
      if (remaining <= node.data.length) {
        var range = document.createRange();
        range.setStart(node, remaining);
        range.collapse(true);
        applyRange(range);
        return;
      }
      remaining -= node.data.length;
    }
    // Offset ran past the end (the render shortened the block): sit at the end.
    var tail = document.createRange();
    if (last) {
      tail.setStart(last, last.data.length);
    } else {
      tail.selectNodeContents(block);
    }
    tail.collapse(false);
    applyRange(tail);
  }

  function placeCaretAtEnd(node) {
    var range = document.createRange();
    range.selectNodeContents(node);
    range.collapse(false);
    applyRange(range);
  }

  /* ======================================================================
   * 3. Block helpers
   * ==================================================================== */

  var BLOCK_TAGS = 'P,H1,H2,H3,H4,H5,H6,BLOCKQUOTE,PRE,LI,TABLE,DIV,HR,UL,OL';

  function currentBlock() {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return null;
    var node = selection.getRangeAt(0).startContainer;
    if (node === editor) node = editor.firstChild;
    while (node && node !== editor) {
      if (node.nodeType === Node.ELEMENT_NODE &&
          node.parentNode === editor &&
          BLOCK_TAGS.indexOf(node.tagName) >= 0) {
        return node;
      }
      node = node.parentNode;
    }
    return null;
  }

  /** Nearest editable leaf block (an <li> counts, its parent <ul> does not). */
  function currentLeaf() {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return null;
    var node = selection.getRangeAt(0).startContainer;
    if (node.nodeType === Node.TEXT_NODE) node = node.parentNode;
    while (node && node !== editor) {
      if (node.nodeType === Node.ELEMENT_NODE && BLOCK_TAGS.indexOf(node.tagName) >= 0) return node;
      node = node.parentNode;
    }
    return null;
  }

  function ensureNotEmpty() {
    if (editor.childNodes.length === 0) {
      var p = document.createElement('p');
      p.appendChild(document.createElement('br'));
      editor.appendChild(p);
    }
  }

  function replaceBlockWith(block, element) {
    block.parentNode.replaceChild(element, block);
    return element;
  }

  /** Rebuilds a block as another tag, carrying its children (and the caret) over. */
  function retagBlock(block, tagName) {
    var replacement = document.createElement(tagName);
    while (block.firstChild) replacement.appendChild(block.firstChild);
    if (!replacement.firstChild) replacement.appendChild(document.createElement('br'));
    return replaceBlockWith(block, replacement);
  }

  /* ======================================================================
   * 4. Inline Markdown rules — applied the instant the closing token is typed
   * ==================================================================== */

  var INLINE_RULES = [
    // Order matters: ** before *, ~~ before ~.
    { pattern: /\*\*([^*\n]+?)\*\*$/,        build: function (m) { return el('strong', m[1]); } },
    { pattern: /__([^_\n]+?)__$/,            build: function (m) { return el('strong', m[1]); } },
    { pattern: /(?:^|[^*])\*([^*\n]+?)\*$/,  build: function (m) { return el('em', m[1]); }, keepPrefix: true },
    { pattern: /(?:^|[^_\w])_([^_\n]+?)_$/,  build: function (m) { return el('em', m[1]); }, keepPrefix: true },
    { pattern: /~~([^~\n]+?)~~$/,            build: function (m) { return el('del', m[1]); } },
    { pattern: /`([^`\n]+?)`$/,              build: function (m) { return el('code', m[1]); } },
    { pattern: /!\[([^\]\n]*)\]\(([^)\s]+)\)$/, build: function (m) {
        var img = document.createElement('img');
        img.setAttribute('src', m[2]);
        img.setAttribute('alt', m[1]);
        return img;
      } },
    { pattern: /\[([^\]\n]+)\]\(([^)\s]+)\)$/, build: function (m) {
        var a = el('a', m[1]);
        a.setAttribute('href', m[2]);
        return a;
      } }
  ];

  function el(tag, text) {
    var node = document.createElement(tag);
    node.textContent = text;
    return node;
  }

  function applyInlineRules() {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !selection.isCollapsed) return false;
    var range = selection.getRangeAt(0);
    var node = range.startContainer;
    if (node.nodeType !== Node.TEXT_NODE) return false;

    // Never transform inside a code block — that text is meant to stay literal.
    if (closest(node, 'PRE') || closest(node, 'CODE')) return false;

    var before = node.data.slice(0, range.startOffset);

    for (var i = 0; i < INLINE_RULES.length; i++) {
      var rule = INLINE_RULES[i];
      var match = rule.pattern.exec(before);
      if (!match) continue;

      var matched = match[0];
      // Rules with keepPrefix match one leading character for disambiguation
      // (e.g. "*x*" must not fire inside "**x**"); that character stays put.
      var consumed = rule.keepPrefix && matched.length > 0 && matched[0] !== '*' && matched[0] !== '_'
        ? matched.length - 1
        : matched.length;
      if (rule.keepPrefix && (matched[0] === '*' || matched[0] === '_')) consumed = matched.length;

      var start = before.length - consumed;
      if (start < 0) continue;

      var element = rule.build(match);
      var target = document.createRange();
      target.setStart(node, start);
      target.setEnd(node, range.startOffset);
      target.deleteContents();
      target.insertNode(element);

      // A zero-width space parks the caret *outside* the new element so the next
      // character typed is not swallowed by the bold/italic/code run.
      var tail = document.createTextNode(ZWSP);
      element.parentNode.insertBefore(tail, element.nextSibling);
      var caret = document.createRange();
      caret.setStart(tail, 1);
      caret.collapse(true);
      applyRange(caret);
      return true;
    }
    return false;
  }

  function closest(node, tagName) {
    while (node && node !== editor) {
      if (node.nodeType === Node.ELEMENT_NODE && node.tagName === tagName) return node;
      node = node.parentNode;
    }
    return null;
  }

  /* ======================================================================
   * 5. Block Markdown rules — fire on space / Enter
   * ==================================================================== */

  var BLOCK_RULES = [
    { pattern: /^(#{1,6})\s$/, apply: function (block, m) { return retagBlock(block, 'h' + m[1].length); } },
    { pattern: /^>\s$/,        apply: function (block) { return wrapInQuote(block); } },
    { pattern: /^[-*+]\s\[([ xX])\]\s$/, apply: function (block, m) {
        return toList(block, 'ul', m[1].toLowerCase() === 'x' ? 'checked' : 'unchecked');
      } },
    { pattern: /^[-*+]\s$/,    apply: function (block) { return toList(block, 'ul'); } },
    { pattern: /^\d+[.)]\s$/,  apply: function (block) { return toList(block, 'ol'); } }
  ];

  function applyBlockRules() {
    var block = currentLeaf();
    if (!block || block.tagName === 'PRE') return false;

    var text = block.textContent.replace(new RegExp(ZWSP, 'g'), '');
    for (var i = 0; i < BLOCK_RULES.length; i++) {
      var match = BLOCK_RULES[i].pattern.exec(text);
      if (!match) continue;
      // Clear the marker text, then restructure.
      block.textContent = '';
      var produced = BLOCK_RULES[i].apply(block, match);
      if (produced) placeCaretAtEnd(produced);
      scheduleChange();
      return true;
    }
    return false;
  }

  function wrapInQuote(block) {
    var quote = document.createElement('blockquote');
    var paragraph = document.createElement('p');
    paragraph.appendChild(document.createElement('br'));
    quote.appendChild(paragraph);
    replaceBlockWith(block, quote);
    return paragraph;
  }

  function toList(block, listTag, taskState) {
    var list = document.createElement(listTag);
    var item = document.createElement('li');

    if (taskState) {
      var checkbox = document.createElement('input');
      checkbox.setAttribute('type', 'checkbox');
      if (taskState === 'checked') checkbox.setAttribute('checked', 'checked');
      item.appendChild(checkbox);
      item.appendChild(document.createTextNode(' '));
      item.className = 'task-list-item';
    }

    list.appendChild(item);
    replaceBlockWith(block, list);
    return item;
  }

  /* ======================================================================
   * 6. Enter / key handling
   * ==================================================================== */

  function onKeyDown(event) {
    // --- code fence: ``` + Enter opens a code block -----------------------
    if (event.key === 'Enter' && !event.shiftKey) {
      var leaf = currentLeaf();
      if (leaf && leaf.tagName !== 'PRE') {
        var fence = /^```(\w*)$/.exec(leaf.textContent.trim());
        if (fence) {
          event.preventDefault();
          insertCodeBlock(fence[1], leaf);
          return;
        }
        var rule = /^(-{3,}|\*{3,}|_{3,})$/.exec(leaf.textContent.trim());
        if (rule) {
          event.preventDefault();
          insertHorizontalRule(leaf);
          return;
        }
      }

      // --- inside a code block Enter inserts a newline, never a new block ---
      var pre = closest(window.getSelection().anchorNode, 'PRE');
      if (pre) {
        event.preventDefault();
        insertTextAtCaret('\n');
        scheduleChange();
        return;
      }

      // --- Enter on an empty list item exits the list -----------------------
      var li = closest(window.getSelection().anchorNode, 'LI');
      if (li && li.textContent.replace(new RegExp(ZWSP, 'g'), '').trim() === '') {
        event.preventDefault();
        exitList(li);
        return;
      }

      // Ordinary Enter: let the browser split the block, then canonicalise the
      // block we just left via the host round trip.
      var leaving = currentLeaf();
      setTimeout(function () { requestBlockRender(leaving); }, 0);
      return;
    }

    // --- Tab indents / outdents list items --------------------------------
    if (event.key === 'Tab') {
      var item = closest(window.getSelection().anchorNode, 'LI');
      if (item) {
        event.preventDefault();
        event.shiftKey ? outdentItem(item) : indentItem(item);
        scheduleChange();
        return;
      }
      if (closest(window.getSelection().anchorNode, 'PRE')) {
        event.preventDefault();
        insertTextAtCaret('  ');
        return;
      }
    }

    // --- keyboard shortcuts ------------------------------------------------
    var mod = event.metaKey || event.ctrlKey;
    if (!mod) return;
    var key = event.key.toLowerCase();
    if (key === 'b')      { event.preventDefault(); commands.bold(); }
    else if (key === 'i') { event.preventDefault(); commands.italic(); }
    else if (key === 'e') { event.preventDefault(); commands.inlineCode(); }
    else if (key === 'k') { event.preventDefault(); postToHost({ type: 'requestLink' }); }
    else if (mod && event.shiftKey && key === 'c') { event.preventDefault(); commands.codeBlock(''); }
    else if (key >= '1' && key <= '6') { event.preventDefault(); commands.heading(Number(key)); }
    else if (key === '0') { event.preventDefault(); commands.heading(0); }
  }

  function insertTextAtCaret(text) {
    var selection = window.getSelection();
    if (!selection.rangeCount) return;
    var range = selection.getRangeAt(0);
    range.deleteContents();
    var node = document.createTextNode(text);
    range.insertNode(node);
    range.setStartAfter(node);
    range.collapse(true);
    applyRange(range);
  }

  function insertCodeBlock(language, block) {
    var pre = document.createElement('pre');
    var code = document.createElement('code');
    if (language) code.className = 'language-' + language;
    code.appendChild(document.createTextNode(''));
    pre.appendChild(code);
    if (block) {
      replaceBlockWith(block, pre);
    } else {
      insertBlockAtCaret(pre);
    }
    placeCaretAtEnd(code);
    scheduleChange();
  }

  function insertHorizontalRule(block) {
    var hr = document.createElement('hr');
    var after = document.createElement('p');
    after.appendChild(document.createElement('br'));
    if (block) {
      replaceBlockWith(block, hr);
    } else {
      insertBlockAtCaret(hr);
    }
    hr.parentNode.insertBefore(after, hr.nextSibling);
    placeCaretAtEnd(after);
    scheduleChange();
  }

  function insertBlockAtCaret(element) {
    var block = currentBlock();
    if (block) {
      block.parentNode.insertBefore(element, block.nextSibling);
    } else {
      editor.appendChild(element);
    }
  }

  function exitList(item) {
    var list = item.parentNode;
    var paragraph = document.createElement('p');
    paragraph.appendChild(document.createElement('br'));
    if (list.parentNode === editor) {
      list.parentNode.insertBefore(paragraph, list.nextSibling);
    } else {
      editor.appendChild(paragraph);
    }
    item.parentNode.removeChild(item);
    if (list.children.length === 0 && list.parentNode) list.parentNode.removeChild(list);
    placeCaretAtEnd(paragraph);
    scheduleChange();
  }

  function indentItem(item) {
    var previous = item.previousElementSibling;
    if (!previous) return;                       // first item cannot indent
    var offset = textOffsetIn(item);
    var nested = previous.querySelector(':scope > ul, :scope > ol');
    if (!nested) {
      nested = document.createElement(item.parentNode.tagName);
      previous.appendChild(nested);
    }
    nested.appendChild(item);
    setTextOffsetIn(item, offset);
  }

  function outdentItem(item) {
    var list = item.parentNode;
    var parentItem = list.parentNode;
    if (!parentItem || parentItem.tagName !== 'LI') return;
    var offset = textOffsetIn(item);
    parentItem.parentNode.insertBefore(item, parentItem.nextSibling);
    if (list.children.length === 0) list.parentNode.removeChild(list);
    setTextOffsetIn(item, offset);
  }

  /* ======================================================================
   * 7. Incremental host round trip
   * ==================================================================== */

  function requestBlockRender(block) {
    if (!block || !block.parentNode || !editor.contains(block)) return;
    if (block.tagName === 'PRE') return;               // code blocks are literal
    var html = block.outerHTML.replace(new RegExp(ZWSP, 'g'), '');
    if (!html || !/[*_`~#>\[\]|-]/.test(block.textContent || '')) {
      // Nothing that looks like Markdown syntax: skip the round trip entirely.
      return;
    }

    var requestId = 'b' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    blockRenderQueue[requestId] = block;
    postToHost({ type: 'renderBlock', requestId: requestId, html: html });
  }

  function onBlockRendered(requestId, html) {
    var block = blockRenderQueue[requestId];
    delete blockRenderQueue[requestId];
    if (!block || !editor.contains(block) || !html) return;

    var caretHere = block.contains(window.getSelection().anchorNode);
    var offset = caretHere ? textOffsetIn(block) : -1;

    var holder = document.createElement('div');
    holder.innerHTML = html;
    var replacement = holder.firstElementChild;
    if (!replacement) return;

    // Skip the swap when nothing actually changed — avoids gratuitous DOM churn
    // (and the flicker that comes with it) on every Enter press.
    if (replacement.outerHTML === block.outerHTML) return;

    suppressChangeEvents = true;
    var inserted = [];
    var reference = block;
    while (holder.firstChild) {
      var node = holder.firstChild;
      block.parentNode.insertBefore(node, reference);
      inserted.push(node);
    }
    block.parentNode.removeChild(block);
    suppressChangeEvents = false;

    if (offset >= 0 && inserted.length > 0) {
      setTextOffsetIn(inserted[inserted.length - 1], offset);
    }
    decorate();
  }

  /* ======================================================================
   * 8. Commands (toolbar + shortcuts)
   * ==================================================================== */

  var commands = {
    bold: function () { execInline('bold'); },
    italic: function () { execInline('italic'); },
    strikethrough: function () { execInline('strikeThrough'); },

    inlineCode: function () { toggleWrap('CODE'); },

    heading: function (level) {
      var block = currentLeaf();
      if (!block) return;
      var offset = textOffsetIn(block);
      var target = level >= 1 && level <= 6 ? 'h' + level : 'p';
      if (block.tagName.toLowerCase() === target) target = 'p';   // toggle back off
      var produced = retagBlock(block, target);
      setTextOffsetIn(produced, offset);
      scheduleChange();
    },

    link: function (url, text) {
      var selection = window.getSelection();
      if (!url) return;
      if (selection && !selection.isCollapsed) {
        var anchor = document.createElement('a');
        anchor.setAttribute('href', url);
        var range = selection.getRangeAt(0);
        anchor.appendChild(range.extractContents());
        range.insertNode(anchor);
        placeCaretAfter(anchor);
      } else {
        var link = document.createElement('a');
        link.setAttribute('href', url);
        link.textContent = text || url;
        insertInlineAtCaret(link);
      }
      scheduleChange();
    },

    codeBlock: function (language) {
      var block = currentLeaf();
      if (block && block.tagName === 'PRE') {           // toggle back to a paragraph
        var text = block.textContent;
        var paragraph = document.createElement('p');
        paragraph.textContent = text;
        replaceBlockWith(block, paragraph);
        placeCaretAtEnd(paragraph);
        scheduleChange();
        return;
      }
      var existing = block ? block.textContent : '';
      insertCodeBlock(language || '', block);
      if (existing) {
        var code = editor.querySelector('pre > code:empty');
        if (code) code.textContent = existing;
      }
    },

    quote: function () {
      var block = currentLeaf();
      if (!block) return;
      var quote = closest(block, 'BLOCKQUOTE');
      if (quote) {                                       // unquote
        while (quote.firstChild) quote.parentNode.insertBefore(quote.firstChild, quote);
        quote.parentNode.removeChild(quote);
      } else {
        var wrapper = document.createElement('blockquote');
        block.parentNode.insertBefore(wrapper, block);
        wrapper.appendChild(block);
        placeCaretAtEnd(block);
      }
      scheduleChange();
    },

    bulletList: function () { convertToList('ul', false); },
    orderedList: function () { convertToList('ol', false); },
    taskList: function () { convertToList('ul', true); },

    horizontalRule: function () { insertHorizontalRule(null); },

    table: function (rows, columns) {
      rows = Math.max(1, rows || 3);
      columns = Math.max(1, columns || 3);
      var table = document.createElement('table');
      var head = document.createElement('thead');
      var headRow = document.createElement('tr');
      for (var c = 0; c < columns; c++) {
        var th = document.createElement('th');
        th.textContent = 'Column ' + (c + 1);
        headRow.appendChild(th);
      }
      head.appendChild(headRow);
      table.appendChild(head);

      var body = document.createElement('tbody');
      for (var r = 0; r < rows; r++) {
        var tr = document.createElement('tr');
        for (var cc = 0; cc < columns; cc++) {
          var td = document.createElement('td');
          td.appendChild(document.createElement('br'));
          tr.appendChild(td);
        }
        body.appendChild(tr);
      }
      table.appendChild(body);
      insertBlockAtCaret(table);

      var after = document.createElement('p');
      after.appendChild(document.createElement('br'));
      table.parentNode.insertBefore(after, table.nextSibling);
      var firstCell = table.querySelector('td');
      if (firstCell) placeCaretAtEnd(firstCell);
      scheduleChange();
    }
  };

  function convertToList(listTag, task) {
    var block = currentLeaf();
    if (!block) return;
    var item = closest(block, 'LI');
    if (item) {                                          // already a list: unwrap it
      var list = item.parentNode;
      var paragraph = document.createElement('p');
      paragraph.innerHTML = item.innerHTML || '<br>';
      list.parentNode.insertBefore(paragraph, list);
      item.parentNode.removeChild(item);
      if (list.children.length === 0) list.parentNode.removeChild(list);
      placeCaretAtEnd(paragraph);
      scheduleChange();
      return;
    }

    var newList = document.createElement(listTag);
    var newItem = document.createElement('li');
    if (task) {
      var checkbox = document.createElement('input');
      checkbox.setAttribute('type', 'checkbox');
      newItem.className = 'task-list-item';
      newItem.appendChild(checkbox);
      newItem.appendChild(document.createTextNode(' '));
    }
    while (block.firstChild) newItem.appendChild(block.firstChild);
    if (!newItem.textContent) newItem.appendChild(document.createElement('br'));
    newList.appendChild(newItem);
    replaceBlockWith(block, newList);
    placeCaretAtEnd(newItem);
    scheduleChange();
  }

  /**
   * document.execCommand is deprecated but remains the only API that gets
   * selection-spanning inline toggles right across WebView2 / WKWebView /
   * WebKitGTK. styleWithCSS(false) keeps the output as <b>/<i> tags rather than
   * inline styles, which the HTML -> Markdown pass understands.
   */
  function execInline(command) {
    try { document.execCommand('styleWithCSS', false, 'false'); } catch (e) { /* ignore */ }
    document.execCommand(command, false, null);
    scheduleChange();
    reportSelection();
  }

  function toggleWrap(tagName) {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return;
    var existing = closest(selection.anchorNode, tagName);
    if (existing) {
      while (existing.firstChild) existing.parentNode.insertBefore(existing.firstChild, existing);
      existing.parentNode.removeChild(existing);
      scheduleChange();
      return;
    }
    if (selection.isCollapsed) {
      var empty = document.createElement(tagName.toLowerCase());
      empty.appendChild(document.createTextNode(ZWSP));
      insertInlineAtCaret(empty);
      placeCaretAtEnd(empty);
      scheduleChange();
      return;
    }
    var range = selection.getRangeAt(0);
    var wrapper = document.createElement(tagName.toLowerCase());
    wrapper.appendChild(range.extractContents());
    range.insertNode(wrapper);
    placeCaretAfter(wrapper);
    scheduleChange();
  }

  function insertInlineAtCaret(element) {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      editor.appendChild(element);
      return;
    }
    var range = selection.getRangeAt(0);
    range.deleteContents();
    range.insertNode(element);
    placeCaretAfter(element);
  }

  function placeCaretAfter(node) {
    var tail = document.createTextNode(ZWSP);
    node.parentNode.insertBefore(tail, node.nextSibling);
    var range = document.createRange();
    range.setStart(tail, 1);
    range.collapse(true);
    applyRange(range);
  }

  /* ======================================================================
   * 9. Change notification, decoration, paste
   * ==================================================================== */

  function serializeHtml() {
    return editor.innerHTML.replace(new RegExp(ZWSP, 'g'), '');
  }

  function wordCount() {
    var text = editor.textContent.replace(new RegExp(ZWSP, 'g'), '').trim();
    return text ? text.split(/\s+/).length : 0;
  }

  function scheduleChange() {
    if (suppressChangeEvents) return;
    if (changeTimer) clearTimeout(changeTimer);
    // 250 ms is long enough to coalesce a burst of typing, short enough that the
    // dirty indicator and autosave never feel stale.
    changeTimer = setTimeout(function () {
      changeTimer = null;
      postToHost({ type: 'docChanged', html: serializeHtml(), wordCount: wordCount() });
    }, 250);
  }

  /**
   * Every empty block carries a <br> placeholder so it stays selectable. Once the block has real
   * content that leading placeholder becomes a stray blank line, so drop it. A <br> anywhere else
   * is a deliberate soft break (Shift+Enter) and is left alone.
   */
  function dropPlaceholderBreak(block) {
    if (!block) return;
    var text = (block.textContent || '').split(ZWSP).join('').trim();
    if (!text) return;
    var first = block.firstChild;
    if (first && first.nodeName === 'BR') block.removeChild(first);
  }

  function onInput(event) {
    ensureNotEmpty();
    dropPlaceholderBreak(currentLeaf());
    if (event && event.inputType === 'insertText' && event.data === ' ') {
      if (applyBlockRules()) return;
    }
    applyInlineRules();
    decorate();
    scheduleChange();
  }

  function onSelectionChange() {
    var block = currentLeaf();
    if (block !== activeBlock) {
      // Caret left a block: canonicalise the one we just left. This is the moment
      // Typora "commits" a line, and it is why syntax markers vanish on exit.
      if (activeBlock && editor.contains(activeBlock)) requestBlockRender(activeBlock);
      if (activeBlock) activeBlock.classList.remove('rio-active-block');
      activeBlock = block;
      if (activeBlock) activeBlock.classList.add('rio-active-block');
    }
    reportSelection();
  }

  function reportSelection() {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !editor.contains(selection.anchorNode)) return;
    var block = currentLeaf();
    var tag = block ? block.tagName.toLowerCase() : 'p';
    postToHost({
      type: 'selection',
      bold: queryState('bold') || !!closest(selection.anchorNode, 'STRONG') || !!closest(selection.anchorNode, 'B'),
      italic: queryState('italic') || !!closest(selection.anchorNode, 'EM') || !!closest(selection.anchorNode, 'I'),
      inlineCode: !!closest(selection.anchorNode, 'CODE'),
      headingLevel: /^h[1-6]$/.test(tag) ? Number(tag[1]) : 0,
      blockType: tag
    });
  }

  function queryState(command) {
    try { return document.queryCommandState(command); } catch (e) { return false; }
  }

  function onPaste(event) {
    var clipboard = event.clipboardData;
    if (!clipboard) return;

    var html = clipboard.getData('text/html');
    var text = clipboard.getData('text/plain');
    event.preventDefault();

    if (html) {
      // Untrusted: the host sanitizes and normalises it through the full pipeline.
      hostRequest('sanitizeHtml', { html: html }).then(function (clean) {
        insertHtmlAtCaret(clean || escapeHtml(text));
        scheduleChange();
        decorate();
      });
      // Ask via the dedicated sanitize channel too (host understands both).
      postToHost({ type: 'sanitize', requestId: 'paste', html: html });
      return;
    }

    if (text) {
      // Plain text may itself be Markdown — render it through Markdig.
      if (/[*_#`\[\]>|]/.test(text) && text.indexOf('\n') >= 0) {
        hostRequest('render', { markdown: text }).then(function (rendered) {
          insertHtmlAtCaret(rendered || escapeHtml(text));
          scheduleChange();
          decorate();
        });
      } else {
        insertTextAtCaret(text);
        scheduleChange();
      }
    }
  }

  function insertHtmlAtCaret(html) {
    var selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return;
    var range = selection.getRangeAt(0);
    range.deleteContents();
    var holder = document.createElement('div');
    holder.innerHTML = html;
    var fragment = document.createDocumentFragment();
    var last = null;
    while (holder.firstChild) { last = holder.firstChild; fragment.appendChild(last); }
    range.insertNode(fragment);
    if (last) placeCaretAtEnd(last);
  }

  function escapeHtml(text) {
    var div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
  }

  /* ======================================================================
   * 10. Decoration: syntax highlighting, Mermaid, KaTeX
   * ==================================================================== */

  // Highlighting lives in the shared highlight.js asset, which HTML export inlines too.
  var decorateTimer = null;

  function decorate() {
    if (decorateTimer) clearTimeout(decorateTimer);
    decorateTimer = setTimeout(function () {
      decorateTimer = null;
      var caret = saveCaret();

      // Highlight only code blocks the caret is not sitting in — rewriting the
      // innerHTML of the block being typed in would fight the user for the caret.
      var active = closest(window.getSelection().anchorNode, 'PRE');
      if (window.rioHighlight) window.rioHighlight.applyAll(editor, active);

      renderMermaid();
      renderMath();
      restoreCaret(caret);
    }, 120);
  }

  function renderMermaid() {
    if (!window.mermaid) return;
    var nodes = editor.querySelectorAll('.mermaid:not([data-processed="true"])');
    if (nodes.length === 0) return;

    // Mermaid replaces the node's text with rendered SVG, so the graph source is gone after the
    // first pass. Stash it: without this a re-render (a theme flip) feeds SVG back to the parser
    // and produces "Syntax error in text" — and, worse, saving would write the SVG into the
    // Markdown file, because that is what the HTML -> Markdown pass would read.
    for (var n = 0; n < nodes.length; n++) {
      if (!nodes[n].getAttribute('data-rio-source')) {
        nodes[n].setAttribute('data-rio-source', nodes[n].textContent);
      } else {
        nodes[n].textContent = nodes[n].getAttribute('data-rio-source');
      }
    }

    try {
      window.mermaid.initialize({
        startOnLoad: false,
        securityLevel: 'strict',
        theme: document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'default'
      });
      window.mermaid.run({ nodes: Array.prototype.slice.call(nodes) });
    } catch (e) {
      /* An invalid graph must not break editing — it just stays as text. */
    }
  }

  function renderMath() {
    if (typeof window.renderMathInElement !== 'function') return;
    try {
      window.renderMathInElement(editor, {
        delimiters: [
          { left: '$$', right: '$$', display: true },
          { left: '$', right: '$', display: false },
          { left: '\\(', right: '\\)', display: false },
          { left: '\\[', right: '\\]', display: true }
        ],
        throwOnError: false,
        ignoredTags: ['script', 'noscript', 'style', 'textarea', 'pre', 'code'],
        // Without this, each pass re-processes KaTeX's own output (its MathML annotation still
        // holds the source) and the expression multiplies on every decoration cycle.
        ignoredClasses: ['katex', 'katex-display']
      });
    } catch (e) { /* ignore */ }
  }

  window.addEventListener('rio-mermaid-ready', function () { renderMermaid(); });

  /* ======================================================================
   * 11. Theme
   * ==================================================================== */

  function setTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme === 'dark' ? 'dark' : 'light');
    // Re-render Mermaid so its own palette follows the editor theme.
    var nodes = editor.querySelectorAll('.mermaid[data-processed="true"]');
    for (var i = 0; i < nodes.length; i++) {
      var source = nodes[i].getAttribute('data-rio-source');
      if (source) nodes[i].textContent = source;      // restore before re-parsing
      nodes[i].removeAttribute('data-processed');
    }
    renderMermaid();
    postToHost({ type: 'themeChanged', theme: theme });
  }

  function toggleTheme() {
    setTheme(document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark');
  }

  /* ======================================================================
   * 12. Public API — window.RioEditor
   * The host talks to the engine over window.rio.receive; these functions are
   * the human-facing surface (dev console, WASM interop, embedding).
   * ==================================================================== */

  function setHtml(html) {
    suppressChangeEvents = true;
    editor.innerHTML = html && html.trim() ? html : '<p><br></p>';
    ensureNotEmpty();
    suppressChangeEvents = false;
    activeBlock = null;
    decorate();
    var first = editor.firstElementChild;
    if (first) placeCaretAtEnd(first);

    // Report the new size without going through docChanged: a freshly opened document
    // has a word count but is emphatically not dirty.
    postToHost({ type: 'stats', wordCount: wordCount() });
  }

  var api = {
    /** @returns {Promise<string>} the document as Markdown (converted by the host). */
    getMarkdown: function () {
      return hostRequest('markdown', { html: serializeHtml() });
    },

    /** Replaces the document. The host renders the Markdown with Markdig first. */
    setMarkdown: function (markdown) {
      return hostRequest('render', { markdown: markdown || '' }).then(function (html) {
        setHtml(html);
        postToHost({ type: 'docChanged', html: serializeHtml(), wordCount: wordCount() });
        return html;
      });
    },

    getHtml: serializeHtml,
    setHtml: setHtml,

    applyBold: commands.bold,
    applyItalic: commands.italic,
    applyStrikethrough: commands.strikethrough,
    applyInlineCode: commands.inlineCode,
    applyHeading: commands.heading,
    applyLink: commands.link,
    applyCodeBlock: commands.codeBlock,
    applyQuote: commands.quote,
    applyBulletList: commands.bulletList,
    applyOrderedList: commands.orderedList,
    applyTaskList: commands.taskList,
    applyHorizontalRule: commands.horizontalRule,
    insertTable: commands.table,

    print: function () { try { window.print(); } catch (e) { /* unsupported */ } },
    toggleTheme: toggleTheme,
    setTheme: setTheme,
    focus: function () { editor.focus(); }
  };

  window.RioEditor = api;

  /* ======================================================================
   * 13. Host -> engine message pump
   * ==================================================================== */

  window.rio = {
    receive: function (payload) {
      var message;
      try {
        message = typeof payload === 'string' ? JSON.parse(payload) : payload;
      } catch (e) {
        return;
      }

      switch (message.type) {
        case 'setHtml':
          setHtml(message.html);
          break;

        case 'blockRendered':
          onBlockRendered(message.requestId, message.html);
          break;

        case 'sanitized':
          if (message.requestId === 'paste' && message.html) {
            insertHtmlAtCaret(message.html);
            scheduleChange();
          }
          break;

        case 'hostResponse': {
          var resolve = pendingHostRequests[message.requestId];
          if (resolve) {
            delete pendingHostRequests[message.requestId];
            resolve(message.value || '');
          }
          break;
        }

        case 'request':
          // Host asking the engine for state (used by Save / autosave).
          if (message.request === 'getHtml') {
            postToHost({ type: 'response', requestId: message.requestId, value: serializeHtml() });
          }
          break;

        case 'command': {
          editor.focus();
          var name = message.name;
          if (name === 'heading') commands.heading(message.level);
          else if (name === 'link') commands.link(message.url, message.text);
          else if (name === 'codeBlock') commands.codeBlock(message.language);
          else if (name === 'table') commands.table(message.rows, message.columns);
          else if (typeof commands[name] === 'function') commands[name]();
          break;
        }

        case 'setTheme':
          setTheme(message.theme);
          break;

        case 'toggleTheme':
          toggleTheme();
          break;

        case 'focus':
          editor.focus();
          break;

        case 'print':
          // Fallback PDF route: the platform print dialog offers "Save as PDF".
          try { window.print(); } catch (e) { console.warn('[rio] print unavailable', e); }
          break;
      }
    }
  };

  /* ======================================================================
   * 14. Wire-up
   * ==================================================================== */

  editor.addEventListener('input', onInput);
  editor.addEventListener('keydown', onKeyDown);
  editor.addEventListener('paste', onPaste);
  document.addEventListener('selectionchange', onSelectionChange);

  // Task list checkboxes arrive disabled from Markdig; make them interactive.
  editor.addEventListener('click', function (event) {
    var target = event.target;
    if (target && target.tagName === 'INPUT' && target.type === 'checkbox') {
      target.removeAttribute('disabled');
      if (target.checked) target.setAttribute('checked', 'checked');
      else target.removeAttribute('checked');
      scheduleChange();
    }
  });

  // Drag & drop of files is handled by the host; block the browser default so a
  // dropped .md never navigates the WebView away from the editor.
  editor.addEventListener('dragover', function (e) { e.preventDefault(); });
  editor.addEventListener('drop', function (e) {
    e.preventDefault();
    var text = e.dataTransfer && e.dataTransfer.getData('text/plain');
    if (text) api.setMarkdown(text);
  });

  ensureNotEmpty();
  postToHost({ type: 'ready' });
})();
