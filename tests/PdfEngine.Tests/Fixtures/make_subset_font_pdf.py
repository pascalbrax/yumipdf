"""Builds subset-font.pdf, the fixture for editing existing text (python make_subset_font_pdf.py).

One page, three lines of text:
  1. "Ciao mondo" in an *embedded subset* TrueType font (the PDF26 Sans font cut down to exactly the
     glyphs of that text, like a real producer does) - typing a letter outside it ("Q", "w", ...) has no glyph.
  2. "Riga standard" in the standard, non-embedded Helvetica (every WinAnsi character is available).
  3. "Seconda riga" in the same embedded subset (glyphs: S, e, c, o, n, d, a, r, i, g, space - some overlap line 1).
Also a rectangle and an image-free page otherwise, so the object list is small and predictable.
"""
import io
import os
import zlib

from fontTools import subset
from fontTools.ttLib import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONT = os.path.join(HERE, '..', '..', '..', 'src', 'PdfEngine', 'Fonts', 'PDF26Sans-Regular.ttf')

used = sorted(set('Ciao mondo' + 'Seconda riga'))
options = subset.Options()
options.layout_features = []
options.notdef_outline = True
options.name_IDs = ['*']
options.hinting = False
font = TTFont(FONT)
sub = subset.Subsetter(options)
sub.populate(text=''.join(used))
sub.subset(font)
buf = io.BytesIO()
font.save(buf)
font_data = buf.getvalue()

full = TTFont(FONT)
upem = full['head'].unitsPerEm
hmtx = full['hmtx']
cmap = full.getBestCmap()
widths = []
for code in range(32, 127):
    gname = cmap.get(code)
    widths.append(round(hmtx[gname][0] * 1000 / upem) if gname else 0)

objs = []


def add(body):
    objs.append(body)
    return len(objs)


def stream(dict_body, data, compress=True):
    if compress:
        data = zlib.compress(data)
        dict_body += ' /Filter /FlateDecode'
    return ('<< %s /Length %d >>\nstream\n' % (dict_body, len(data))).encode('latin-1') + data + b'\nendstream'


catalog = add(b'')           # 1
pages = add(b'')             # 2
page = add(b'')              # 3
contents = add(b'')          # 4
f_embedded = add(b'')        # 5
descriptor = add(b'')        # 6
fontfile = add(b'')          # 7
f_helv = add(b'')            # 8

objs[catalog - 1] = b'<< /Type /Catalog /Pages 2 0 R >>'
objs[pages - 1] = b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>'
objs[page - 1] = (b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R '
                  b'/Resources << /Font << /F1 5 0 R /F2 8 0 R >> >> >>')
content = (b'BT /F1 24 Tf 72 700 Td (Ciao mondo) Tj ET\n'
           b'BT /F2 14 Tf 72 650 Td (Riga standard) Tj ET\n'
           b'BT /F1 18 Tf 72 600 Td 0.8 0 0 rg (Seconda riga) Tj ET\n')
objs[contents - 1] = stream('', content)
objs[f_embedded - 1] = ('<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+PDF26Sans /FirstChar 32 /LastChar 126 '
                        '/Widths [%s] /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>'
                        % ' '.join(map(str, widths))).encode('latin-1')
objs[descriptor - 1] = (b'<< /Type /FontDescriptor /FontName /ABCDEF+PDF26Sans /Flags 32 /FontBBox [-200 -250 1200 950] '
                        b'/ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>')
objs[fontfile - 1] = stream('/Length1 %d' % len(font_data), font_data)
objs[f_helv - 1] = b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>'

out = bytearray(b'%PDF-1.5\n%\xe2\xe3\xcf\xd3\n')
offsets = []
for i, body in enumerate(objs, 1):
    offsets.append(len(out))
    out += ('%d 0 obj\n' % i).encode() + body + b'\nendobj\n'
xref = len(out)
out += ('xref\n0 %d\n0000000000 65535 f \n' % (len(objs) + 1)).encode()
for off in offsets:
    out += ('%010d 00000 n \n' % off).encode()
out += ('trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n' % (len(objs) + 1, xref)).encode()
with open(os.path.join(HERE, 'subset-font.pdf'), 'wb') as f:
    f.write(out)
print('subset-font.pdf', len(out), 'bytes; embedded font', len(font_data), 'bytes')
