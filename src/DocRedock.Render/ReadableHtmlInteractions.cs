namespace DocRedock.Render;

using System.Security.Cryptography;
using System.Text;

internal static class ReadableHtmlInteractions
{
    public static string Script => """
(function () {
  'use strict';
  var HIDE = 'drr-hidden';
  var ROWH = 'drr-row-hidden';
  var COLH = 'drr-col-hidden';
  var SECH = 'drr-sec-hidden';
  var HL = 'drr-hit';
  var PIN = 'drr-pin';
  var TOOLS = 'drr-tools';
  var NAV = 'drr-sheetnav';
  var STATUS = 'drr-status';
  var PRINTING = 'drr-printing';
  var PRINT_ONE = 'drr-print-sheet';
  var MAXCOLS = 256;
  var JA = document.documentElement.lang !== 'en';
  function msg(ja, en) { return JA ? ja : en; }
  function $(id) { return document.getElementById(id); }
  function hashId(raw) { try { return decodeURIComponent(raw); } catch (_) { return raw; } }
  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) { node.className = cls; }
    if (text !== undefined && text !== null) { node.textContent = text; }
    return node;
  }
  function cellSpan(cell, attr, fallback) {
    var v = parseInt(cell.getAttribute(attr), 10);
    return (v > 0) ? v : fallback;
  }
  function rowsOf(table) {
    var list = [], nodes = table.querySelectorAll('tr'), i;
    for (i = 0; i < nodes.length; i++) {
      if (nodes[i].closest('table') === table) { list.push(nodes[i]); }
    }
    return list;
  }
  function colCount(table) {
    var cols = table.querySelectorAll('col'), n = 0, i;
    for (i = 0; i < cols.length; i++) {
      if (cols[i].closest('table') === table) { n++; }
    }
    if (n > 0) { return n; }
    return layout(table).width;
  }
  function layout(table) {
    if (table.drrLay) { return table.drrLay; }
    var rows = rowsOf(table), occupiedUntil = [], cellsByRow = [], width = 0, i, j, k, col;
    table.setAttribute('data-drr-style', table.getAttribute('style') || '');
    for (i = 0; i < rows.length; i++) {
      var cells = [];
      col = 0;
      var kids = rows[i].children;
      for (j = 0; j < kids.length; j++) {
        var c = kids[j];
        if (c.tagName !== 'TD' && c.tagName !== 'TH') { continue; }
        while (occupiedUntil[col] > i) { col++; }
        var span = cellSpan(c, 'colspan', 1), rs = cellSpan(c, 'rowspan', 1);
        if (!c.hasAttribute('data-drr-col')) { c.setAttribute('data-drr-col', String(col)); }
        if (!c.hasAttribute('data-drr-colspan')) { c.setAttribute('data-drr-colspan', String(span)); }
        if (!c.hasAttribute('data-drr-rowspan')) { c.setAttribute('data-drr-rowspan', String(rs)); }
        if (!c.hasAttribute('data-drr-style')) { c.setAttribute('data-drr-style', c.getAttribute('style') || ''); }
        c.drrCol = col;
        c.drrSpan = span;
        c.drrRowspan = rs;
        for (k = 0; k < span; k++) { occupiedUntil[col + k] = i + rs; }
        cells.push(c);
        col += span;
        if (col > width) { width = col; }
      }
      cellsByRow.push(cells);
    }
    var colEls = table.querySelectorAll('col'), n = 0;
    for (i = 0; i < colEls.length; i++) {
      if (colEls[i].closest('table') !== table) { continue; }
      if (!colEls[i].hasAttribute('data-drr-style')) { colEls[i].setAttribute('data-drr-style', colEls[i].getAttribute('style') || ''); }
      n++;
    }
    if (n > width) { width = n; }
    table.drrLay = { rows: rows, cells: cellsByRow, width: width, visible: null, text: null };
    return table.drrLay;
  }
  function visibleCols(table) {
    var lay = layout(table);
    if (!lay.visible) {
      lay.visible = [];
      for (var i = 0; i < lay.width; i++) { lay.visible.push(true); }
    }
    return lay.visible;
  }
  function visibleCount(visible, start, span) {
    var n = 0, i;
    for (i = start; i < start + span && i < visible.length; i++) {
      if (visible[i]) { n++; }
    }
    return n;
  }
  function ensureGroups(table) {
    if (table.drrGroups) { return table.drrGroups; }
    var lay = layout(table), rows = [], i, j;
    for (i = 0; i < lay.rows.length; i++) {
      if (!lay.rows[i].closest('thead')) { rows.push(lay.rows[i]); }
    }
    var iv = [];
    for (i = 0; i < rows.length; i++) { iv.push([i, i]); }
    for (i = 0; i < rows.length; i++) {
      var idx = lay.rows.indexOf(rows[i]);
      var cells = lay.cells[idx];
      for (j = 0; j < cells.length; j++) {
        if (cells[j].drrRowspan > 1) { iv.push([i, i + cells[j].drrRowspan - 1]); }
      }
    }
    iv.sort(function (a, b) { return (a[0] - b[0]) || (a[1] - b[1]); });
    var spans = [], cur = null;
    for (i = 0; i < iv.length; i++) {
      if (cur && iv[i][0] <= cur[1]) {
        if (iv[i][1] > cur[1]) { cur[1] = iv[i][1]; }
      } else {
        cur = [iv[i][0], iv[i][1]];
        spans.push(cur);
      }
    }
    var groups = [], owner = new Array(rows.length);
    for (i = 0; i < spans.length; i++) { groups.push([]); }
    for (i = 0; i < spans.length; i++) {
      for (j = spans[i][0]; j <= spans[i][1] && j < rows.length; j++) {
        if (owner[j] === undefined) { owner[j] = i; groups[i].push(rows[j]); }
      }
    }
    for (i = 0; i < rows.length; i++) {
      rows[i].drrIndex = i;
      rows[i].drrGroup = (owner[i] === undefined) ? i : owner[i];
      if (owner[i] === undefined) { groups.push([rows[i]]); owner[i] = groups.length - 1; rows[i].drrGroup = owner[i]; }
    }
    table.drrGroups = groups;
    return groups;
  }
  function applyLayout(table) {
    var lay = layout(table), visible = visibleCols(table), i, j;
    var colEls = table.querySelectorAll('col'), ci = 0;
    for (i = 0; i < colEls.length; i++) {
      if (colEls[i].closest('table') !== table) { continue; }
      var vis = visible[ci] !== false;
      colEls[i].classList.toggle(COLH, !vis);
      var saved = colEls[i].getAttribute('data-drr-style') || '';
      colEls[i].setAttribute('style', vis ? saved : 'display:none;');
      ci++;
    }
    var visCount = 0;
    for (i = 0; i < visible.length; i++) { if (visible[i]) { visCount++; } }
    for (i = 0; i < lay.rows.length; i++) {
      var cells = lay.cells[i];
      for (j = 0; j < cells.length; j++) {
        var cell = cells[j];
        var shown = visibleCount(visible, cell.drrCol, cell.drrSpan);
        cell.classList.toggle(COLH, shown === 0);
        var origSpan = parseInt(cell.getAttribute('data-drr-colspan'), 10) || 1;
        if (shown === origSpan) {
          if (cell.getAttribute('colspan') !== String(origSpan)) { cell.setAttribute('colspan', String(origSpan)); }
        } else if (shown > 0) {
          cell.setAttribute('colspan', String(shown));
        }
      }
    }
    recomputeWidths(table);
    updateStatus(table);
  }
  function recomputeWidths(table) {
    var lay = layout(table), visible = visibleCols(table), i;
    clearPins(table);
    var colEls = [], all = table.querySelectorAll('col'), k;
    for (i = 0; i < all.length; i++) {
      if (all[i].closest('table') === table) { colEls.push(all[i]); }
    }
    var pcts = [], total = 0, visTotal = 0;
    for (i = 0; i < colEls.length; i++) {
      var original = colEls[i].getAttribute('data-drr-style') || '';
      var widthMatch = /(?:^|;)\s*width\s*:\s*([0-9.]+)/i.exec(original);
      var w = widthMatch ? parseFloat(widthMatch[1]) : 0;
      if (!(w > 0)) { w = 100 / Math.max(colEls.length, 1); }
      pcts.push(w);
      total += w;
      if (visible[i] !== false) { visTotal += w; }
    }
    if (total <= 0) { return; }
    for (i = 0; i < colEls.length; i++) {
      if (visible[i] === false) { continue; }
      var saved = colEls[i].getAttribute('data-drr-style') || '';
      var scaled = visTotal > 0 ? (pcts[i] / visTotal) * 100 : pcts[i];
      var m = /(^|;)\s*width\s*:[^;]*(;|$)/i.exec(saved);
      var base = m ? saved.replace(m[0], '') : saved;
      colEls[i].setAttribute('style', base + 'width:' + scaled.toFixed(4) + '%;');
    }
    var tableStyle = table.getAttribute('data-drr-style') || '';
    var tableWidth = /(?:^|;)\s*width\s*:\s*([0-9.]+)px/i.exec(tableStyle);
    var px = tableWidth ? parseFloat(tableWidth[1]) : 0;
    if (px > 0 && visTotal > 0) {
      var newPx = px * (visTotal / total);
      table.style.width = Math.round(newPx) + 'px';
    }
    var pin = table.drrPin || 0, left = 0;
    table.classList.toggle('drr-managed-pins', table.drrPin !== undefined);
    for (i = 0; i < colEls.length; i++) {
      if (visible[i] === false) { continue; }
      if (i < pin) {
        for (k = 0; k < lay.rows.length; k++) {
          var cells = lay.cells[k];
          for (var c2 = 0; c2 < cells.length; c2++) {
            if (cells[c2].drrCol === i && i + cells[c2].drrSpan <= pin) {
              cells[c2].classList.add(PIN);
              cells[c2].style.left = left + 'px';
            }
          }
        }
      }
      if (px > 0) { left += (pcts[i] / total) * px; }
    }
  }
  function clearPins(table) {
    var lay = layout(table), i, j;
    for (i = 0; i < lay.rows.length; i++) {
      for (j = 0; j < lay.cells[i].length; j++) {
        lay.cells[i][j].classList.remove(PIN);
        lay.cells[i][j].style.left = '';
      }
    }
  }
  function rowText(table, row) {
    var lay = layout(table);
    if (!lay.text) {
      lay.text = new Map();
      for (var i = 0; i < lay.rows.length; i++) {
        lay.text.set(lay.rows[i], (lay.rows[i].textContent || '').toLowerCase());
      }
    }
    var v = lay.text.get(row);
    return v === undefined ? '' : v;
  }
  function updateStatus(table) {
    var lay = layout(table), st = table.drrStatusEl;
    if (!st) { return; }
    var groups = ensureGroups(table), visGroups = 0, i;
    for (i = 0; i < groups.length; i++) {
      if (!groups[i][0].classList.contains(ROWH)) { visGroups++; }
    }
    var visible = visibleCols(table), visCols = 0;
    for (i = 0; i < visible.length; i++) { if (visible[i]) { visCols++; } }
    st.textContent = msg(
      '表示: 行グループ ' + visGroups + '/' + groups.length + ' 列 ' + visCols + '/' + visible.length,
      'Showing: row groups ' + visGroups + '/' + groups.length + ' columns ' + visCols + '/' + visible.length);
  }
  function filterRows(table, query, scope) {
    var groups = ensureGroups(table), q = query.trim().toLowerCase(), i, j;
    if (scope < 0) { scope = null; }
    for (i = 0; i < groups.length; i++) {
      var match = !q, k;
      for (k = 0; k < groups[i].length && !match; k++) {
        var row = groups[i][k];
        if (scope !== null && scope !== undefined) {
          var cells = layout(table).cells[layout(table).rows.indexOf(row)], hit = false;
          for (j = 0; j < cells.length; j++) {
            if (cells[j].drrCol === scope || (cells[j].drrCol < scope && cells[j].drrCol + cells[j].drrSpan > scope)) {
              if ((cells[j].textContent || '').toLowerCase().indexOf(q) !== -1) { hit = true; }
            }
          }
          match = hit;
        } else {
          match = rowText(table, row).indexOf(q) !== -1;
        }
      }
      for (k = 0; k < groups[i].length; k++) {
        groups[i][k].classList.toggle(ROWH, !match);
      }
    }
    updateStatus(table);
  }
  function sections() {
    var out = [], nodes = document.querySelectorAll('section.source-section[data-source-sheet]'), i;
    for (i = 0; i < nodes.length; i++) { out.push(nodes[i]); }
    return out;
  }
  function screenTables() {
    var out = [], nodes = document.querySelectorAll('main.reader-content table, body.workbook-document table'), i;
    for (i = 0; i < nodes.length; i++) {
      if (nodes[i].closest('.print-copy')) { continue; }
      if (out.indexOf(nodes[i]) === -1) { out.push(nodes[i]); }
    }
    return out;
  }
  function clearHighlights(root) {
    var nodes = root.querySelectorAll('.' + HL), i;
    for (i = 0; i < nodes.length; i++) { nodes[i].classList.remove(HL); }
  }
  function globalSearch(query) {
    var q = query.trim().toLowerCase(), secs = sections(), i, j;
    var hitSections = 0, hitCells = 0;
    for (i = 0; i < secs.length; i++) {
      clearHighlights(secs[i]);
      if (!q) { secs[i].classList.remove(SECH); continue; }
      var tables = secs[i].querySelectorAll('table');
      var secHit = (secs[i].drrSearchText || '').indexOf(q) !== -1;
      for (j = 0; j < tables.length; j++) {
        if (tables[j].closest('.print-copy')) { continue; }
        var cells = tables[j].querySelectorAll('td, th'), k;
        for (k = 0; k < cells.length; k++) {
          if ((cells[k].textContent || '').toLowerCase().indexOf(q) !== -1) {
            cells[k].classList.add(HL);
            hitCells++;
            secHit = true;
          }
        }
      }
      if (secHit) {
        secs[i].classList.remove(SECH); hitSections++;
        for (j = 0; j < tables.length; j++) {
          if (!tables[j].closest('.print-copy')) { filterRows(tables[j], '', null); }
        }
      }
      else { secs[i].classList.add(SECH); }
    }
    setGlobalStatus(q ? msg('一致: セクション ' + hitSections + ' / マス ' + hitCells, 'Matches: sections ' + hitSections + ' / cells ' + hitCells) : msg('検索語を入力してください', 'Enter a search term'));
  }
  function setGlobalStatus(text) {
    var st = $('drr-global-status');
    if (st) { st.textContent = text; }
  }
  function restoreTable(table) {
    var lay = layout(table), i, j;
    lay.visible = null;
    delete table.drrPin;
    table.classList.remove('drr-managed-pins');
    clearPins(table);
    var colEls = table.querySelectorAll('col');
    for (i = 0; i < colEls.length; i++) {
      if (colEls[i].closest('table') !== table) { continue; }
      colEls[i].classList.remove(COLH);
      colEls[i].setAttribute('style', colEls[i].getAttribute('data-drr-style') || '');
    }
    for (i = 0; i < lay.rows.length; i++) {
      lay.rows[i].classList.remove(ROWH);
      clearHighlights(lay.rows[i]);
      for (j = 0; j < lay.cells[i].length; j++) {
        var cell = lay.cells[i][j];
        cell.classList.remove(COLH);
        cell.setAttribute('style', cell.getAttribute('data-drr-style') || '');
        cell.setAttribute('colspan', cell.getAttribute('data-drr-colspan'));
      }
    }
    table.setAttribute('style', table.getAttribute('data-drr-style') || '');
    if (table.drrFilterInput) { table.drrFilterInput.value = ''; }
    if (table.drrPinSelect) { table.drrPinSelect.value = '-1'; }
    if (table.drrScopeSelect) { table.drrScopeSelect.value = '-1'; }
    if (table.drrColBoxes) {
      for (i = 0; i < table.drrColBoxes.length; i++) { table.drrColBoxes[i].checked = true; }
    }
    updateStatus(table);
  }
  function resetAll() {
    var tables = screenTables(), secs = sections(), i;
    for (i = 0; i < tables.length; i++) { restoreTable(tables[i]); }
    for (i = 0; i < secs.length; i++) { secs[i].classList.remove(SECH); clearHighlights(secs[i]); }
    var inp = $('drr-search');
    if (inp) { inp.value = ''; }
    setGlobalStatus(msg('初期状態に戻しました', 'Reset to initial state'));
  }
  function printSheet(section) {
    var secs = sections(), i;
    for (i = 0; i < secs.length; i++) { secs[i].removeAttribute('data-drr-print-target'); }
    section.setAttribute('data-drr-print-target', '');
    document.body.classList.add(PRINT_ONE);
    beginSourcePrint();
    try { window.print(); } catch (error) { endPrintOne(); throw error; }
  }
  var printFieldSnapshot = null;
  var printVariantSnapshot = null;
  function beginSourcePrint() {
    if (printFieldSnapshot) { return; }
    printFieldSnapshot = [];
    printVariantSnapshot = [];
    var secs = sections(), pages = [], i, j;
    for (i = 0; i < secs.length; i++) {
      var excluded = document.body.classList.contains(PRINT_ONE) && !secs[i].hasAttribute('data-drr-print-target');
      if (excluded) { secs[i].setAttribute('data-drr-print-excluded', ''); }
      else { secs[i].removeAttribute('data-drr-print-excluded'); }
      if (!excluded) {
        var local = secs[i].querySelectorAll('[data-drr-print-page]');
        for (j = 0; j < local.length; j++) { pages.push(local[j]); }
      }
    }
    var number = 0;
    for (i = 0; i < pages.length; i++) {
      var page = pages[i], localPage = Number(page.getAttribute('data-local-page')) || 1;
      var first = Number(page.getAttribute('data-first-page-number')) || 0;
      if (localPage === 1 && first > 0) { number = first - 1; }
      number++;
      page.classList.toggle('drr-print-first', i === 0);
      var fields = page.querySelectorAll('[data-drr-print-field]');
      for (j = 0; j < fields.length; j++) {
        var field = fields[j];
        printFieldSnapshot.push([field, field.textContent]);
        field.textContent = String((field.getAttribute('data-drr-print-field') === 'pages' ? pages.length : number) + (Number(field.getAttribute('data-page-offset')) || 0));
      }
      var variants = page.querySelectorAll('[data-furniture-variant]');
      var variant = localPage === 1 && page.getAttribute('data-footer-first') === 'true' ? 'first' : number % 2 === 0 && page.getAttribute('data-footer-even') === 'true' ? 'even' : 'odd';
      for (j = 0; j < variants.length; j++) { printVariantSnapshot.push([variants[j], variants[j].hidden]); variants[j].hidden = variants[j].getAttribute('data-furniture-variant') !== variant; }
    }
  }
  function endPrintOne() {
    document.body.classList.remove(PRINT_ONE);
    var secs = sections(), i;
    for (i = 0; i < secs.length; i++) { secs[i].removeAttribute('data-drr-print-target'); secs[i].removeAttribute('data-drr-print-excluded'); }
    if (printFieldSnapshot) {
      for (i = 0; i < printFieldSnapshot.length; i++) { printFieldSnapshot[i][0].textContent = printFieldSnapshot[i][1]; }
      printFieldSnapshot = null;
    }
    if (printVariantSnapshot) {
      for (i = 0; i < printVariantSnapshot.length; i++) { printVariantSnapshot[i][0].hidden = printVariantSnapshot[i][1]; }
      printVariantSnapshot = null;
    }
    var pages = document.querySelectorAll('[data-drr-print-page]');
    for (i = 0; i < pages.length; i++) { pages[i].classList.remove('drr-print-first'); }
  }
  function revealElement(target) {
    if (!target || target.nodeType !== 1) { return; }
    var sec = target.closest('section.source-section');
    if (sec) { sec.classList.remove(SECH); }
    var targetTable = target.closest('table');
    if (targetTable && !targetTable.closest('.print-copy')) {
      restoreTable(targetTable);
    }
    var row = target.closest('tr');
    if (row && row.classList.contains(ROWH)) {
      var table = row.closest('table');
      if (table && !table.closest('.print-copy')) {
        ensureGroups(table);
        var groups = table.drrGroups, i, j;
        for (i = 0; i < groups.length; i++) {
          for (j = 0; j < groups[i].length; j++) {
            if (groups[i][j] === row) {
              for (var k = 0; k < groups[i].length; k++) { groups[i][k].classList.remove(ROWH); }
            }
          }
        }
      } else {
        row.classList.remove(ROWH);
      }
    }
  }
  function buildTools(table) {
    var lay = layout(table), n = lay.width, i;
    var bar = el('div', TOOLS);
    bar.setAttribute('role', 'group');
    bar.setAttribute('aria-label', msg('表ツール', 'Table tools'));
    var filter = el('input');
    filter.type = 'text';
    filter.setAttribute('placeholder', msg('行フィルタ(この表)', 'Filter rows (this table)'));
    filter.className = 'drr-filter-input';
    var scope = el('select', 'drr-scope-select');
    var optAll = el('option', null, msg('全列', 'All columns'));
    optAll.value = '-1';
    scope.appendChild(optAll);
    for (i = 0; i < Math.min(n, MAXCOLS); i++) {
      var opt = el('option', null, msg('列 ' + (i + 1), 'Column ' + (i + 1)));
      opt.value = String(i);
      scope.appendChild(opt);
    }
    var showAll = el('button', 'drr-btn', msg('全行表示', 'Show all rows'));
    showAll.type = 'button';
    var resetBtn = el('button', 'drr-btn', msg('リセット', 'Reset'));
    resetBtn.type = 'button';
    bar.appendChild(filter);
    bar.appendChild(scope);
    bar.appendChild(showAll);
    bar.appendChild(resetBtn);
    var colBar = el('div', 'drr-colbar');
    colBar.appendChild(el('span', 'drr-colbar-label', msg('表示列:', 'Columns:')));
    if (n <= MAXCOLS) {
      var boxes = [];
      for (i = 0; i < n; i++) {
        var box = document.createElement('input');
        box.type = 'checkbox';
        box.checked = true;
        box.drrColIndex = i;
        var lab = el('label', 'drr-col-check');
        lab.appendChild(box);
        lab.appendChild(el('span', null, msg('列' + (i + 1), 'C' + (i + 1))));
        colBar.appendChild(lab);
        boxes.push(box);
      }
      table.drrColBoxes = boxes;
      var std = el('button', 'drr-btn', msg('標準', 'Standard'));
      std.type = 'button';
      var all = el('button', 'drr-btn', msg('全列', 'All columns'));
      all.type = 'button';
      std.addEventListener('click', function () {
        var keep = Math.min(8, n);
        for (var q2 = 0; q2 < boxes.length; q2++) { boxes[q2].checked = q2 < keep; }
        applyColumnBoxes(table, boxes);
      });
      all.addEventListener('click', function () {
        for (var q2 = 0; q2 < boxes.length; q2++) { boxes[q2].checked = true; }
        applyColumnBoxes(table, boxes);
      });
      colBar.appendChild(std);
      colBar.appendChild(all);
      for (i = 0; i < boxes.length; i++) {
        boxes[i].addEventListener('change', function () { applyColumnBoxes(table, boxes); });
      }
    } else {
      colBar.appendChild(el('span', 'drr-colbar-note', msg('列数が ' + MAXCOLS + ' を超えるため列チェックボックスは表示しません(全列保持)', 'More than ' + MAXCOLS + ' columns: checkboxes not created; all columns preserved')));
    }
    bar.appendChild(colBar);
    var pinSel = el('select', 'drr-pin-select');
    for (i = -1; i <= 3; i++) {
      var popt = el('option', null, i < 0 ? msg('元の固定設定', 'Source freeze') : msg('先頭' + i + '列固定', 'Pin first ' + i));
      popt.value = String(i);
      pinSel.appendChild(popt);
    }
    pinSel.addEventListener('change', function () {
      var choice = parseInt(pinSel.value, 10);
      table.drrPin = choice < 0 ? undefined : choice || 0;
      if (table.drrPin === 0) { clearPins(table); }
      applyLayout(table);
    });
    bar.appendChild(el('span', 'drr-colbar-label', msg('固定:', 'Pin:')));
    bar.appendChild(pinSel);
    table.drrPinSelect = pinSel;
    table.drrScopeSelect = scope;
    var status = el('span', STATUS);
    table.drrStatusEl = status;
    bar.appendChild(status);
    table.drrFilterInput = filter;
    filter.addEventListener('input', function () {
      var sc = parseInt(scope.value, 10);
      filterRows(table, filter.value, isNaN(sc) ? null : sc);
    });
    scope.addEventListener('change', function () {
      var sc = parseInt(scope.value, 10);
      filterRows(table, filter.value, isNaN(sc) ? null : sc);
    });
    showAll.addEventListener('click', function () { filter.value = ''; filterRows(table, '', null); });
    resetBtn.addEventListener('click', function () { restoreTable(table); });
    if (table.parentNode) { table.parentNode.insertBefore(bar, table); }
    updateStatus(table);
  }
  function applyColumnBoxes(table, boxes) {
    var visible = visibleCols(table), checked = 0, i;
    for (i = 0; i < boxes.length; i++) { if (boxes[i].checked) { checked++; } }
    if (checked === 0) {
      for (i = 0; i < boxes.length; i++) {
        if (!boxes[i].checked) { boxes[i].checked = true; break; }
      }
    }
    for (i = 0; i < boxes.length; i++) { visible[i] = boxes[i].checked; }
    applyLayout(table);
  }
  function buildSheetNav(section, index, list) {
    var nav = el('div', NAV);
    nav.setAttribute('role', 'group');
    nav.setAttribute('aria-label', msg('シート操作', 'Sheet controls'));
    var name = section.getAttribute('data-source-sheet') || '';
    nav.appendChild(el('span', 'drr-sheet-name', name));
    var prev = el('button', 'drr-btn', msg('前のシート', 'Prev sheet'));
    prev.type = 'button';
    prev.disabled = index === 0;
    var next = el('button', 'drr-btn', msg('次のシート', 'Next sheet'));
    next.type = 'button';
    next.disabled = index === list.length - 1;
    var contents = el('button', 'drr-btn', msg('目次', 'Contents'));
    contents.type = 'button';
    var print = el('button', 'drr-btn', msg('このシートを印刷', 'Print this sheet'));
    print.type = 'button';
    prev.addEventListener('click', function () {
      if (index > 0) { list[index - 1].classList.remove(SECH); list[index - 1].scrollIntoView(); }
    });
    next.addEventListener('click', function () {
      if (index < list.length - 1) { list[index + 1].classList.remove(SECH); list[index + 1].scrollIntoView(); }
    });
    contents.addEventListener('click', function () {
      var contentsNav = document.querySelector('.reader-nav');
      if (contentsNav) { contentsNav.scrollIntoView(); }
      setGlobalStatus(msg('シート数: ' + list.length, 'Sheets: ' + list.length));
    });
    print.addEventListener('click', function () { printSheet(section); });
    nav.appendChild(prev);
    nav.appendChild(next);
    nav.appendChild(contents);
    nav.appendChild(print);
    if (section.firstChild) { section.insertBefore(nav, section.firstChild); }
    else { section.appendChild(nav); }
  }
  function wireGlobal() {
    var search = $('drr-search'), btn = $('drr-search-btn'), reset = $('drr-reset-btn'), printAll = $('drr-print-all-btn');
    if (btn) { btn.addEventListener('click', function () { globalSearch(search ? search.value : ''); }); }
    if (search) {
      search.addEventListener('keydown', function (ev) {
        if (ev.key === 'Enter') { globalSearch(search.value); }
      });
    }
    if (reset) { reset.addEventListener('click', function () { resetAll(); }); }
    if (printAll) { printAll.addEventListener('click', function () { beginSourcePrint(); try { window.print(); } catch (error) { endPrintOne(); throw error; } }); }
  }
  function init() {
    if (!document.body || !document.body.classList.contains('workbook-document')) { return; }
    var secs = sections(), i;
    for (i = 0; i < secs.length; i++) { secs[i].drrSearchText = (secs[i].textContent || '').toLowerCase(); }
    var tables = screenTables();
    for (i = 0; i < tables.length; i++) { buildTools(tables[i]); }
    for (i = 0; i < secs.length; i++) { buildSheetNav(secs[i], i, secs); }
    wireGlobal();
    window.addEventListener('beforeprint', function () { beginSourcePrint(); document.body.classList.add(PRINTING); });
    window.addEventListener('afterprint', function () {
      document.body.classList.remove(PRINTING);
      endPrintOne();
    });
    var printMedia = window.matchMedia ? window.matchMedia('print') : null;
    if (printMedia && printMedia.addEventListener) { printMedia.addEventListener('change', function (event) { if (!event.matches) { document.body.classList.remove(PRINTING); endPrintOne(); } }); }
    document.addEventListener('click', function (ev) {
      var a = ev.target && ev.target.closest ? ev.target.closest('a[href^="#"]') : null;
      if (!a) { return; }
      var id = hashId(a.getAttribute('href').slice(1));
      revealElement($(id));
    });
    window.addEventListener('hashchange', function () {
      var id = hashId(location.hash.replace(/^#/, ''));
      if (id) { revealElement($(id)); }
    });
    if (location.hash) {
      var id2 = hashId(location.hash.replace(/^#/, ''));
      revealElement($(id2));
    }
    setGlobalStatus(msg('準備完了', 'Ready'));
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
""";

    public static string Css => """
@media print {
  .drr-tools, .drr-sheetnav, .drr-global-toolbar, .drr-status { display: none !important; }
  section.source-section.drr-sec-hidden { display: block !important; }
  tr.drr-row-hidden { display: table-row !important; }
  td.drr-col-hidden, th.drr-col-hidden { display: table-cell !important; }
  col.drr-col-hidden { display: table-column !important; }
  .drr-hit { background: transparent !important; }
  body.drr-print-sheet section.source-section { display: none !important; }
  body.drr-print-sheet section.source-section[data-drr-print-target] { display: block !important; }
}
.drr-global-toolbar { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; margin: 0.5rem 0; padding: 0.5rem; border: 1px solid #bbb; border-radius: 4px; background: var(--panel,#f7f7f7); color:var(--ink,#18212b); }
.drr-tools { display: flex; flex-wrap: wrap; gap: 0.4rem; align-items: center; margin: 0.4rem 0; padding: 0.35rem; border: 1px dashed #999; border-radius: 4px; background: var(--panel,#f7f7f7); color:var(--ink,#18212b); }
.drr-sheetnav { display: flex; flex-wrap: wrap; gap: 0.4rem; align-items: center; margin: 0.3rem 0; }
.drr-status, .drr-global-status { font-size: 0.85em; color: var(--muted,#444); }
.drr-col-check { margin-right: 0.35rem; white-space: nowrap; font-size: 0.85em; }
.drr-colbar-label { font-size: 0.85em; font-weight: 600; }
.drr-colbar-note { font-size: 0.85em; color: #7a3e00; }
.drr-row-hidden { display: none; }
.drr-sec-hidden { display: none; }
td.drr-col-hidden, th.drr-col-hidden { display: none; }
col.drr-col-hidden { display: none; }
.drr-hit { outline: 2px solid #d97706; outline-offset: -2px; }
.drr-pin { position: sticky; z-index: 2; }
.direct-document .drr-managed-pins .frozen-column { position:static; }
.direct-document .drr-managed-pins .drr-pin { position:sticky; }
.drr-hidden { display: none; }
""";

    public static string Controls(bool japanese)
    {
        var searchLabel = japanese ? "検索" : "Search";
        var resetLabel = japanese ? "リセット" : "Reset";
        var printLabel = japanese ? "全体を印刷" : "Print all";
        var placeholder = japanese ? "ワークブック全体を検索" : "Search the whole workbook";
        var toolbarLabel = japanese ? "文書ツール" : "Document tools";
        return "<div class=\"drr-global-toolbar\" role=\"toolbar\" aria-label=\"" + toolbarLabel + "\">"
            + "<input type=\"text\" id=\"drr-search\" placeholder=\"" + placeholder + "\" />"
            + "<button type=\"button\" id=\"drr-search-btn\">" + searchLabel + "</button>"
            + "<button type=\"button\" id=\"drr-reset-btn\">" + resetLabel + "</button>"
            + "<button type=\"button\" id=\"drr-print-all-btn\">" + printLabel + "</button>"
            + "<span id=\"drr-global-status\" class=\"drr-global-status\" role=\"status\" aria-live=\"polite\"></span>"
            + "</div>";
    }

    public static string CspHash => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script)));
}
