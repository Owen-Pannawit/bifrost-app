import { describe, expect, it } from 'vitest';
import { doc, raw, template, toBase64 } from '../src/index.js';

/**
 * The builder is sugar, so the only thing worth testing is that it produces exactly the payload the
 * bridge's DslCompiler parses — sugar that needs its own debugging would not be worth having.
 */

describe('doc() builder', () => {
  it('produces the payload shape DslCompiler parses', () => {
    const payload = doc()
      .text('6205-2RS', { size: 3, bold: true, align: 'center' })
      .text('Lot L2408-0231', { size: 1, align: 'center' })
      .barcode('CODE128', '6205-2RS', { heightDots: 80, moduleWidth: 3 })
      .feed(3)
      .build();

    expect(payload).toEqual({
      tier: 'dsl',
      document: {
        widthDots: undefined,
        elements: [
          { type: 'text', value: '6205-2RS', size: 3, bold: true, align: 'center' },
          { type: 'text', value: 'Lot L2408-0231', size: 1, align: 'center' },
          { type: 'barcode', format: 'CODE128', value: '6205-2RS', heightDots: 80, moduleWidth: 3 },
          { type: 'feed', lines: 3 },
        ],
      },
    });
  });

  it('carries widthDots when the caller pins it', () => {
    expect(doc(576).text('X').build().document.widthDots).toBe(576);
  });

  it('appends a cut element', () => {
    const elements = doc().text('X').cut('PARTIAL').build().document.elements;

    expect(elements[1]).toEqual({ type: 'cut', mode: 'PARTIAL' });
  });

  it('keeps element order, because the printer prints in it', () => {
    // ESC/POS is a sequential model: order on the wire is order on the paper (DES-06 §4.1).
    const elements = doc().text('first').barcode('CODE128', 'x').text('last').build().document.elements;

    expect(elements.map((e) => e.type)).toEqual(['text', 'barcode', 'text']);
  });

  it('emits qr and feed-by-dots in the shape the compiler expects', () => {
    const elements = doc().qr('PN=6205-2RS', { scale: 6, errorCorrection: 'H' }).feedDots(60).build()
      .document.elements;

    expect(elements).toEqual([
      { type: 'qr', value: 'PN=6205-2RS', scale: 6, errorCorrection: 'H' },
      { type: 'feed', dots: 60 },
    ]);
  });

  it('applies a default alignment only where an element did not choose one', () => {
    const elements = doc().align('center').text('centred').text('right', { align: 'right' }).build()
      .document.elements;

    expect(elements[0]).toMatchObject({ align: 'center' });
    expect(elements[1]).toMatchObject({ align: 'right' });
  });

  it('omits options entirely when none were set, rather than sending an empty object', () => {
    expect(doc().text('X').build().options).toBeUndefined();
    expect(doc().text('X').copies(2).cutAfter().build().options).toEqual({ copies: 2, cutAfter: true });
  });
});

describe('template()', () => {
  it('builds a Tier 1 payload', () => {
    expect(template('part-label', { partNo: '6205-2RS', qty: 50 })).toEqual({
      tier: 'template',
      template: 'part-label',
      data: { partNo: '6205-2RS', qty: 50 },
    });
  });
});

describe('raw()', () => {
  it('base64-encodes bytes so the caller never touches the encoding', () => {
    expect(raw('ESCPOS', new Uint8Array([0x1b, 0x40]))).toEqual({
      tier: 'raw',
      language: 'ESCPOS',
      data: 'G0A=',
    });
  });

  it('treats a string as bytes, not as UTF-8 text', () => {
    // '\x1B@' is two bytes — the ESC/POS initialise command. A UTF-8 encoder would make it three,
    // and the printer would receive a command it does not recognise.
    expect(toBase64('\x1B@')).toBe('G0A=');
  });

  it('encodes a payload larger than the argument limit of String.fromCharCode', () => {
    const big = new Uint8Array(70_000).fill(0x41);

    expect(() => toBase64(big)).not.toThrow();
    expect(toBase64(big).length).toBeGreaterThan(90_000);
  });
});
