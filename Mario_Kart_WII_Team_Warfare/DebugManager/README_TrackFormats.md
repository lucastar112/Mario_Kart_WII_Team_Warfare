# KMP / LE-CODE track readers

This increment adds read-only readers for Mario Kart Wii track archives.

## Loading a track archive

```csharp
using DebugManager.Services.TrackFormats;

LoadedTrackArchive track = TrackArchiveLoader.Load(pathToSzs);
Console.WriteLine($"Track ID: {track.TrackId}");
Console.WriteLine($"KMP sections: {track.Kmp.Sections.Count}");
Console.WriteLine($"LEX sections: {track.Lex?.Sections.Count ?? 0}");
```

`TrackArchiveLoader` reads the filename (without extension) as the track ID, opens the Yaz0-compressed U8 archive, reads `course.kmp`, and reads `course.lex` if present.

## KMP support

- Validates the `RKMD` header, declared file length, section table, offsets, section headers, and duplicate section names.
- Supports the normal big-endian KMP header and section format.
- Splits entries for fixed-size sections whose sizes are established: KTPT, ENPT, ENPH, ITPT, ITPH, CKPT, CKPH, GOBJ, AREA, JGPT, CNPT, MSPT and STGI.
- Keeps the complete raw payload for every section. POTI and CAME remain raw because their internal layouts are variable / need more detailed parsing.
- `KmpEntryDecoder` provides big-endian float/vector and integer helpers.

## LEX support

- Validates the `LE-X` header and follows the section chain using its declared first-section offset.
- Checks section boundaries, 4-byte alignment, duplicate section IDs and the terminating zero section.
- Recognizes the currently documented IDs (FEAT, SET1, CANN, CTDN, HIPT, RITP, TEST, DEV1 and `----`).
- Retains every section's raw bytes, including unknown IDs, so future LE-CODE extensions are not silently discarded.

## Validation against the supplied Luigi Circuit archive (`000.szs`)

The archive was successfully Yaz0-decompressed and its U8 file table inspected. The included KMP has 15 sections, file size 15,208 bytes, header length 0x4C and version 0x9D8:

| Section | Entry count |
|---|---:|
| KTPT | 1 |
| ENPT | 106 |
| ENPH | 1 |
| ITPT | 112 |
| ITPH | 12 |
| CKPT | 46 |
| CKPH | 3 |
| GOBJ | 97 |
| POTI | 24 |
| AREA | 18 |
| CAME | 16 |
| JGPT | 8 |
| CNPT | 0 |
| MSPT | 0 |
| STGI | 1 |

The 188-byte `course.lex` contains SET1, CANN, CTDN, HIPT and RITP, followed by the zero terminator. All five sections are retained as raw payloads.

## Deliberate limitations

This is a reader, not an editor: it does not modify or rewrite KMP/LEX files. Unknown KMP sections remain accessible as raw sections. POTI and CAME entry decoding, and semantic decoding of LE-CODE payloads, are left for a subsequent step so we do not invent undocumented field meanings. The archive reader currently handles Yaz0-compressed U8 archives and uncompressed U8 data.
