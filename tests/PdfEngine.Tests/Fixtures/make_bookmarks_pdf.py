"""Regenerates bookmarks.pdf: a 4-page document with a hand-written outline covering the cases
GetBookmarks has to handle (direct destination, GoTo action, URI action, open/closed entries,
a non-ASCII title). Run: python make_bookmarks_pdf.py"""


def utf16_hex(text):
    return "<FEFF" + text.encode("utf-16-be").hex().upper() + ">"


objs = {}
objs[1] = b"<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R >>"
objs[2] = b"<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>"
for n in (3, 4, 5, 6):
    objs[n] = b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>"

# Outline: Capitolo 1 (open) { Sezione 1.1, Sezione 1.2 (GoTo action) }, Capitolo 2 (closed) { Città e più }, Sito web (URI)
objs[7] = b"<< /Type /Outlines /First 8 0 R /Last 12 0 R /Count 5 >>"
objs[8] = ("<< /Title (Capitolo 1) /Parent 7 0 R /Next 11 0 R /First 9 0 R /Last 10 0 R /Count 2 /Dest [3 0 R /Fit] >>").encode()
objs[9] = ("<< /Title (Sezione 1.1) /Parent 8 0 R /Next 10 0 R /Dest [4 0 R /Fit] >>").encode()
objs[10] = ("<< /Title (Sezione 1.2) /Parent 8 0 R /Prev 9 0 R /A << /S /GoTo /D [5 0 R /Fit] >> >>").encode()
objs[11] = ("<< /Title (Capitolo 2) /Parent 7 0 R /Prev 8 0 R /Next 12 0 R /First 13 0 R /Last 13 0 R /Count -1 /Dest [6 0 R /Fit] >>").encode()
objs[12] = ("<< /Title (Sito web) /Parent 7 0 R /Prev 11 0 R /A << /S /URI /URI (https://example.com) >> >>").encode()
objs[13] = ("<< /Title %s /Parent 11 0 R /Dest [6 0 R /Fit] >>" % utf16_hex("Città e più")).encode()

out = bytearray(b"%PDF-1.7\n")
offsets = {}
for num in sorted(objs):
    offsets[num] = len(out)
    out += b"%d 0 obj\n" % num + objs[num] + b"\nendobj\n"
xref = len(out)
n = len(objs) + 1
out += b"xref\n0 %d\n0000000000 65535 f \n" % n
for num in sorted(objs):
    out += b"%010d 00000 n \n" % offsets[num]
out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF" % (n, xref)
open("bookmarks.pdf", "wb").write(out)
