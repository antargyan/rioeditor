/* ============================================================================
 * RioEditor engine tests
 * ---------------------------------------------------------------------------
 * Runs in a real browser against a real contenteditable. Everything is driven
 * through the engine's public surface - window.RioEditor and window.rio.receive
 * - plus genuine DOM events, so a test fails when a user would notice, not when
 * an internal detail moves.
 * ========================================================================== */
(function () {
  'use strict';

  var results = [];
  var editor = document.getElementById('editor');

  function test(name, fn) {
    try { fn(); results.push({ name: name, ok: true }); }
    catch (e) { results.push({ name: name, ok: false, error: e && e.message ? e.message : String(e) }); }
  }

  function assert(condition, message) {
    if (!condition) throw new Error(message || 'assertion failed');
  }

  function assertHtmlContains(fragment, message) {
    var html = editor.innerHTML;
    assert(html.indexOf(fragment) >= 0,
      (message || 'expected to find ' + fragment) + '\n   actual: ' + html.slice(0, 300));
  }

  /** Replaces the document and puts the caret at the end of the first block. */
  function setContent(html) {
    editor.innerHTML = html;
    var first = editor.firstElementChild || editor;
    caretAtEndOf(first);
    return first;
  }

  function caretAtEndOf(node) {
    var range = document.createRange();
    range.selectNodeContents(node);
    range.collapse(false);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function selectContentsOf(node) {
    var range = document.createRange();
    range.selectNodeContents(node);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  /** Types text into the current block and fires the input event the engine listens for. */
  function type(text, inputType, data) {
    var selection = window.getSelection();
    var range = selection.getRangeAt(0);
    var node = range.startContainer;
    if (node.nodeType !== Node.TEXT_NODE) {
      node = document.createTextNode('');
      range.insertNode(node);
      range.setStart(node, 0);
      range.collapse(true);
      selection.removeAllRanges();
      selection.addRange(range);
    }
    var offset = range.startOffset;
    node.insertData(offset, text);
    var after = document.createRange();
    after.setStart(node, offset + text.length);
    after.collapse(true);
    selection.removeAllRanges();
    selection.addRange(after);
    editor.dispatchEvent(new InputEvent('input', {
      bubbles: true,
      inputType: inputType || 'insertText',
      data: data === undefined ? text : data
    }));
  }

  function pressEnter() {
    editor.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
  }

  function strip(html) { return html.replace(/​/g, ''); }

  // ---------------------------------------------------------------- the engine loaded

  test('exposes its public API', function () {
    assert(typeof window.RioEditor === 'object', 'window.RioEditor missing');
    ['getMarkdown', 'setMarkdown', 'getHtml', 'setHtml', 'applyBold', 'applyItalic',
     'applyHeading', 'applyLink', 'applyCodeBlock', 'applyQuote', 'applyBulletList',
     'applyOrderedList', 'applyTaskList', 'insertTable', 'toggleTheme', 'setTheme', 'focus']
      .forEach(function (fn) {
        assert(typeof window.RioEditor[fn] === 'function', 'missing RioEditor.' + fn);
      });
    assert(window.rio && typeof window.rio.receive === 'function', 'window.rio.receive missing');
  });

  test('survives having no host to talk to', function () {
    // Opened directly rather than inside a WebView, the engine must still work; a thrown
    // exception here would break every command.
    window.RioEditor.setHtml('<p>standalone</p>');
    assertHtmlContains('standalone');
  });

  // ---------------------------------------------------------------- content in and out

  test('setHtml then getHtml returns the content', function () {
    window.RioEditor.setHtml('<h1>Title</h1><p>Body</p>');
    var html = window.RioEditor.getHtml();
    assert(html.indexOf('<h1>Title</h1>') >= 0, 'heading lost: ' + html);
    assert(html.indexOf('Body') >= 0, 'body lost: ' + html);
  });

  test('setHtml with nothing leaves an editable paragraph', function () {
    window.RioEditor.setHtml('');
    assert(editor.children.length > 0, 'editor left with no block to type into');
  });

  test('getHtml strips the zero-width spaces used to park the caret', function () {
    editor.innerHTML = '<p>a​b</p>';
    assert(window.RioEditor.getHtml().indexOf('​') < 0, 'zero-width space leaked into output');
  });

  // ---------------------------------------------------------------- inline input rules

  test('**bold** becomes strong as the closing asterisks are typed', function () {
    setContent('<p></p>');
    type('**bold**');
    assertHtmlContains('<strong>bold</strong>');
  });

  test('`code` becomes an inline code span', function () {
    setContent('<p></p>');
    type('`snippet`');
    assertHtmlContains('<code>snippet</code>');
  });

  test('~~text~~ becomes a strikethrough', function () {
    setContent('<p></p>');
    type('~~gone~~');
    assertHtmlContains('<del>gone</del>');
  });

  test('[text](url) becomes a link with that href', function () {
    setContent('<p></p>');
    type('[site](https://example.com)');
    assertHtmlContains('href="https://example.com"');
    assertHtmlContains('>site<');
  });

  test('inline rules do not fire inside a code block', function () {
    // Text in a fence is literal; transforming it would corrupt the very documents a developer
    // is most likely to be writing.
    setContent('<pre><code></code></pre>');
    var code = editor.querySelector('code');
    caretAtEndOf(code);
    type('**not bold**');
    assert(editor.querySelector('pre strong') === null,
      'emphasis was applied inside a code block: ' + editor.innerHTML);
  });

  // ---------------------------------------------------------------- block input rules

  test('"# " turns the block into a heading', function () {
    setContent('<p></p>');
    type('#');
    type(' ');
    assert(editor.querySelector('h1') !== null, 'no h1 produced: ' + editor.innerHTML);
  });

  test('"### " turns the block into a level-three heading', function () {
    setContent('<p></p>');
    type('###');
    type(' ');
    assert(editor.querySelector('h3') !== null, 'no h3 produced: ' + editor.innerHTML);
  });

  test('"- " starts a bullet list', function () {
    setContent('<p></p>');
    type('-');
    type(' ');
    assert(editor.querySelector('ul li') !== null, 'no list item: ' + editor.innerHTML);
  });

  test('"1. " starts a numbered list', function () {
    setContent('<p></p>');
    type('1.');
    type(' ');
    assert(editor.querySelector('ol li') !== null, 'no ordered list: ' + editor.innerHTML);
  });

  test('"> " starts a block quote', function () {
    setContent('<p></p>');
    type('>');
    type(' ');
    assert(editor.querySelector('blockquote') !== null, 'no blockquote: ' + editor.innerHTML);
  });

  test('"- [ ] " starts a task list item', function () {
    setContent('<p></p>');
    type('- [ ]');
    type(' ');
    assert(editor.querySelector('li input[type=checkbox]') !== null,
      'no checkbox: ' + editor.innerHTML);
  });

  test('a fence followed by Enter opens a code block', function () {
    setContent('<p></p>');
    type('```');
    pressEnter();
    assert(editor.querySelector('pre code') !== null, 'no code block: ' + editor.innerHTML);
  });

  test('three dashes followed by Enter become a rule', function () {
    setContent('<p></p>');
    type('---');
    pressEnter();
    assert(editor.querySelector('hr') !== null, 'no horizontal rule: ' + editor.innerHTML);
  });

  // ---------------------------------------------------------------- toolbar commands

  test('applyHeading converts the current block and toggles back off', function () {
    setContent('<p>Heading text</p>');
    window.RioEditor.applyHeading(2);
    assert(editor.querySelector('h2') !== null, 'no h2: ' + editor.innerHTML);

    caretAtEndOf(editor.querySelector('h2'));
    window.RioEditor.applyHeading(2);
    assert(editor.querySelector('h2') === null, 'second press did not toggle off: ' + editor.innerHTML);
  });

  test('applyHeading keeps the text', function () {
    setContent('<p>Keep me</p>');
    window.RioEditor.applyHeading(1);
    assert(editor.textContent.indexOf('Keep me') >= 0, 'text lost: ' + editor.innerHTML);
  });

  test('applyBulletList converts a paragraph into a list item', function () {
    setContent('<p>An item</p>');
    window.RioEditor.applyBulletList();
    assert(editor.querySelector('ul li') !== null, 'no list: ' + editor.innerHTML);
    assert(editor.textContent.indexOf('An item') >= 0, 'text lost: ' + editor.innerHTML);
  });

  test('applyTaskList produces a checkbox item', function () {
    setContent('<p>A task</p>');
    window.RioEditor.applyTaskList();
    assert(editor.querySelector('li input[type=checkbox]') !== null, 'no checkbox: ' + editor.innerHTML);
  });

  test('applyQuote wraps the block and applying it again unwraps', function () {
    setContent('<p>Quoted</p>');
    window.RioEditor.applyQuote();
    assert(editor.querySelector('blockquote') !== null, 'not quoted: ' + editor.innerHTML);

    caretAtEndOf(editor.querySelector('blockquote p') || editor.querySelector('blockquote'));
    window.RioEditor.applyQuote();
    assert(editor.querySelector('blockquote') === null, 'not unquoted: ' + editor.innerHTML);
  });

  test('applyLink wraps the selection in an anchor', function () {
    setContent('<p>clickable</p>');
    selectContentsOf(editor.querySelector('p'));
    window.RioEditor.applyLink('https://example.com');
    assertHtmlContains('href="https://example.com"');
    assert(editor.textContent.indexOf('clickable') >= 0, 'link text lost: ' + editor.innerHTML);
  });

  test('applyInlineCode wraps the selection and unwraps on a second press', function () {
    setContent('<p>snippet</p>');
    selectContentsOf(editor.querySelector('p'));
    window.RioEditor.applyInlineCode();
    assert(editor.querySelector('code') !== null, 'not wrapped: ' + editor.innerHTML);

    caretAtEndOf(editor.querySelector('code'));
    window.RioEditor.applyInlineCode();
    assert(editor.querySelector('code') === null, 'not unwrapped: ' + editor.innerHTML);
  });

  test('insertTable builds the requested shape', function () {
    setContent('<p></p>');
    window.RioEditor.insertTable(3, 4);
    var table = editor.querySelector('table');
    assert(table !== null, 'no table: ' + editor.innerHTML);
    assert(table.querySelectorAll('thead th').length === 4,
      'expected 4 header cells, got ' + table.querySelectorAll('thead th').length);
    assert(table.querySelectorAll('tbody tr').length === 3,
      'expected 3 body rows, got ' + table.querySelectorAll('tbody tr').length);
  });

  test('applyCodeBlock converts a paragraph and keeps its text', function () {
    setContent('<p>var x = 1;</p>');
    window.RioEditor.applyCodeBlock('');
    assert(editor.querySelector('pre code') !== null, 'no code block: ' + editor.innerHTML);
    assert(editor.textContent.indexOf('var x = 1;') >= 0, 'code text lost: ' + editor.innerHTML);
  });

  test('applyHorizontalRule inserts a rule and leaves somewhere to type', function () {
    setContent('<p>above</p>');
    window.RioEditor.applyHorizontalRule();
    assert(editor.querySelector('hr') !== null, 'no rule: ' + editor.innerHTML);
    assert(editor.querySelector('hr').nextElementSibling !== null,
      'nothing to type into after the rule');
  });

  // ---------------------------------------------------------------- host protocol

  test('setTheme flips the document theme', function () {
    window.rio.receive(JSON.stringify({ type: 'setTheme', theme: 'dark' }));
    assert(document.documentElement.getAttribute('data-theme') === 'dark', 'theme not dark');
    window.rio.receive(JSON.stringify({ type: 'setTheme', theme: 'light' }));
    assert(document.documentElement.getAttribute('data-theme') === 'light', 'theme not light');
  });

  test('toggleTheme alternates', function () {
    window.rio.receive(JSON.stringify({ type: 'setTheme', theme: 'light' }));
    window.rio.receive(JSON.stringify({ type: 'toggleTheme' }));
    assert(document.documentElement.getAttribute('data-theme') === 'dark', 'did not toggle to dark');
    window.rio.receive(JSON.stringify({ type: 'toggleTheme' }));
    assert(document.documentElement.getAttribute('data-theme') === 'light', 'did not toggle back');
  });

  test('the host can mount a document', function () {
    window.rio.receive(JSON.stringify({ type: 'setHtml', html: '<h2>From host</h2>' }));
    assertHtmlContains('<h2>From host</h2>');
  });

  test('the host can drive a command', function () {
    window.rio.receive(JSON.stringify({ type: 'setHtml', html: '<p>commanded</p>' }));
    caretAtEndOf(editor.querySelector('p'));
    // The field is 'level', matching HostMessage.Level on the C# side. Writing 'value' here
    // silently did nothing, which is precisely the class of drift ContractTests now pins.
    window.rio.receive(JSON.stringify({ type: 'command', name: 'heading', level: 3 }));
    assert(editor.querySelector('h3') !== null, 'command not applied: ' + editor.innerHTML);
  });

  test('a malformed host message is ignored rather than fatal', function () {
    var before = editor.innerHTML;
    window.rio.receive('{ not json');
    window.rio.receive(JSON.stringify({ type: 'nonsense' }));
    assert(editor.innerHTML === before, 'editor changed on a bad message');
  });

  test('the host can request the document as html', function () {
    window.rio.receive(JSON.stringify({ type: 'setHtml', html: '<p>requested</p>' }));
    var answered = null;
    var original = window.rioHostChannel;
    window.rioHostChannel = function (json) { answered = JSON.parse(json); };
    window.rio.receive(JSON.stringify({ type: 'request', request: 'getHtml', requestId: 'r1' }));
    window.rioHostChannel = original;
    assert(answered && answered.type === 'response', 'no response posted');
    assert(answered.requestId === 'r1', 'wrong requestId');
    assert(answered.value.indexOf('requested') >= 0, 'response missing content');
  });

  // ---------------------------------------------------------------- checkbox interaction

  test('clicking a task checkbox records the new state in the markup', function () {
    // The engine has to mirror the property onto the attribute, because the attribute is what
    // survives being serialised back to Markdown.
    window.RioEditor.setHtml('<ul><li><input type="checkbox"> todo</li></ul>');
    var box = editor.querySelector('input[type=checkbox]');

    // Click and let the browser do the toggling. Setting .checked first and then clicking
    // flips it straight back, because dispatching a click runs the checkbox's own
    // activation behaviour - which made this test fail against a perfectly correct engine.
    box.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    assert(box.checked, 'click did not check the box');
    assert(box.hasAttribute('checked'), 'checked attribute not set: ' + editor.innerHTML);

    box.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    assert(!box.checked, 'second click did not uncheck the box');
    assert(!box.hasAttribute('checked'), 'checked attribute not cleared: ' + editor.innerHTML);
  });

  // ---------------------------------------------------------------- report

  var failed = results.filter(function (r) { return !r.ok; });
  window.__rioTestResults = { total: results.length, failed: failed.length, results: results };
  document.title = failed.length ? ('FAIL ' + failed.length + '/' + results.length)
                                 : ('PASS ' + results.length);
  var report = document.getElementById('report');
  if (!report) { throw new Error('runner.html must define #report before loading tests.js'); }
  report.innerHTML =
    results.map(function (r) {
      return '<div class="' + (r.ok ? 'pass' : 'fail') + '">' +
             (r.ok ? 'PASS  ' : 'FAIL  ') + r.name +
             (r.ok ? '' : '\n      ' + r.error) + '</div>';
    }).join('');
})();
