// Smoke + security tests for the raw-HTML sanitize policy (SANITIZE_OPTS) that
// html-render.js applies before injecting embedded HTML into the editor. Runs on
// the built-in Node test runner against real DOMPurify bound to a jsdom window —
// the same library the browser uses — so a regression that reopens an XSS hole or
// drops a legitimate presentational tag fails the build.
//
//   node --test        (or: npm test)

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import createDOMPurify from 'dompurify';
import { SANITIZE_OPTS } from '../src/sanitize.js';

const { window } = new JSDOM('');
const DOMPurify = createDOMPurify(window);
const clean = (dirty) => DOMPurify.sanitize(dirty, SANITIZE_OPTS);

// ---- XSS vectors must be stripped ----

test('strips <script>', () => {
  const out = clean('<p>hi</p><script>alert(1)</script>');
  assert.ok(!/script/i.test(out), out);
  assert.ok(out.includes('hi'));
});

test('strips inline event handlers (onerror)', () => {
  const out = clean('<img src="x" onerror="alert(1)">');
  assert.ok(!/onerror/i.test(out), out);
});

test('strips javascript: URLs', () => {
  const out = clean('<a href="javascript:alert(1)">click</a>');
  assert.ok(!/javascript:/i.test(out), out);
});

test('strips <iframe> and <object>/<embed>', () => {
  const out = clean('<iframe src="evil"></iframe><object data="x"></object><embed src="y">');
  assert.ok(!/iframe|<object|<embed/i.test(out), out);
});

test('strips <base>, <meta>, <link>, <style>, <form>', () => {
  const out = clean('<base href="//evil"><meta http-equiv="refresh" content="0">' +
    '<link rel="stylesheet" href="//evil"><style>*{}</style><form action="//evil"></form>');
  assert.ok(!/<base|<meta|<link|<style|<form/i.test(out), out);
});

test('drops srcset (candidate for tracking/exfil variants)', () => {
  const out = clean('<img src="a.png" srcset="b.png 2x">');
  assert.ok(!/srcset/i.test(out), out);
  assert.ok(/src="a\.png"/.test(out), out);
});

// ---- legitimate presentational HTML must survive ----

test('keeps a centered logo (<p align><img width height>) — the motivating case', () => {
  const out = clean('<p align="center"><img src="logo.png" width="200" height="80" alt="Logo"></p>');
  assert.ok(/align="center"/.test(out), out);
  assert.ok(/<img[^>]+src="logo\.png"/.test(out), out);
  assert.ok(/width="200"/.test(out) && /height="80"/.test(out), out);
});

test('keeps inline formatting and small tables', () => {
  const out = clean('<sub>x</sub><sup>2</sup><br>' +
    '<table><tr><td colspan="2" valign="top">c</td></tr></table>');
  assert.ok(/<sub>x<\/sub>/.test(out), out);
  assert.ok(/<sup>2<\/sup>/.test(out), out);
  assert.ok(/<br\s*\/?>/.test(out), out);
  assert.ok(/colspan="2"/.test(out) && /valign="top"/.test(out), out);
});

test('keeps anchor target (open-in-new)', () => {
  const out = clean('<a href="https://example.com" target="_blank">link</a>');
  assert.ok(/target="_blank"/.test(out), out);
  assert.ok(/href="https:\/\/example\.com"/.test(out), out);
});
